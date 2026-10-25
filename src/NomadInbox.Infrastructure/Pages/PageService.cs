using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NomadInbox.Core.Content;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Data;
using NomadInbox.Infrastructure.Ingest;
using NomadInbox.Infrastructure.Settings;

namespace NomadInbox.Infrastructure.Pages;

/// <summary>Wakes the page archive worker: a page was queued.</summary>
public sealed class PageTrigger : WorkSignal;

public sealed record SaveLinkResult(int? PageId, int? EmailId, string Message);

/// <summary>Queues, saves, refreshes and deletes saved pages.</summary>
public sealed class PageService(
    IDbContextFactory<NomadInboxDbContext> dbFactory,
    SettingsService settingsService,
    PageArchiver archiver,
    PageStore store,
    PageExporter exporter,
    PageTrigger trigger,
    LinkTrigger linkTrigger,
    ILogger<PageService> logger)
{
    public const string ArchiveTag = "archive";

    public static bool ShouldArchive(PageArchiveMode mode, IEnumerable<string> tags) => mode switch
    {
        PageArchiveMode.Always => true,
        PageArchiveMode.Tagged => tags.Contains(ArchiveTag, StringComparer.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>Queues a page unless it's already saved or queued; a failed one is retried. Returns its id.</summary>
    public async Task<int> QueueAsync(string url, int? emailId, int? sourceLinkId, int? savedFromPageId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var page = await db.Pages.SingleOrDefaultAsync(p => p.Url == url, ct);
        if (page is null)
        {
            page = new ArchivedPage { Url = url, InboxEmailId = emailId, SourceLinkId = sourceLinkId, SavedFromPageId = savedFromPageId };
            db.Pages.Add(page);
        }
        else if (page.Status == PageStatus.Failed)
        {
            page.Status = PageStatus.Queued;
            page.Error = null;
        }
        page.InboxEmailId ??= emailId;
        page.SourceLinkId ??= sourceLinkId;
        await db.SaveChangesAsync(ct);
        trigger.Signal();
        return page.Id;
    }

    /// <summary>The email was processed: queue its web links if the archive setting (or its #archive tag) says so.</summary>
    public async Task<int> QueueForEmailAsync(InboxEmail email, InboxSettings settings, CancellationToken ct)
    {
        if (!ShouldArchive(settings.PageArchiveMode, email.Tags)) return 0;
        var queued = 0;
        foreach (var link in email.Links.Where(l => l.Kind == LinkKind.Web))
        {
            await QueueAsync(link.Url, email.Id, link.Id, null, ct);
            queued++;
        }
        return queued;
    }

    public async Task RefreshAsync(int pageId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Pages.Where(p => p.Id == pageId).ExecuteUpdateAsync(u => u
            .SetProperty(p => p.Status, PageStatus.Queued)
            .SetProperty(p => p.Error, (string?)null), ct);
        trigger.Signal();
    }

    /// <summary>
    /// "Save this page" on a link from a saved page. YouTube links aren't pages: they go to the email as a video,
    /// so they get a transcript, a summary and (per the video settings) a download.
    /// </summary>
    public async Task<SaveLinkResult> SaveLinkAsync(int pageLinkId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var link = await db.PageLinks.AsNoTracking().SingleOrDefaultAsync(l => l.Id == pageLinkId, ct);
        if (link is null) return new SaveLinkResult(null, null, "That link no longer exists.");
        var from = await db.Pages.AsNoTracking().SingleAsync(p => p.Id == link.ArchivedPageId, ct);

        if (LinkExtractor.Normalize(link.Url) is { Kind: not LinkKind.Web } youTube)
        {
            if (from.InboxEmailId is not { } emailId) return new SaveLinkResult(null, null, "This page doesn't belong to an email.");
            await AddVideoLinksAsync(emailId, [youTube.Url], ct);
            return new SaveLinkResult(null, emailId, "Added to the email as a YouTube link; it will be summarized there.");
        }

        var id = await QueueAsync(link.Url, from.InboxEmailId, null, from.Id, ct);
        return new SaveLinkResult(id, from.InboxEmailId, "Saving…");
    }

    /// <summary>Queues the not-yet-saved pages linked from a page (same site only unless <paramref name="anySite"/>).</summary>
    public async Task<int> SaveLinkedPagesAsync(int pageId, bool anySite, CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var page = await db.Pages.AsNoTracking().Include(p => p.Links).SingleOrDefaultAsync(p => p.Id == pageId, ct);
        if (page is null) return 0;

        var host = Host(page.FinalUrl ?? page.Url);
        var urls = page.Links.OrderBy(l => l.Position).Select(l => l.Url)
            .Where(u => LinkExtractor.Normalize(u)?.Kind == LinkKind.Web)
            .Where(u => anySite || Host(u) == host)
            .Distinct().ToList();
        var existing = (await db.Pages.Where(p => urls.Contains(p.Url) && p.Status != PageStatus.Failed).Select(p => p.Url).ToListAsync(ct)).ToHashSet();

        var queued = 0;
        foreach (var url in urls.Where(u => !existing.Contains(u)).Take(Math.Max(1, settings.SaveLinkedPagesLimit)))
        {
            await QueueAsync(url, page.InboxEmailId, null, page.Id, ct);
            queued++;
        }
        return queued;
    }

    /// <summary>Deletes a saved page, its content, and media no other page uses.</summary>
    public async Task<bool> DeleteAsync(int pageId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var page = await db.Pages.Include(p => p.Assets).SingleOrDefaultAsync(p => p.Id == pageId, ct);
        if (page is null) return false;

        var files = page.Assets.Select(a => a.FileName).OfType<string>().Distinct().ToList();
        db.Pages.Remove(page);
        await db.SaveChangesAsync(ct);

        await DeleteUnusedAssetsAsync(db, files, ct);
        store.DeleteContent(pageId);
        await exporter.PageChangedAsync(pageId, page.Url, ct); // removes its folder, fixes links to it
        logger.LogInformation("Deleted saved page {Id} ({Url})", pageId, page.Url);
        return true;
    }

    /// <summary>Saves the oldest queued page. Returns false when there was nothing to do.</summary>
    public async Task<bool> ArchiveNextAsync(CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);
        if (!settings.UsesInternet) return false; // air-gapped: saved pages arrive in bundles
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // Anything left "Saving" was interrupted by a restart.
        await db.Pages.Where(p => p.Status == PageStatus.Saving)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PageStatus.Queued), ct);

        var page = await db.Pages.Include(p => p.Assets).Include(p => p.Links).AsSplitQuery()
            .Where(p => p.Status == PageStatus.Queued).OrderBy(p => p.RequestedAt).FirstOrDefaultAsync(ct);
        if (page is null) return false;

        page.Status = PageStatus.Saving;
        await db.SaveChangesAsync(ct);

        IReadOnlyList<string> youTube = [];
        try
        {
            var result = await archiver.ArchiveAsync(page, settings, ct);
            page.Status = PageStatus.Done;
            youTube = result.YouTubeIds;
            await db.SaveChangesAsync(ct);
            await DeleteUnusedAssetsAsync(db, result.ReplacedAssetFiles, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Saving page {Url} failed: {Message}", page.Url, ex.Message);
            db.ChangeTracker.Clear();
            await db.Pages.Where(p => p.Id == page.Id).ExecuteUpdateAsync(u => u
                .SetProperty(p => p.Status, PageStatus.Failed)
                .SetProperty(p => p.Error, ex.Message), ct);
            return true;
        }

        // Embedded YouTube videos go to the email, so the video settings decide about transcripts and downloads.
        if (youTube.Count > 0 && page.InboxEmailId is { } emailId)
            await AddVideoLinksAsync(emailId, youTube.Select(id => $"https://www.youtube.com/watch?v={id}"), ct);

        await exporter.PageChangedAsync(page.Id, page.Url, ct);
        return true;
    }

    private async Task AddVideoLinksAsync(int emailId, IEnumerable<string> urls, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var email = await db.Emails.Include(e => e.Links).SingleOrDefaultAsync(e => e.Id == emailId, ct);
        if (email is null) return;
        var added = 0;
        foreach (var url in urls)
        {
            if (email.Links.Any(l => l.Url == url)) continue;
            email.Links.Add(new EmailLink { Url = url, Kind = LinkKind.YouTubeVideo, Position = email.Links.Count == 0 ? 0 : email.Links.Max(l => l.Position) + 1 });
            added++;
        }
        if (added == 0) return;
        email.LinkCount = email.Links.Count;
        await db.SaveChangesAsync(ct);
        linkTrigger.Signal();
        logger.LogInformation("Added {Count} embedded YouTube video(s) to email {Id}", added, emailId);
    }

    private async Task DeleteUnusedAssetsAsync(NomadInboxDbContext db, IEnumerable<string> files, CancellationToken ct)
    {
        foreach (var file in files.Distinct())
            if (!await db.PageAssets.AnyAsync(a => a.FileName == file, ct)) store.DeleteAsset(file);
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : "";
}

/// <summary>Saves queued pages one at a time, in the background.</summary>
public sealed class PageArchiveWorker(PageService pages, PageTrigger trigger, ILogger<PageArchiveWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var didWork = false;
            try
            {
                didWork = await pages.ArchiveNextAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Page archive failed");
            }
            if (!didWork) await trigger.WaitAsync(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
