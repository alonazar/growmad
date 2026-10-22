using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Ingest;

namespace NomadInbox.Web.Components.Shared;

/// <summary>Formatting helpers shared by the pages. Times are shown in the server's time zone (TZ in compose.yaml).</summary>
public static class Display
{
    public static string Local(DateTimeOffset? value) =>
        value is { } v ? v.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";

    public static string Ago(DateTimeOffset? value)
    {
        if (value is not { } v) return "never";
        var delta = DateTimeOffset.UtcNow - v;
        var future = delta < TimeSpan.Zero;
        delta = delta.Duration();

        var text = delta.TotalSeconds < 45 ? "a few seconds"
            : delta.TotalMinutes < 90 ? $"{Math.Max(1, (int)Math.Round(delta.TotalMinutes))} min"
            : delta.TotalHours < 36 ? $"{(int)Math.Round(delta.TotalHours)} h"
            : $"{(int)Math.Round(delta.TotalDays)} days";
        return future ? $"in {text}" : $"{text} ago";
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    public static string BadgeClass(EmailStatus status) => status switch
    {
        EmailStatus.Pending => "text-bg-warning",
        EmailStatus.Processing => "text-bg-info",
        EmailStatus.Done => "text-bg-success",
        EmailStatus.Failed => "text-bg-danger",
        _ => "text-bg-secondary",
    };

    public static (string Label, string Css) Describe(PollerState state) => state switch
    {
        PollerState.Starting => ("Starting", "text-bg-secondary"),
        PollerState.NotConfigured => ("Not configured", "text-bg-secondary"),
        PollerState.Disabled => ("Paused", "text-bg-secondary"),
        PollerState.Idle => ("Waiting", "text-bg-success"),
        PollerState.Polling => ("Checking mail…", "text-bg-info"),
        PollerState.Error => ("Error", "text-bg-danger"),
        _ => (state.ToString(), "text-bg-secondary"),
    };
}
