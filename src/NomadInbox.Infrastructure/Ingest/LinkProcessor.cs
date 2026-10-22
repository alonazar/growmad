using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Ai;
using NomadInbox.Infrastructure.Data;
using NomadInbox.Infrastructure.Settings;
using NomadInbox.Infrastructure.Web;
using NomadInbox.Infrastructure.YouTube;

namespace NomadInbox.Infrastructure.Ingest;

public sealed record LinkOutcome(int Fetched, int Summarized, int Failed)
{
    public bool DidWork => Fetched + Summarized + Failed > 0;
}

/// <summary>
/// Works through links in two steps: fetch the content (Pending → Fetched), then summarize it with NOMAD's
/// Ollama (Fetched → Done). Links wait in Fetched while the AI isn't set up or reachable.
/// <list type="bullet">
/// <item>Web pages: readable text (headless browser for JavaScript-built pages).</item>
/// <item>YouTube videos: metadata, chapters and a transcript from the captions.</item>
/// <item>YouTube channels and playlists: description and recent videos. An email tagged <c>#follow</c> follows the channel.</item>
/// </list>
/// </summary>
public sealed class LinkProcessor(
    IDbContextFactory<NomadInboxDbContext> dbFactory,
    SettingsService settingsService,
    PageFetcher fetcher,
    ReadableText readable,
    Summarizer summarizer,
    IOllamaApi ollama,
    ILogger<LinkProcessor> logger,
    IPageRenderer? renderer = null,
    IYouTubeClient? youTube = null,
    FollowService? follows = null,
    VideoDownloader? videos = null,
    VideoTrigger? videoTrigger = null)
{
    public const int FetchBatch = 5;
    public const int SummaryBatch = 3;
    public const int MaxSummaryAttempts = 3;
    public const int RecentVideosListed = 15;
    private const int MaxTranscriptChars = 200_000;

    public async Task<LinkOutcome> RunOnceAsync(CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);
        int fetched = 0, summarized = 0, failed = 0;

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var youTubeEnabled = youTube is not null;
            var toFetch = await db.Links
                .Where(l => l.Status == LinkStatus.Pending && (l.Kind == LinkKind.Web || youTubeEnabled))
                .OrderBy(l => l.Id).Take(FetchBatch).ToListAsync(ct);
            var emails = await EmailsForAsync(db, toFetch, ct);

            foreach (var link in toFetch)
            {
                var email = emails.GetValueOrDefault(link.InboxEmailId);
                var ok = link.Kind switch
                {
                    LinkKind.YouTubeVideo => await FetchVideoAsync(link, email, settings, ct),
                    LinkKind.YouTubeChannel or LinkKind.YouTubePlaylist => await FetchChannelAsync(link, email, ct),
                    _ => await FetchAsync(link, settings, ct),
                };
                if (ok) fetched++; else failed++;
                await db.SaveChangesAsync(ct);
                if (link.VideoStatus == VideoStatus.Queued) videoTrigger?.Signal();
            }
        }

        if (settings.IsAiConfigured && await IsOllamaUpAsync(settings, ct))
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var toSummarize = await db.Links
                .Where(l => l.Status == LinkStatus.Fetched)
                .OrderBy(l => l.Id).Take(SummaryBatch).ToListAsync(ct);

            var emails = await EmailsForAsync(db, toSummarize, ct);

            foreach (var link in toSummarize)
            {
                if (await SummarizeAsync(link, emails.GetValueOrDefault(link.InboxEmailId), settings, ct)) summarized++;
                else if (link.Status == LinkStatus.Failed) failed++;
                await db.SaveChangesAsync(ct);
            }
        }

        var outcome = new LinkOutcome(fetched, summarized, failed);
        if (outcome.DidWork) logger.LogInformation("Links: {Outcome}", outcome);
        return outcome;
    }

    private async Task<bool> FetchAsync(EmailLink link, InboxSettings settings, CancellationToken ct)
    {
        link.Attempts++;
        try
        {
            var page = await fetcher.FetchAsync(link.Url, settings.AllowPrivateNetworkLinks, ct);
            link.FinalUrl = page.FinalUrl.AbsoluteUri == link.Url ? null : page.FinalUrl.AbsoluteUri;
            link.ContentType = page.ContentType;
            link.FetchedAt = DateTimeOffset.UtcNow;

            var content = readable.Extract(page);
            if (content is null)
            {
                link.Status = LinkStatus.Skipped;
                link.Error = $"Not a readable page ({page.ContentType}).";
                return true;
            }

            if (page.ContentType is "text/html" or "application/xhtml+xml"
                && content.Text.Length < ReadableText.ThinPageChars && settings.RenderJavaScriptPages && renderer is not null)
                content = await RenderAsync(link, settings, content, ct);

            link.Title = content.Title;
            link.SiteName = content.SiteName;
            link.ContentText = content.Text;
            if (string.IsNullOrWhiteSpace(content.Text))
            {
                link.Status = LinkStatus.Skipped;
                link.Error = content.Note ?? "No text found on the page.";
                return true;
            }

            link.Status = LinkStatus.Fetched;
            link.Error = content.Note;
            link.Attempts = 0; // attempts now count summary tries
            logger.LogInformation("Fetched {Url}: \"{Title}\", {Chars} characters", link.Url, link.Title, content.Text.Length);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Fetching {Url} failed: {Message}", link.Url, ex.Message);
            link.Status = LinkStatus.Failed;
            link.Error = ex is LinkFetchException ? ex.Message : $"Fetch failed: {ex.Message}";
            return false;
        }
    }

    private async Task<bool> FetchVideoAsync(EmailLink link, InboxEmail? email, InboxSettings settings, CancellationToken ct)
    {
        link.Attempts++;
        try
        {
            var video = await youTube!.GetVideoAsync(link.Url, settings.TranscriptLanguageList, ct);
            link.ExternalId = video.Id;
            link.Title = video.Title;
            link.SiteName = "YouTube";
            link.Author = video.Channel;
            link.AuthorId = video.ChannelId;
            link.AuthorUrl = video.ChannelUrl;
            link.PublishedAt = video.PublishedAt;
            link.DurationSeconds = video.DurationSeconds;
            link.Description = video.Description;
            link.Chapters = video.Chapters.Count == 0 ? null
                : string.Join('\n', video.Chapters.Select(c => $"{VttTranscript.Stamp(c.StartSeconds)} {c.Title}"));
            link.FetchedAt = DateTimeOffset.UtcNow;

            if (!string.IsNullOrWhiteSpace(video.Transcript))
            {
                link.ContentText = video.Transcript.Length > MaxTranscriptChars ? video.Transcript[..MaxTranscriptChars] : video.Transcript;
                link.ContentSource = video.TranscriptSource;
            }
            else
            {
                link.ContentText = video.Description; // summarize what there is
                link.ContentSource = "Video description (no transcript)";
            }

            link.Error = video.Note;
            link.Status = string.IsNullOrWhiteSpace(link.ContentText) ? LinkStatus.Skipped : LinkStatus.Fetched;
            if (link.Status == LinkStatus.Skipped) link.Error = "The video has neither captions nor a description.";
            link.Attempts = 0;
            logger.LogInformation("Fetched video {Url}: \"{Title}\" by {Channel}, {Source}, {Chars} characters",
                link.Url, video.Title, video.Channel, link.ContentSource, link.ContentText?.Length ?? 0);

            if (link.VideoStatus == VideoStatus.None && videos is not null
                && VideoDownloader.ShouldDownload(settings.VideoDownloadMode, email?.Tags ?? []))
                link.VideoStatus = VideoStatus.Queued;

            if (HasFollowTag(email) && video.ChannelId is not null && follows is not null)
                await follows.FollowAsync(video.ChannelId, video.Channel ?? video.ChannelId,
                    video.ChannelUrl ?? $"https://www.youtube.com/channel/{video.ChannelId}", email!.Category, email.Tags, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Fetching video {Url} failed: {Message}", link.Url, ex.Message);
            link.Status = LinkStatus.Failed;
            link.Error = $"YouTube: {ex.Message}";
            return false;
        }
    }

    private async Task<bool> FetchChannelAsync(EmailLink link, InboxEmail? email, CancellationToken ct)
    {
        link.Attempts++;
        try
        {
            var channel = await youTube!.GetChannelAsync(link.Url, RecentVideosListed, ct);
            link.ExternalId = channel.Id;
            link.Title = channel.Title;
            link.SiteName = "YouTube";
            link.Author = channel.Uploader;
            link.AuthorId = channel.ChannelId ?? (channel.IsPlaylist ? null : channel.Id);
            link.AuthorUrl = channel.Url;
            link.Description = channel.Description;
            link.ContentText = string.Join('\n', channel.Videos.Select(v =>
                $"- {v.Title}{(v.DurationSeconds is { } d ? $" ({VttTranscript.Stamp(d)})" : "")}{(v.Views is { } n ? $", {n:N0} views" : "")}"));
            link.ContentSource = $"{(channel.IsPlaylist ? "Playlist" : "Channel")}: description and {channel.Videos.Count} recent videos"
                                 + (channel.Followers is { } f ? $" · {f:N0} subscribers" : "");
            link.FetchedAt = DateTimeOffset.UtcNow;
            link.Error = null;
            link.Status = channel.Videos.Count == 0 && string.IsNullOrWhiteSpace(channel.Description) ? LinkStatus.Skipped : LinkStatus.Fetched;
            link.Attempts = 0;
            logger.LogInformation("Fetched {Kind} {Url}: \"{Title}\", {Count} videos", link.Kind, link.Url, channel.Title, channel.Videos.Count);

            if (HasFollowTag(email) && !channel.IsPlaylist && link.AuthorId is not null && follows is not null)
                await follows.FollowAsync(link.AuthorId, channel.Title, channel.Url ?? link.Url, email!.Category, email.Tags, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Fetching {Url} failed: {Message}", link.Url, ex.Message);
            link.Status = LinkStatus.Failed;
            link.Error = $"YouTube: {ex.Message}";
            return false;
        }
    }

    private static bool HasFollowTag(InboxEmail? email) =>
        email is not null && email.Tags.Contains(FollowService.FollowTag, StringComparer.OrdinalIgnoreCase);

    private static async Task<Dictionary<int, InboxEmail>> EmailsForAsync(NomadInboxDbContext db, List<EmailLink> links, CancellationToken ct)
    {
        var ids = links.Select(l => l.InboxEmailId).Distinct().ToList();
        return await db.Emails.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
    }

    /// <summary>Metadata for the prompt: who made it, when, how long, what it says about itself.</summary>
    private static (SummaryKind Kind, string? Details) DescribeForSummary(EmailLink link)
    {
        if (link.Kind == LinkKind.Web) return (SummaryKind.Web, null);

        var lines = new List<string>();
        if (link.Kind == LinkKind.YouTubeVideo)
        {
            if (link.Author is not null) lines.Add($"Channel: {link.Author}");
            if (link.PublishedAt is { } p) lines.Add($"Published: {p:yyyy-MM-dd}");
            if (link.DurationSeconds is { } d) lines.Add($"Length: {VttTranscript.Stamp(d)}");
            if (link.ContentSource is not null) lines.Add($"Text source: {link.ContentSource}");
            if (link.Chapters is not null) lines.Add($"Chapters:\n{link.Chapters}");
            if (!string.IsNullOrWhiteSpace(link.Description) && link.ContentSource?.StartsWith("Video description") != true)
                lines.Add($"Description:\n{Cap(link.Description, 1500)}");
            return (SummaryKind.Video, string.Join('\n', lines));
        }

        lines.Add(link.Kind == LinkKind.YouTubePlaylist ? $"Playlist by: {link.Author ?? "unknown"}" : $"Channel: {link.Title}");
        if (link.ContentSource is not null) lines.Add(link.ContentSource);
        if (!string.IsNullOrWhiteSpace(link.Description)) lines.Add($"Description:\n{Cap(link.Description, 2000)}");
        return (SummaryKind.Channel, string.Join('\n', lines));

        static string Cap(string s, int max) => s.Length > max ? s[..max] + "…" : s;
    }

    /// <summary>
    /// The plain download had almost no text, which usually means the page is built by JavaScript
    /// (a single-page or WebAssembly app). Load it in a headless browser and keep whichever version has more text.
    /// </summary>
    private async Task<ReadableContent> RenderAsync(EmailLink link, InboxSettings settings, ReadableContent plain, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        try
        {
            var rendered = await renderer!.RenderAsync(link.Url, settings.AllowPrivateNetworkLinks, ct);
            var content = readable.ExtractHtml(rendered.FinalUrl, rendered.Html);
            logger.LogInformation("Rendered {Url} in a headless browser in {Seconds:0.0}s: {Before} → {After} characters",
                link.Url, (DateTimeOffset.UtcNow - started).TotalSeconds, plain.Text.Length, content.Text.Length);
            return content.Text.Length > plain.Text.Length
                ? content with { Note = "Rendered in a headless browser (the page is built by JavaScript)." }
                : plain;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Rendering {Url} failed: {Message}", link.Url, ex.Message);
            return plain with { Note = $"The page has little text and rendering it failed: {ex.Message}" };
        }
    }

    private async Task<bool> SummarizeAsync(EmailLink link, InboxEmail? email, InboxSettings settings, CancellationToken ct)
    {
        link.Attempts++;
        try
        {
            var (kind, details) = DescribeForSummary(link);
            var context = new SummaryContext(link.Title, link.FinalUrl ?? link.Url, email?.Category,
                email?.Tags ?? [], email?.Title, kind, details);
            var started = DateTimeOffset.UtcNow;
            var result = await summarizer.SummarizeAsync(settings, context, link.ContentText ?? "", ct);

            link.Summary = result.Markdown;
            link.SummaryModel = settings.OllamaModel;
            link.SummarizedAt = DateTimeOffset.UtcNow;
            link.Status = LinkStatus.Done;
            link.Error = null;
            logger.LogInformation("Summarized {Url} with {Model} in {Seconds:0.0}s ({Chunks} chunk(s))",
                link.Url, settings.OllamaModel, (DateTimeOffset.UtcNow - started).TotalSeconds, result.Chunks);

            if (link.VideoPath is not null && videos is not null)
            {
                try { await videos.WriteNfoAsync(link, email, ct); } // Jellyfin shows the summary as the plot
                catch (Exception ex) { logger.LogWarning("Updating the video's .nfo failed: {Message}", ex.Message); }
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Summarizing {Url} failed (attempt {Attempt}): {Message}", link.Url, link.Attempts, ex.Message);
            link.Error = $"Summary failed: {ex.Message}";
            if (link.Attempts >= MaxSummaryAttempts) link.Status = LinkStatus.Failed;
            return false;
        }
    }

    private async Task<bool> IsOllamaUpAsync(InboxSettings settings, CancellationToken ct)
    {
        try
        {
            await ollama.VersionAsync(settings.OllamaUrl, ct);
            return true;
        }
        catch (OllamaException ex)
        {
            logger.LogDebug("Ollama not reachable: {Message}", ex.Message);
            return false;
        }
    }
}
