using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NomadInbox.Infrastructure.YouTube;

/// <summary>
/// YouTube changes often and old yt-dlp versions stop working, so the standalone binary in the image updates
/// itself (<c>yt-dlp -U</c>) at startup and once a day. Elsewhere (a system package) it only logs the version.
/// </summary>
public sealed class YtDlpMaintenance(YtDlp ytDlp, ILogger<YtDlpMaintenance> logger) : BackgroundService
{
    public static string? Version { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (ytDlp.Executable == YtDlp.ImagePath)
                {
                    var update = await ytDlp.RunAsync(["-U"], null, TimeSpan.FromMinutes(3), stoppingToken);
                    var summary = update.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
                    if (update.ExitCode == 0) logger.LogInformation("yt-dlp update check: {Result}", summary);
                    else logger.LogWarning("yt-dlp update failed: {Error}", YtDlp.ErrorMessage(update));
                }

                var version = await ytDlp.RunAsync(["--version"], null, TimeSpan.FromSeconds(30), stoppingToken);
                Version = version.ExitCode == 0 ? version.Output.Trim() : null;
                logger.LogInformation("yt-dlp {Version} at {Path}", Version ?? "(not working)", ytDlp.Executable);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("yt-dlp isn't available: {Message}", ex.Message);
            }
            await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
        }
    }
}
