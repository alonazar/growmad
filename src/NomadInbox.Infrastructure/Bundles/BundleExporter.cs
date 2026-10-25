using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Data;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.Images;
using NomadInbox.Infrastructure.Settings;

namespace NomadInbox.Infrastructure.Bundles;

public sealed record ExportResult(int Number, string FileName, int Items, long Bytes);

/// <summary>
/// Collector mode: writes the items that are new or changed since the last bundle into the next bundle. An item goes
/// in once it's processed and its links are fetched; when the collector has an AI, also once its links are summarized
/// and its images described, or after <see cref="InboxSettings.ExportAiWaitHours"/> at the latest.
/// </summary>
public sealed class BundleExporter(
    IDbContextFactory<NomadInboxDbContext> dbFactory,
    SettingsService settingsService,
    AttachmentStore store,
    string exportDirectory,
    ILogger<BundleExporter> logger)
{
    public string ExportDirectory { get; } = Path.GetFullPath(exportDirectory);

    /// <summary>Writes the next bundle, or returns null when there's nothing new.</summary>
    public async Task<ExportResult?> ExportAsync(CancellationToken ct)
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
        var images = await db.ImageDescriptions.AsNoTracking().ToDictionaryAsync(d => d.ImageKey, ct);
        var exported = await db.ExportedItems.ToDictionaryAsync(e => e.MessageId, ct);
        var now = DateTimeOffset.UtcNow;

        var items = new List<(BundleItem Item, byte[] Json, string Hash)>();
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal); // entry → file on disk
        foreach (var email in emails.Where(e => IsReady(e, settings, images, now)).OrderBy(e => e.ReceivedAt))
        {
            var (item, itemFiles) = BuildItem(email, images);
            var json = JsonSerializer.SerializeToUtf8Bytes(item, BundleFormat.Json);
            var hash = BundleFormat.Sha256(json);
            if (exported.TryGetValue(email.MessageId, out var before) && before.ContentHash == hash) continue;
            items.Add((item, json, hash));
            foreach (var (entry, path) in itemFiles) files[entry] = path;
        }
        if (items.Count == 0) return null;

        Directory.CreateDirectory(ExportDirectory);
        var number = (await db.ExportedBundles.MaxAsync(b => (int?)b.Number, ct) ?? 0) + 1;
        var fileName = $"nomad-inbox-{BundleFormat.Slug(settings.CollectorName)}-{number}-{now:yyyyMMdd-HHmm}.zip";
        var target = Path.Combine(ExportDirectory, fileName);
        var temp = target + ".partial";

        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        await using (var stream = File.Create(temp))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (item, json, _) in items)
            {
                var entry = BundleFormat.ItemEntry(item.MessageId);
                hashes[entry] = BundleFormat.Sha256(json);
                await WriteAsync(zip, entry, json, CompressionLevel.Optimal, ct);
            }
            foreach (var (entry, path) in files)
            {
                var bytes = await File.ReadAllBytesAsync(path, ct);
                hashes[entry] = BundleFormat.Sha256(bytes);
                await WriteAsync(zip, entry, bytes, CompressionLevel.Fastest, ct);
            }
            var manifest = new BundleManifest(BundleFormat.Format, BundleFormat.Version, settings.CollectorId!, settings.CollectorName,
                number, now, items.Count, hashes);
            await WriteAsync(zip, BundleFormat.ManifestName, JsonSerializer.SerializeToUtf8Bytes(manifest, BundleFormat.Json), CompressionLevel.Optimal, ct);
        }
        File.Move(temp, target);

        var bytesWritten = new FileInfo(target).Length;
        db.ExportedBundles.Add(new ExportedBundle { Number = number, FileName = fileName, Items = items.Count, Bytes = bytesWritten, CreatedAt = now });
        foreach (var (item, _, hash) in items)
        {
            if (exported.TryGetValue(item.MessageId, out var row))
            {
                row.ContentHash = hash;
                row.BundleNumber = number;
                row.ExportedAt = now;
            }
            else
            {
                db.ExportedItems.Add(new ExportedItem { MessageId = item.MessageId, ContentHash = hash, BundleNumber = number, ExportedAt = now });
            }
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Exported bundle {Number} ({File}): {Items} item(s), {Files} file(s), {Kb} KB",
            number, fileName, items.Count, files.Count, bytesWritten / 1024);
        return new ExportResult(number, fileName, items.Count, bytesWritten);
    }

    /// <summary>Whether an item is ready to go into a bundle (see the class summary).</summary>
    public static bool IsReady(InboxEmail email, InboxSettings settings, IReadOnlyDictionary<string, ImageDescription> images, DateTimeOffset now)
    {
        if (email.Status != EmailStatus.Done) return false;
        // Don't wait forever: an item goes out after the wait even if a link is stuck or the AI is down.
        if (now - (email.ProcessedAt ?? email.ReceivedAt) >= TimeSpan.FromHours(Math.Max(0, settings.ExportAiWaitHours))) return true;

        if (email.Links.Any(l => l.Status == LinkStatus.Pending)) return false; // still being fetched
        if (settings.IsAiConfigured && email.Links.Any(l => l.Status == LinkStatus.Fetched)) return false; // waiting for a summary
        if (settings.IsImageAiConfigured)
        {
            var from = settings.DescribeImagesFrom;
            foreach (var file in email.Attachments.Where(a => a.StoredPath is not null && a.Sha256 is not null && !a.StoredAsZip &&
                                                              ImageDescriber.IsImage(a.FileName, a.ContentType) && ImageDescriber.CanRead(a.FileName)))
            {
                if (images.TryGetValue(ImageDescription.KeyFor(file.Sha256!), out var image))
                {
                    if (image.Status == ImageDescriptionStatus.Pending) return false;
                }
                else if (from is null || (email.ProcessedAt ?? email.ReceivedAt) >= from)
                {
                    return false; // about to be queued for a description
                }
            }
        }
        return true;
    }

    private (BundleItem Item, Dictionary<string, string> Files) BuildItem(InboxEmail email, IReadOnlyDictionary<string, ImageDescription> images)
    {
        var files = new Dictionary<string, string>();
        var attachments = new List<BundleAttachment>();
        foreach (var a in email.Attachments.OrderBy(a => a.Id))
        {
            string? entry = null;
            if (a.StoredPath is not null && store.ResolveFullPath(a.StoredPath) is { } path && File.Exists(path))
            {
                // A program is stored zipped, so its stored file differs from the original's checksum.
                var sha = a.StoredAsZip || a.Sha256 is null ? BundleFormat.Sha256(File.ReadAllBytes(path)) : a.Sha256;
                entry = $"files/{sha}";
                files[entry] = path;
            }
            var image = a.Sha256 is not null && images.TryGetValue(ImageDescription.KeyFor(a.Sha256), out var d) &&
                        d.Status == ImageDescriptionStatus.Done && d.Caption is not null
                ? new BundleImage(d.Caption, d.ImageType, d.Categories, d.Tags, d.ImageText, d.Model)
                : null;
            attachments.Add(new BundleAttachment(a.FileName, a.ContentType, a.SizeBytes, a.Sha256, a.StoredAsZip, a.ExtractedText, a.Note, entry, image));
        }

        var links = email.Links.OrderBy(l => l.Position).Select(l => new BundleLink(
            l.Url, l.Kind, l.Position, l.Status, l.Error, l.FinalUrl, l.ContentType, l.Title, l.SiteName, l.ContentText, l.FetchedAt,
            l.ExternalId, l.Author, l.AuthorId, l.AuthorUrl, l.PublishedAt, l.DurationSeconds, l.Description, l.ContentSource,
            l.Chapters, l.Summary, l.SummaryModel, l.SummarizedAt)).ToList();

        var item = new BundleItem(email.MessageId, email.Source, email.Folder, email.FromAddress, email.FromName, email.Subject,
            email.ReceivedAt, email.Category, email.Tags, email.Fields, email.Title, email.BodyText, links, attachments);
        return (item, files);
    }

    private static async Task WriteAsync(ZipArchive zip, string name, byte[] content, CompressionLevel level, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, level);
        await using var stream = entry.Open();
        await stream.WriteAsync(content, ct);
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
