using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Data;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.Images;
using NomadInbox.Infrastructure.Pages;
using NomadInbox.Infrastructure.Settings;
using NomadInbox.Infrastructure.YouTube;

namespace NomadInbox.Infrastructure.Bundles;

public sealed record ExportResult(int Number, string FileName, int Items, long Bytes);

/// <summary>
/// Collector mode: writes the items that are new or changed since the last bundle into the next bundle. An item goes
/// in once it's processed, its links are fetched, and its pages and videos are saved; when the collector has an AI,
/// also once its links are summarized and its images described, or after <see cref="InboxSettings.ExportAiWaitHours"/>
/// at the latest. An export bigger than <see cref="InboxSettings.MaxBundleMegabytes"/> is split into several bundles,
/// each with whole items and everything they refer to.
/// </summary>
public sealed class BundleExporter(
    IDbContextFactory<NomadInboxDbContext> dbFactory,
    SettingsService settingsService,
    AttachmentStore store,
    PageStore pageStore,
    VideoDownloader videos,
    string exportDirectory,
    ILogger<BundleExporter> logger)
{
    public string ExportDirectory { get; } = Path.GetFullPath(exportDirectory);

    private sealed record PendingItem(BundleItem Item, byte[] Json, string Hash, Dictionary<string, string> Files);

    /// <summary>Writes the next bundle (or several, when it's too big for one); empty when there's nothing new.</summary>
    public async Task<IReadOnlyList<ExportResult>> ExportAsync(CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);
        if (settings.Mode != AppMode.Collector) throw new InvalidOperationException("Bundles are exported only in Collector mode.");
        if (string.IsNullOrWhiteSpace(settings.CollectorId))
        {
            settings.CollectorId = Guid.NewGuid().ToString("N");
            await settingsService.SaveAsync(settings, newPassword: null, ct);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var emails = await db.Emails.AsNoTracking().Where(e => e.Status == EmailStatus.Done)
            .Include(e => e.Links).Include(e => e.Attachments).AsSplitQuery().ToListAsync(ct);
        var pages = (await db.Pages.AsNoTracking().Where(p => p.InboxEmailId != null)
            .Include(p => p.Assets).Include(p => p.Links).AsSplitQuery().ToListAsync(ct)).ToLookup(p => p.InboxEmailId!.Value);
        var pageUrls = await db.Pages.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Url, ct);
        var images = await db.ImageDescriptions.AsNoTracking().ToDictionaryAsync(d => d.ImageKey, ct);
        var exported = await db.ExportedItems.ToDictionaryAsync(e => e.MessageId, ct);
        var now = DateTimeOffset.UtcNow;

        var pending = new List<PendingItem>();
        foreach (var email in emails.Where(e => IsReady(e, settings, images, now, pages[e.Id])).OrderBy(e => e.ReceivedAt))
        {
            var (item, files) = await BuildItemAsync(email, pages[email.Id], pageUrls, images, ct);
            var json = JsonSerializer.SerializeToUtf8Bytes(item, BundleFormat.Json);
            var hash = BundleFormat.Sha256(json);
            if (exported.TryGetValue(email.MessageId, out var before) && before.ContentHash == hash) continue;
            pending.Add(new PendingItem(item, json, hash, files));
        }
        if (pending.Count == 0) return [];

        Directory.CreateDirectory(ExportDirectory);
        var number = (await db.ExportedBundles.MaxAsync(b => (int?)b.Number, ct) ?? 0) + 1;
        var limit = Math.Max(1, settings.MaxBundleMegabytes) * 1024L * 1024;
        var sizes = new Dictionary<string, long>();
        long SizeOf(string path) => sizes.TryGetValue(path, out var size) ? size : sizes[path] = File.Exists(path) ? new FileInfo(path).Length : 0;

        // Whole items in order; a file several items share is counted (and stored) once per bundle.
        var results = new List<ExportResult>();
        var batch = new List<PendingItem>();
        var batchFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
        long batchBytes = 0;
        long Adds(PendingItem p) => p.Json.Length + p.Files.Where(f => !batchFiles.ContainsKey(f.Key)).Sum(f => SizeOf(f.Value));
        foreach (var p in pending)
        {
            if (batch.Count > 0 && batchBytes + Adds(p) > limit)
            {
                results.Add(await WriteBundleAsync(db, settings, number++, now, batch, batchFiles, exported, ct));
                batch.Clear();
                batchFiles.Clear();
                batchBytes = 0;
            }
            batchBytes += Adds(p);
            batch.Add(p);
            foreach (var (entry, path) in p.Files) batchFiles[entry] = path;
        }
        results.Add(await WriteBundleAsync(db, settings, number, now, batch, batchFiles, exported, ct));
        return results;
    }

    private async Task<ExportResult> WriteBundleAsync(NomadInboxDbContext db, InboxSettings settings, int number, DateTimeOffset now,
        IReadOnlyList<PendingItem> items, IReadOnlyDictionary<string, string> files, Dictionary<string, ExportedItem> exported, CancellationToken ct)
    {
        var fileName = $"nomad-inbox-{BundleFormat.Slug(settings.CollectorName)}-{number}-{now:yyyyMMdd-HHmm}.zip";
        var target = Path.Combine(ExportDirectory, fileName);
        var temp = target + ".partial";

        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        await using (var stream = File.Create(temp))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var p in items)
            {
                var entry = BundleFormat.ItemEntry(p.Item.MessageId);
                hashes[entry] = p.Hash;
                await WriteAsync(zip, entry, p.Json, CompressionLevel.Optimal, ct);
            }
            foreach (var (entry, path) in files)
            {
                // Media is compressed already.
                var level = entry.StartsWith("assets/", StringComparison.Ordinal) || entry.StartsWith("videos/", StringComparison.Ordinal)
                    ? CompressionLevel.NoCompression
                    : entry.StartsWith("pages/", StringComparison.Ordinal) ? CompressionLevel.Optimal : CompressionLevel.Fastest;
                hashes[entry] = await WriteFileAsync(zip, entry, path, level, ct);
            }
            var manifest = new BundleManifest(BundleFormat.Format, BundleFormat.Version, settings.CollectorId!, settings.CollectorName,
                number, now, items.Count, hashes);
            await WriteAsync(zip, BundleFormat.ManifestName, JsonSerializer.SerializeToUtf8Bytes(manifest, BundleFormat.Json), CompressionLevel.Optimal, ct);
        }
        File.Move(temp, target);

        var bytesWritten = new FileInfo(target).Length;
        db.ExportedBundles.Add(new ExportedBundle { Number = number, FileName = fileName, Items = items.Count, Bytes = bytesWritten, CreatedAt = now });
        foreach (var p in items)
        {
            if (exported.TryGetValue(p.Item.MessageId, out var row))
            {
                row.ContentHash = p.Hash;
                row.BundleNumber = number;
                row.ExportedAt = now;
            }
            else
            {
                exported[p.Item.MessageId] = db.ExportedItems.Add(new ExportedItem
                    { MessageId = p.Item.MessageId, ContentHash = p.Hash, BundleNumber = number, ExportedAt = now }).Entity;
            }
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Exported bundle {Number} ({File}): {Items} item(s), {Files} file(s), {Mb:N0} MB",
            number, fileName, items.Count, files.Count, bytesWritten / 1024 / 1024);
        return new ExportResult(number, fileName, items.Count, bytesWritten);
    }

    /// <summary>Whether an item is ready to go into a bundle (see the class summary).</summary>
    /// <param name="pages">The saved pages that belong to the item.</param>
    public static bool IsReady(InboxEmail email, InboxSettings settings, IReadOnlyDictionary<string, ImageDescription> images, DateTimeOffset now,
        IEnumerable<ArchivedPage>? pages = null)
    {
        if (email.Status != EmailStatus.Done) return false;
        // Don't wait forever: an item goes out after the wait even if a link is stuck or the AI is down.
        if (now - (email.ProcessedAt ?? email.ReceivedAt) >= TimeSpan.FromHours(Math.Max(0, settings.ExportAiWaitHours))) return true;

        var saved = pages?.ToList() ?? [];
        if (email.Links.Any(l => l.Status == LinkStatus.Pending)) return false; // still being fetched
        if (email.Links.Any(l => l.VideoStatus == VideoStatus.Queued)) return false; // still being downloaded
        if (saved.Any(p => p.Status is PageStatus.Queued or PageStatus.Saving)) return false; // still being saved
        if (settings.IsAiConfigured && email.Links.Any(l => l.Status == LinkStatus.Fetched)) return false; // waiting for a summary
        if (settings.IsImageAiConfigured)
        {
            var from = settings.DescribeImagesFrom;
            bool Waiting(string key, DateTimeOffset when) => images.TryGetValue(key, out var image)
                ? image.Status == ImageDescriptionStatus.Pending
                : from is null || when >= from; // about to be queued for a description

            foreach (var file in email.Attachments.Where(a => a.StoredPath is not null && a.Sha256 is not null && !a.StoredAsZip &&
                                                              ImageDescriber.IsImage(a.FileName, a.ContentType) && ImageDescriber.CanRead(a.FileName)))
            {
                if (Waiting(ImageDescription.KeyFor(file.Sha256!), email.ProcessedAt ?? email.ReceivedAt)) return false;
            }
            foreach (var page in saved.Where(p => p.Status == PageStatus.Done))
            {
                foreach (var asset in ImageDescriber.PageImages(page, settings).Where(a => ImageDescriber.CanRead(a.FileName!)))
                {
                    if (Waiting(Path.GetFileNameWithoutExtension(asset.FileName!), page.ArchivedAt ?? page.RequestedAt)) return false;
                }
            }
        }
        return true;
    }

    private async Task<(BundleItem Item, Dictionary<string, string> Files)> BuildItemAsync(InboxEmail email, IEnumerable<ArchivedPage> pages,
        IReadOnlyDictionary<int, string> pageUrls, IReadOnlyDictionary<string, ImageDescription> images, CancellationToken ct)
    {
        var files = new Dictionary<string, string>(); // entry → file on disk
        var attachments = new List<BundleAttachment>();
        foreach (var a in email.Attachments.OrderBy(a => a.Id))
        {
            string? entry = null;
            if (a.StoredPath is not null && store.ResolveFullPath(a.StoredPath) is { } path && File.Exists(path))
            {
                // A program is stored zipped, so its stored file differs from the original's checksum.
                var sha = a.StoredAsZip || a.Sha256 is null ? await BundleFormat.Sha256Async(path, ct) : a.Sha256;
                entry = $"files/{sha}";
                files[entry] = path;
            }
            var image = a.Sha256 is not null ? Described(images, ImageDescription.KeyFor(a.Sha256)) : null;
            attachments.Add(new BundleAttachment(a.FileName, a.ContentType, a.SizeBytes, a.Sha256, a.StoredAsZip, a.ExtractedText, a.Note, entry, image));
        }

        var links = new List<BundleLink>();
        foreach (var l in email.Links.OrderBy(l => l.Position))
        {
            string? video = null, thumbnail = null;
            if (l.VideoStatus == VideoStatus.Downloaded && l.VideoPath is not null && videos.ResolveFullPath(l.VideoPath) is { } videoFile &&
                File.Exists(videoFile) && new FileInfo(videoFile).Length <= BundleImporter.MaxEntryBytes)
            {
                (video, thumbnail) = BundleFormat.VideoEntries(l.VideoPath);
                files[video] = videoFile;
                var thumbFile = Path.Combine(Path.GetDirectoryName(videoFile)!, Path.GetFileNameWithoutExtension(videoFile) + "-thumb.jpg");
                if (File.Exists(thumbFile)) files[thumbnail] = thumbFile;
                else thumbnail = null;
            }
            links.Add(new BundleLink(
                l.Url, l.Kind, l.Position, l.Status, l.Error, l.FinalUrl, l.ContentType, l.Title, l.SiteName, l.ContentText, l.FetchedAt,
                l.ExternalId, l.Author, l.AuthorId, l.AuthorUrl, l.PublishedAt, l.DurationSeconds, l.Description, l.ContentSource,
                l.Chapters, l.Summary, l.SummaryModel, l.SummarizedAt,
                l.VideoStatus, l.VideoPath, l.VideoBytes, l.VideoError, video, thumbnail));
        }

        var savedPages = new List<BundlePage>();
        foreach (var page in pages.Where(p => p.Status == PageStatus.Done).OrderBy(p => p.Id))
        {
            string? content = null;
            var contentFile = pageStore.ContentPathFor(page.Id);
            if (File.Exists(contentFile))
            {
                content = $"pages/{await BundleFormat.Sha256Async(contentFile, ct)}.html";
                files[content] = contentFile;
            }

            var assets = new List<BundlePageAsset>();
            foreach (var asset in page.Assets.OrderBy(a => a.Id))
            {
                string? entry = null;
                if (asset.FileName is not null && BundleFormat.AssetName().IsMatch(asset.FileName) && pageStore.ResolveAsset(asset.FileName) is { } assetFile)
                {
                    entry = $"assets/{asset.FileName}";
                    files[entry] = assetFile;
                }
                var image = asset is { Kind: AssetKind.Image, FileName: not null } ? Described(images, Path.GetFileNameWithoutExtension(asset.FileName)) : null;
                assets.Add(new BundlePageAsset(asset.OriginalUrl, asset.Kind, asset.FileName, asset.ContentType, asset.Bytes, asset.Error, entry, image));
            }

            savedPages.Add(new BundlePage(page.Url, page.FinalUrl, page.Title, page.SiteName, page.Byline, page.Excerpt, page.TextContent,
                page.RequestedAt, page.ArchivedAt, page.Bytes,
                email.Links.FirstOrDefault(l => l.Id == page.SourceLinkId)?.Position,
                page.SavedFromPageId is { } from && pageUrls.TryGetValue(from, out var fromUrl) ? fromUrl : null,
                content, assets,
                page.Links.OrderBy(l => l.Position).Select(l => new BundlePageLink(l.Url, l.Text, l.Position)).ToList()));
        }

        var item = new BundleItem(email.MessageId, email.Source, email.Folder, email.FromAddress, email.FromName, email.Subject,
            email.ReceivedAt, email.Category, email.Tags, email.Fields, email.Title, email.BodyText, links, attachments,
            savedPages.Count > 0 ? savedPages : null);
        return (item, files);
    }

    private static BundleImage? Described(IReadOnlyDictionary<string, ImageDescription> images, string key) =>
        images.TryGetValue(key, out var d) && d.Status == ImageDescriptionStatus.Done && d.Caption is not null
            ? new BundleImage(d.Caption, d.ImageType, d.Categories, d.Tags, d.ImageText, d.Model)
            : null;

    private static async Task WriteAsync(ZipArchive zip, string name, byte[] content, CompressionLevel level, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, level);
        await using var stream = entry.Open();
        await stream.WriteAsync(content, ct);
    }

    /// <summary>Copies a file into the bundle, hashing it on the way (videos are too big to read into memory).</summary>
    private static async Task<string> WriteFileAsync(ZipArchive zip, string name, string path, CompressionLevel level, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, level);
        await using var target = entry.Open();
        await using var source = File.OpenRead(path);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}

/// <summary>Collector mode: writes a bundle every <see cref="InboxSettings.AutoExportHours"/> when there's something new.</summary>
public sealed class BundleExportWorker(
    BundleExporter exporter,
    SettingsService settingsService,
    IDbContextFactory<NomadInboxDbContext> dbFactory,
    ILogger<BundleExportWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var settings = await settingsService.GetAsync(stoppingToken);
                    if (settings.Mode == AppMode.Collector && settings.AutoExportHours > 0)
                    {
                        await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                        var last = (await db.ExportedBundles.Select(b => b.CreatedAt).ToListAsync(stoppingToken)).DefaultIfEmpty().Max();
                        if (DateTimeOffset.UtcNow - last >= TimeSpan.FromHours(settings.AutoExportHours))
                            await exporter.ExportAsync(stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Automatic bundle export failed: {Message}", ex.Message);
                }
                await Task.Delay(Tick, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
