using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.Ingest;
using NomadInbox.Infrastructure.Pages;
using NomadInbox.Infrastructure.Web;

namespace NomadInbox.Tests;

public class PageContentProcessorTests
{
    private static readonly Uri PageUrl = new("https://example.com/guides/water");

    [Fact]
    public void Keeps_structure_and_inline_formatting_but_not_scripts_styles_or_classes()
    {
        const string html = """
            <div class="wrap" style="color:red"><h2 id="x">Steps</h2>
            <p onclick="evil()">Boil <b>all</b> water for <i>one</i> <strong>minute</strong>.</p>
            <script>alert(1)</script><iframe src="https://ads.example/x"></iframe><form><input></form>
            <ul><li>First</li></ul><blockquote>Quote</blockquote></div>
            """;

        var result = PageContentProcessor.Prepare(html, PageUrl).Html;

        Assert.Contains("<h2>Steps</h2>", result);
        Assert.Contains("<p>Boil <b>all</b> water for <i>one</i> <strong>minute</strong>.</p>", result);
        Assert.Contains("<ul><li>First</li></ul>", result);
        Assert.Contains("<blockquote>Quote</blockquote>", result);
        Assert.DoesNotContain("script", result);
        Assert.DoesNotContain("iframe", result);
        Assert.DoesNotContain("onclick", result);
        Assert.DoesNotContain("style=", result);
        Assert.DoesNotContain("class=", result);
        Assert.DoesNotContain("<input", result);
    }

    [Fact]
    public void Lazy_images_srcset_and_picture_resolve_to_the_best_absolute_image()
    {
        const string html = """
            <img src="data:image/gif;base64,R0lGOD" data-src="/img/lazy.jpg" alt="Lazy">
            <img src="small.jpg" srcset="small.jpg 400w, /img/large.jpg 1600w, medium.jpg 800w" sizes="100vw">
            <picture><source srcset="/img/pic-1x.webp 1x, /img/pic-2x.webp 2x"><img src="/img/pic.jpg"></picture>
            <noscript><img src="/img/fallback.jpg"></noscript>
            """;

        var prepared = PageContentProcessor.Prepare(html, PageUrl);

        Assert.Equal(
            ["https://example.com/img/lazy.jpg", "https://example.com/img/large.jpg", "https://example.com/img/pic-2x.webp"],
            prepared.Assets.Select(a => a.Url));
        Assert.DoesNotContain("srcset", prepared.Html);
        Assert.DoesNotContain("fallback", prepared.Html);
    }

    [Fact]
    public void YouTube_embeds_become_markers_with_a_thumbnail_and_other_videos_keep_their_sources()
    {
        const string html = """
            <iframe src="https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ?rel=0"></iframe>
            <video poster="/p.jpg"><source src="/clip.mp4" type="video/mp4"></video>
            """;

        var prepared = PageContentProcessor.Prepare(html, PageUrl);

        Assert.Equal(["dQw4w9WgXcQ"], prepared.YouTubeIds);
        Assert.Contains("data-nomad-youtube=\"dQw4w9WgXcQ\"", prepared.Html);
        Assert.Contains(prepared.Assets, a => a.Url == "https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg");
        Assert.Contains(prepared.Assets, a => a.Url == "https://example.com/clip.mp4" && a.Kind == AssetKind.Video);
        Assert.Contains(prepared.Assets, a => a.Url == "https://example.com/p.jpg" && a.Kind == AssetKind.Image);
    }

    [Fact]
    public void Links_are_recorded_once_and_become_markers_and_in_page_anchors_stay()
    {
        const string html = """
            <p><a href="/guides/filters?utm_source=x">Filters</a> and <a href="https://example.com/guides/filters#cleaning">cleaning</a>,
            <a href="#steps">jump</a>, <a href="https://example.com/guides/water#top">top</a>, <a href="mailto:me@example.com">mail</a></p>
            """;

        var prepared = PageContentProcessor.Prepare(html, PageUrl);

        var link = Assert.Single(prepared.Links);
        Assert.Equal("https://example.com/guides/filters", link.Url);
        Assert.Equal("Filters", link.Text);
        Assert.Equal(2, prepared.Html.Split("href=\"nomad-link:0\"").Length - 1);
        Assert.Contains("href=\"#steps\"", prepared.Html);
        Assert.Contains("href=\"#top\"", prepared.Html);
        Assert.Contains("href=\"mailto:me@example.com\"", prepared.Html);
    }

    [Fact]
    public void Finalize_points_media_at_local_files_and_flags_what_was_not_saved()
    {
        var prepared = PageContentProcessor.Prepare("""<img src="/a.jpg" alt="Kept"><img src="/b.jpg" alt="Lost"><video src="/v.mp4"></video>""", PageUrl);

        var html = PageContentProcessor.Finalize(prepared.Html, new Dictionary<string, string> { ["https://example.com/a.jpg"] = "abc.jpg" });

        Assert.Contains("src=\"nomad-asset:abc.jpg\"", html);
        Assert.Contains("class=\"nomad-missing-media\" href=\"https://example.com/b.jpg\"", html);
        Assert.Contains("[Image: Lost: not saved", html);
        Assert.Contains("[Video: not saved", html);
    }

