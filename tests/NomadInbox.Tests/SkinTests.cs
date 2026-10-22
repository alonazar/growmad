using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NomadInbox.Core.Appearance;
using NomadInbox.Core.Domain;

namespace NomadInbox.Tests;

public partial class SkinTests
{
    private static readonly string WebRoot = Path.Combine(FindRepoRoot(), "src", "NomadInbox.Web", "wwwroot");
    private static readonly string SkinsDir = Path.Combine(WebRoot, "skins");

    // Every skin must set these (see the header of skins/_base.css).
    private static readonly string[] RequiredTokens =
    [
        "font-body", "font-heading", "font-mono",
        "bg", "surface", "surface-2", "text", "muted", "border", "link", "accent", "on-accent", "focus",
        "topbar-bg", "topbar-text", "topbar-border",
        "sidebar-bg", "sidebar-bg-2", "sidebar-text", "brand-bg",
        "sidebar-active-bg", "sidebar-active-text", "sidebar-marker",
    ];

    // Text on background; each must reach WCAG AA for normal text (4.5:1) in light and in dark.
    private static readonly (string Text, string Background)[] TextPairs =
    [
        ("text", "bg"), ("text", "surface"), ("text", "surface-2"),
        ("muted", "bg"), ("muted", "surface"),
        ("link", "bg"), ("link", "surface"),
        ("on-accent", "accent"),
        ("topbar-text", "topbar-bg"), ("muted", "topbar-bg"),
        ("sidebar-text", "sidebar-bg"), ("sidebar-text", "sidebar-bg-2"),
        ("sidebar-active-text", "sidebar-active-bg"), ("sidebar-active-text", "brand-bg"),
    ];

    public static TheoryData<string> SkinIds => new(SkinCatalog.All.Select(s => s.Id));

    [GeneratedRegex(@"--nomad-(?<name>[a-z0-9-]+)\s*:\s*(?<value>[^;]+);")]
    private static partial Regex Declaration();

    [GeneratedRegex(@"url\(""(?<url>\.\./fonts/[^""]+)""\)")]
    private static partial Regex FontUrl();

    [Fact]
    public void Catalog_ids_are_unique_slugs_and_include_the_default()
    {
        var ids = SkinCatalog.All.Select(s => s.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Matches("^[a-z][a-z0-9-]*$", id));
        Assert.Contains(SkinCatalog.DefaultId, ids);
    }

    [Theory]
    [InlineData(null, "classic")]
    [InlineData("", "classic")]
    [InlineData("removed-skin", "classic")]
    [InlineData("EMBER", "ember")]
    [InlineData("signal", "signal")]
    public void Get_falls_back_to_the_default_skin(string? id, string expected)
    {
        Assert.Equal(expected, SkinCatalog.Get(id).Id);
    }

    [Fact]
    public void Every_skin_file_is_registered()
    {
        var files = Directory.GetFiles(SkinsDir, "*.css")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !name!.StartsWith('_'));

