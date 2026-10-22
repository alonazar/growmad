using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Data;
using NomadInbox.Infrastructure.Settings;
using NomadInbox.Infrastructure.YouTube;

namespace NomadInbox.Infrastructure.Ingest;

/// <summary>Downloads queued videos one at a time, separately from summaries (a download can take many minutes).</summary>
public sealed class VideoDownloadWorker(
    IDbContextFactory<NomadInboxDbContext> dbFactory,
    SettingsService settingsService,
    VideoDownloader downloader,
    VideoTrigger trigger,
    ILogger<VideoDownloadWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var didWork = false;
            try
            {
                didWork = await DownloadNextAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Video download failed");
            }
            if (!didWork) await trigger.WaitAsync(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    public async Task<bool> DownloadNextAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Needs the video's metadata (title, channel, date) for the file name, so wait until it has been fetched.
        var link = await db.Links
            .Where(l => l.VideoStatus == VideoStatus.Queued && l.Kind == LinkKind.YouTubeVideo && l.FetchedAt != null)
            .OrderBy(l => l.Id).FirstOrDefaultAsync(ct);
        if (link is null) return false;

        var settings = await settingsService.GetAsync(ct);
        var email = await db.Emails.AsNoTracking().SingleOrDefaultAsync(e => e.Id == link.InboxEmailId, ct);
        logger.LogInformation("Downloading video {Url} (up to {Height}p)", link.Url, settings.MaxVideoHeight);

        VideoDownloadResult result;
        try
        {
            result = await downloader.DownloadAsync(link, email, settings, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            result = new VideoDownloadResult(VideoStatus.Failed, null, null, ex.Message);
        }

        link.VideoStatus = result.Status;
        link.VideoPath = result.RelativePath;
        link.VideoBytes = result.Bytes;
        link.VideoError = result.Error;
        if (result.Status != VideoStatus.Downloaded)
            logger.LogWarning("Video {Url} not downloaded ({Status}): {Error}", link.Url, result.Status, result.Error);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