    [Theory]
    [InlineData("a.jpg 400w, b.jpg 1200w, c.jpg 800w", "b.jpg")]
    [InlineData("a.jpg 1x, b.jpg 2x", "b.jpg")]
    [InlineData("only.jpg", "only.jpg")]
    [InlineData("", null)]
    public void Picks_the_largest_srcset_candidate(string srcset, string? expected)
    {
        Assert.Equal(expected, PageContentProcessor.BestFromSrcset(srcset));
    }
}

/// <summary>Saving, rendering, exporting and deleting pages end to end, against a local web server.</summary>
public sealed class PageArchiveFlowTests : IAsyncLifetime
{
    private readonly TestWebServer _server = new();
    private readonly PageFetcher _fetcher = new();
    private readonly string _root = Directory.CreateTempSubdirectory("pages-").FullName;
    private TestDatabase _db = default!;
    private PageStore _store = default!;
    private PageRenderer _renderer = default!;
    private PageService _pages = default!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _store = new PageStore(Path.Combine(_root, "data"), Path.Combine(_root, "export"));
        _renderer = new PageRenderer(_db.Factory, _store);
        var exporter = new PageExporter(_db.Factory, _renderer, _store, _db.Settings, NullLogger<PageExporter>.Instance);
        var archiver = new PageArchiver(_fetcher, _store, new TextExtractor(), NullLogger<PageArchiver>.Instance);
        _pages = new PageService(_db.Factory, _db.Settings, archiver, _store, exporter, new PageTrigger(), new LinkTrigger(),
            NullLogger<PageService>.Instance);
        // The test server is on 127.0.0.1.
        await _db.Settings.SaveAsync(new InboxSettings { AllowPrivateNetworkLinks = true }, null);