        Assert.All(files, name => Assert.Contains(SkinCatalog.All, s => s.Id == name));
    }

    [Theory]
    [MemberData(nameof(SkinIds))]
    public void Skin_file_exists_and_sets_every_token(string id)
    {
        var css = ReadSkin(id);

        Assert.Contains($"[data-skin=\"{id}\"]", css);
        var tokens = Tokens(css);
        Assert.All(RequiredTokens, token => Assert.True(tokens.ContainsKey(token), $"{id}.css does not set --nomad-{token}"));
    }

    [Theory]
    [MemberData(nameof(SkinIds))]
    public void Text_is_readable_in_light_and_dark(string id)
    {
        var tokens = Tokens(ReadSkin(id));
        var failures = new List<string>();

        foreach (var (text, background) in TextPairs)
        {
            var (textLight, textDark) = Colors(tokens, text);
            var (bgLight, bgDark) = Colors(tokens, background);
            foreach (var (mode, fg, bg) in new[] { ("light", textLight, bgLight), ("dark", textDark, bgDark) })
            {
                var ratio = Contrast(fg, bg);
                if (ratio < 4.5)
                    failures.Add($"{mode}: --nomad-{text} {fg} on --nomad-{background} {bg} is {ratio:0.00}:1");
            }
        }

        Assert.True(failures.Count == 0, $"{id}.css contrast below 4.5:1:\n" + string.Join("\n", failures));
    }

    [Theory]
    [MemberData(nameof(SkinIds))]
    public void Fonts_are_self_hosted_with_their_license(string id)
    {
        foreach (Match match in FontUrl().Matches(ReadSkin(id)))
        {
            var path = Path.GetFullPath(Path.Combine(SkinsDir, match.Groups["url"].Value));
            Assert.True(File.Exists(path), $"{id}.css points to a missing font: {match.Groups["url"].Value}");

            var license = Path.Combine(Path.GetDirectoryName(path)!, "OFL.txt");
            Assert.True(File.Exists(license), $"No OFL.txt next to {path}");
            Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", File.ReadAllText(license));
        }
        Assert.DoesNotContain("fonts.googleapis.com", ReadSkin(id));
    }

    [Fact]
    public void Defaults_are_the_classic_skin_following_the_device()
    {
        var settings = new InboxSettings();

        Assert.Equal(SkinCatalog.DefaultId, settings.Skin);
        Assert.Equal(ColorMode.System, settings.ColorMode);
    }

    [Fact]
    public async Task Saving_the_settings_form_keeps_the_appearance()
    {
        await using var test = await TestDatabase.CreateAsync();
        var loadedEarlier = await test.Settings.GetAsync(); // e.g. the Settings page, opened before a change

        await test.Settings.SaveAppearanceAsync("ember", ColorMode.Dark);
        loadedEarlier.Folder = "Archive";
        await test.Settings.SaveAsync(loadedEarlier, newPassword: null);

        var saved = await test.Settings.GetAsync();
        Assert.Equal("Archive", saved.Folder);
        Assert.Equal("ember", saved.Skin);
        Assert.Equal(ColorMode.Dark, saved.ColorMode);
    }

    [Fact]
    public async Task Unknown_skins_are_saved_as_the_default()
    {
        await using var test = await TestDatabase.CreateAsync();

        await test.Settings.SaveAppearanceAsync("no-such-skin", ColorMode.Light);

        Assert.Equal(SkinCatalog.DefaultId, (await test.Settings.GetAsync()).Skin);
    }

    [Fact]
    public async Task Existing_settings_get_the_default_look_when_upgrading()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.Settings.SaveAsync(new InboxSettings { Folder = "Archive" }, newPassword: null);

        await using (var db = await test.Factory.CreateDbContextAsync())
        {
            var migrator = db.GetService<IMigrator>();
            var appearance = db.Database.GetMigrations().Single(m => m.EndsWith("_Appearance"));
            var before = db.Database.GetMigrations().TakeWhile(m => m != appearance).Last();
            await migrator.MigrateAsync(before); // drops the new columns
            await migrator.MigrateAsync(); // adds them back to the existing row
        }

        var settings = await test.Settings.GetAsync();
        Assert.Equal("Archive", settings.Folder);
        Assert.Equal(SkinCatalog.DefaultId, settings.Skin);
        Assert.Equal(ColorMode.System, settings.ColorMode);
    }

    private static string ReadSkin(string id)
    {
        var path = Path.Combine(SkinsDir, $"{id}.css");
        Assert.True(File.Exists(path), $"Skin '{id}' is registered but {path} is missing");
        return File.ReadAllText(path);
    }

    private static Dictionary<string, string> Tokens(string css) =>
        Declaration().Matches(css).ToDictionary(m => m.Groups["name"].Value, m => m.Groups["value"].Value.Trim());

    /// <summary>A token's light and dark color: light-dark(#light, #dark), or one #hex for both.</summary>
    private static (string Light, string Dark) Colors(Dictionary<string, string> tokens, string name)
    {
        var value = tokens[name];
        var match = Regex.Match(value, @"^light-dark\(\s*(#[0-9A-Fa-f]{6})\s*,\s*(#[0-9A-Fa-f]{6})\s*\)$");
        if (match.Success) return (match.Groups[1].Value, match.Groups[2].Value);
        Assert.Matches("^#[0-9A-Fa-f]{6}$", value); // contrast is only checked on plain colors
        return (value, value);
    }

    private static double Contrast(string a, string b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int offset)
        {
            var c = int.Parse(hex.AsSpan(offset, 2), NumberStyles.HexNumber) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "NomadInbox.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("NomadInbox.slnx not found above " + AppContext.BaseDirectory);
    }
}
