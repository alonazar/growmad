using NomadInbox.Core.Appearance;

namespace NomadInbox.Web.Components;

/// <summary>The saved skin and light/dark choice, as App.razor renders them on &lt;html&gt;.</summary>
public sealed record UiAppearance(Skin Skin, ColorMode Mode)
{
    public string ModeAttribute => ModeName(Mode);

    /// <summary>Bootstrap's data-bs-theme; null in System mode, where appearance.js sets it from the device.</summary>
    public string? BootstrapTheme => Mode switch
    {
        ColorMode.Light => "light",
        ColorMode.Dark => "dark",
        _ => null,
    };

    public static string ModeName(ColorMode mode) => mode.ToString().ToLowerInvariant();

    /// <summary>The top bar's toggle steps through the modes in this order.</summary>
    public static ColorMode Next(ColorMode mode) => mode switch
    {
        ColorMode.System => ColorMode.Light,
        ColorMode.Light => ColorMode.Dark,
        _ => ColorMode.System,
    };
}
