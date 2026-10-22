namespace NomadInbox.Core.Appearance;

/// <param name="Id">Lowercase slug; the skin's stylesheet is wwwroot/skins/{Id}.css and selects [data-skin="{Id}"].</param>
public sealed record Skin(string Id, string Name, string Description, string Fonts);

/// <summary>
/// The skins the web app offers. To add one: write wwwroot/skins/{id}.css (every token from _base.css,
/// each as light-dark(light, dark)) and add a line here. The tests check the file, its tokens and contrast.
/// </summary>
public static class SkinCatalog
{
    public const string DefaultId = "classic";

    public static IReadOnlyList<Skin> All { get; } =
    [
        new(DefaultId, "Classic", "The original look: navy-to-plum sidebar, clean and neutral.", "System fonts"),
        new("basecamp", "Basecamp", "Topo map and trail gear: olive and sand, with blaze-orange trail markers.", "Barlow · Barlow Condensed"),
        new("ember", "Ember", "Campfire and cabin at night: warm charcoal with an ember glow.", "Bitter · Source Sans 3"),
        new("signal", "Signal", "Ham radio console by night, field logbook by day.", "IBM Plex Sans · IBM Plex Mono"),
    ];

    /// <summary>The skin with this id, or the default one for an unknown or removed id.</summary>
    public static Skin Get(string? id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}

public enum ColorMode
{
    /// <summary>Follow each device's light/dark setting.</summary>
    System,
    Light,
    Dark,
}