        _server.Pages["/img/photo.jpg"] = (200, "image/jpeg", Enumerable.Repeat((byte)7, 2048).ToArray());
        _server.Add("/guide", "text/html", Article("Water guide", """
            <p>Boil <b>all</b> water for <i>one minute</i> before drinking, longer at altitude. Let it cool covered.</p>
            <p><img src="/img/photo.jpg" alt="Pot"></p>
            <p>See also <a href="/filters">ceramic filters</a> and <a href="https://elsewhere.example/x">another site</a>.</p>
            """));
        _server.Add("/filters", "text/html", Article("Ceramic filters", """
            <p>Scrub the <b>ceramic</b> element gently with a soft brush and never use soap on it. Rinse and dry.</p>
            <p>Back to the <a href="/guide">water guide</a>.</p>
            """));
    }

    public async Task DisposeAsync()
    {
        _fetcher.Dispose();
        await _server.DisposeAsync();
        await _db.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Saves_content_media_and_links_and_renders_through_the_template()
    {
        var emailId = await AddEmailAsync(["archive"], _server.Url("/guide"));
        var settings = await _db.Settings.GetAsync();
        await using (var db = await _db.Factory.CreateDbContextAsync())
            Assert.Equal(1, await _pages.QueueForEmailAsync(await db.Emails.Include(e => e.Links).SingleAsync(), settings, default));

        Assert.True(await _pages.ArchiveNextAsync(default));

        var page = await LoadAsync(_server.Url("/guide"));
        Assert.Equal(PageStatus.Done, page.Status);
        Assert.Equal("Water guide", page.Title);
        Assert.Equal(emailId, page.InboxEmailId);
        var asset = Assert.Single(page.Assets);
        Assert.NotNull(_store.ResolveAsset(asset.FileName!));
        Assert.Equal([_server.Url("/filters"), "https://elsewhere.example/x"], page.Links.OrderBy(l => l.Position).Select(l => l.Url));

        var html = (await _renderer.RenderAsync(page.Id, PageView.App, settings))!;
        Assert.Contains("<title>Water guide</title>", html);
        Assert.Contains("Boil <b>all</b> water for <i>one minute</i>", html);
        Assert.Contains($"src=\"/pages/assets/{asset.FileName}\"", html);
        var filtersLink = page.Links.Single(l => l.Url == _server.Url("/filters"));
        Assert.Contains($"href=\"/pages/links/{filtersLink.Id}\"", html); // not saved yet
        Assert.Contains("nomad-not-saved", html);
    }

    [Fact]
    public async Task Saving_a_linked_page_lights_up_links_in_both_pages_and_the_export()
    {
        await AddEmailAsync(["archive"], _server.Url("/guide"));
        var guideId = await _pages.QueueAsync(_server.Url("/guide"), null, null, null, default);
        await _pages.ArchiveNextAsync(default);

        Assert.Equal(1, await _pages.SaveLinkedPagesAsync(guideId, anySite: false, default)); // same site only
        await _pages.ArchiveNextAsync(default);
        var filters = await LoadAsync(_server.Url("/filters"));
        Assert.Equal(guideId, filters.SavedFromPageId);

        var settings = await _db.Settings.GetAsync();
        var guideHtml = (await _renderer.RenderAsync(guideId, PageView.App, settings))!;
        Assert.Contains($"href=\"/pages/{filters.Id}\"", guideHtml);
        var filtersHtml = (await _renderer.RenderAsync(filters.Id, PageView.App, settings))!;
        Assert.Contains($"href=\"/pages/{guideId}\"", filtersHtml);

        // The exported copy uses relative links between the page folders.
        var export = Path.Combine(_root, "export");
        var guideFile = Path.Combine(export, "pages", PageRenderer.ExportFolder(guideId, "Water guide"), "index.html");
        Assert.Contains($"href=\"../{PageRenderer.ExportFolder(filters.Id, "Ceramic filters")}/index.html\"", await File.ReadAllTextAsync(guideFile));
        Assert.Contains("../../assets/", await File.ReadAllTextAsync(guideFile));
        Assert.True(File.Exists(Path.Combine(export, "style.css")));
        Assert.Contains("Ceramic filters", await File.ReadAllTextAsync(Path.Combine(export, "index.html")));
    }

    [Fact]
    public async Task Deleting_removes_content_unshared_media_and_the_exported_folder()
    {
        var id = await _pages.QueueAsync(_server.Url("/guide"), null, null, null, default);
        await _pages.ArchiveNextAsync(default);
        var file = (await LoadAsync(_server.Url("/guide"))).Assets.Single().FileName!;

        await _pages.DeleteAsync(id, default);

        Assert.Null(_store.ResolveAsset(file));
        Assert.False(File.Exists(_store.ContentPathFor(id)));
        Assert.False(Directory.Exists(Path.Combine(_root, "export", "pages", PageRenderer.ExportFolder(id, "Water guide"))));
        await using var db = await _db.Factory.CreateDbContextAsync();
        Assert.Empty(await db.Pages.ToListAsync());
    }

    [Fact]
    public async Task Untagged_emails_and_missing_pages()
    {
        await AddEmailAsync([], _server.Url("/guide"));
        var settings = await _db.Settings.GetAsync();
        await using (var db = await _db.Factory.CreateDbContextAsync())
            Assert.Equal(0, await _pages.QueueForEmailAsync(await db.Emails.Include(e => e.Links).SingleAsync(), settings, default));

        await _pages.QueueAsync(_server.Url("/does-not-exist"), null, null, null, default);
        await _pages.ArchiveNextAsync(default);
        var failed = await LoadAsync(_server.Url("/does-not-exist"));
        Assert.Equal(PageStatus.Failed, failed.Status);
        Assert.Contains("404", failed.Error);
        Assert.Null(await _renderer.RenderAsync(failed.Id, PageView.App, settings));
    }

    [Fact]
    public async Task Custom_templates_are_used_and_broken_ones_are_reported()
    {
        var id = await _pages.QueueAsync(_server.Url("/guide"), null, null, null, default);
        await _pages.ArchiveNextAsync(default);
        var settings = await _db.Settings.GetAsync();

        var custom = await _renderer.RenderAsync(id, PageView.App, settings, template: "<h1>{{ page.title | html.escape }}</h1>{{ page.link_count }}");
        Assert.Equal("<h1>Water guide</h1>2", custom);

        Assert.NotEmpty(PageRenderer.Validate("{{ if page.title }}no end"));
        await Assert.ThrowsAsync<PageTemplateException>(() => _renderer.RenderAsync(id, PageView.App, settings, template: "{{ for x in }}"));
        Assert.Empty(PageRenderer.Validate(PageRenderer.DefaultTemplate));
    }

    private static string Article(string title, string body) =>
        $"<html><head><title>{title}</title></head><body><nav><a href=\"/\">Home</a></nav><article><h1>{title}</h1>{body}" +
        string.Concat(Enumerable.Repeat("<p>More detail about this topic so the reader view recognises an article here.</p>", 6)) +
        "</article><footer>Footer links</footer></body></html>";

    private async Task<int> AddEmailAsync(List<string> tags, string url)
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        var email = new InboxEmail
        {
            MessageId = Guid.NewGuid().ToString(), Folder = "INBOX", FromAddress = "me@gmail.com", Subject = "NOMAD: x",
            Status = EmailStatus.Done, Tags = tags, Category = "Survival/Water",
        };
        email.Links.Add(new EmailLink { Url = url, Kind = LinkKind.Web });
        db.Emails.Add(email);
        await db.SaveChangesAsync();
        return email.Id;
    }

    private async Task<ArchivedPage> LoadAsync(string url)
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        return await db.Pages.Include(p => p.Assets).Include(p => p.Links).SingleAsync(p => p.Url == url);
    }
}
