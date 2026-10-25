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
using NomadInbox.Infrastructure.Ingest;
using NomadInbox.Infrastructure.Knowledge;
using NomadInbox.Infrastructure.Settings;

namespace NomadInbox.Infrastructure.Bundles;

public sealed record ImportResult(ImportStatus Status, string Message, int Items = 0, int Skipped = 0);

/// <summary>A bundle that can't be imported; nothing from it is used.</summary>
public sealed class BundleException(string message) : Exception(message);

/// <summary>
/// Air-gapped mode: imports a bundle into this NOMAD Inbox, after which the usual workers take over (summaries and
/// image descriptions where the collector had no AI, and the knowledge base sync). The bundle is checked first:
/// known format, only the expected files, and every file matching the checksum in the manifest. Importing is
/// idempotent and order-independent: unchanged items, items a newer bundle already brought, and items deleted
/// here are skipped.
/// </summary>
public sealed class BundleImporter(
    IDbContextFactory<NomadInboxDbContext> dbFactory,
    SettingsService settingsService,
    AttachmentStore store,
    KnowledgeTrigger knowledgeTrigger,
    LinkTrigger linkTrigger,
    ImageTrigger imageTrigger,
    string importDirectory,
    ILogger<BundleImporter> logger)
{
    public const int MaxEntries = 100_000;
    public const long MaxManifestBytes = 16 * 1024 * 1024;
    public const long MaxEntryBytes = 4L * 1024 * 1024 * 1024;

    public string ImportDirectory { get; } = Path.GetFullPath(importDirectory);

    public async Task<ImportResult> ImportAsync(string zipPath, string fileName, CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);
        var record = new ImportedBundle { FileName = Path.GetFileName(fileName) };
        ImportResult result;
        try
        {
            if (settings.Mode != AppMode.AirGapped)
                throw new BundleException("Bundles are imported only in Air-gapped mode (Settings → Mode).");

            using var zip = ZipFile.OpenRead(zipPath);
            var manifest = await ReadAndVerifyAsync(zip, ct);
            record.CollectorId = manifest.CollectorId;
            record.CollectorName = manifest.CollectorName;
            record.Number = manifest.Number;

            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                if (await db.ImportedBundles.AnyAsync(b => b.CollectorId == manifest.CollectorId && b.Number == manifest.Number &&
                                                           b.Status == ImportStatus.Imported, ct))
                {
                    result = new ImportResult(ImportStatus.AlreadyImported, $"Bundle {manifest.Number} from {manifest.CollectorName} was imported before.");
                    return await RecordAsync(record, result, ct);
                }
            }

            var (items, skipped) = await ImportItemsAsync(zip, manifest, ct);
            result = new ImportResult(ImportStatus.Imported,
                $"Imported bundle {manifest.Number} from {manifest.CollectorName}: {items} item(s)" + (skipped > 0 ? $", {skipped} unchanged or skipped." : "."),
                items, skipped);
            knowledgeTrigger.Signal();
            linkTrigger.Signal();
            imageTrigger.Signal();
        }
        catch (BundleException ex)
        {
            result = new ImportResult(ImportStatus.Rejected, ex.Message);
        }
        catch (InvalidDataException)
        {
            result = new ImportResult(ImportStatus.Rejected, "Not a ZIP file, or the file is damaged.");
        }
        catch (JsonException ex)
        {
            result = new ImportResult(ImportStatus.Rejected, $"The bundle's contents aren't readable: {ex.Message}");
        }
        return await RecordAsync(record, result, ct);
    }

    /// <summary>Reads the manifest and checks every file against it; throws <see cref="BundleException"/> otherwise.</summary>
    public static async Task<BundleManifest> ReadAndVerifyAsync(ZipArchive zip, CancellationToken ct)
    {
        if (zip.Entries.Count > MaxEntries) throw new BundleException("The bundle has too many files.");
        var manifestEntry = zip.GetEntry(BundleFormat.ManifestName) ?? throw new BundleException("Not a NOMAD Inbox bundle (no manifest.json).");
        if (manifestEntry.Length > MaxManifestBytes) throw new BundleException("The bundle's manifest is too large.");

        BundleManifest? manifest;
        await using (var stream = manifestEntry.Open())
            manifest = await JsonSerializer.DeserializeAsync<BundleManifest>(stream, BundleFormat.Json, ct);
        if (manifest is null || manifest.Format != BundleFormat.Format) throw new BundleException("Not a NOMAD Inbox bundle.");
        if (manifest.Version > BundleFormat.Version)
            throw new BundleException($"The bundle is format version {manifest.Version}; this NOMAD Inbox reads up to {BundleFormat.Version}. Update it first.");
        if (string.IsNullOrWhiteSpace(manifest.CollectorId) || manifest.Number <= 0 || manifest.Files is null)
            throw new BundleException("The bundle's manifest is incomplete.");

        var listed = manifest.Files.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var entry in zip.Entries.Where(e => e.FullName != BundleFormat.ManifestName))
        {
            if (!BundleFormat.EntryName().IsMatch(entry.FullName) || !listed.Remove(entry.FullName))
                throw new BundleException($"The bundle contains an unexpected file: {entry.FullName}");
            if (entry.Length > MaxEntryBytes) throw new BundleException($"{entry.FullName} is too large.");
            if (await HashAsync(entry, ct) != manifest.Files[entry.FullName])
                throw new BundleException($"{entry.FullName} doesn't match its checksum; the bundle is damaged or was changed.");
        }
        if (listed.Count > 0) throw new BundleException($"The bundle is missing {listed.Count} file(s), e.g. {listed.First()}.");
        return manifest;
    }

    private async Task<(int Items, int Skipped)> ImportItemsAsync(ZipArchive zip, BundleManifest manifest, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deleted = (await db.DeletedMessages.Select(d => d.MessageId).ToListAsync(ct)).ToHashSet();
        int items = 0, skipped = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("items/", StringComparison.Ordinal)).OrderBy(e => e.FullName))
        {
            var json = await ReadAsync(entry, ct);
            var hash = BundleFormat.Sha256(json);
            var item = JsonSerializer.Deserialize<BundleItem>(json, BundleFormat.Json);
            if (item is null || string.IsNullOrWhiteSpace(item.MessageId) || item.MessageId.Length > 1000)
                throw new BundleException($"{entry.FullName} isn't a valid item.");

            var previous = await db.ImportedItems.SingleOrDefaultAsync(i => i.MessageId == item.MessageId, ct);
            if (deleted.Contains(item.MessageId) ||
                previous is not null && (previous.ContentHash == hash ||
                                         previous.CollectorId == manifest.CollectorId && previous.BundleNumber > manifest.Number))
            {
                skipped++;
                continue;
            }

            await ApplyAsync(db, zip, item, now, ct);
            if (previous is null)
                db.ImportedItems.Add(new ImportedItem { MessageId = item.MessageId, CollectorId = manifest.CollectorId, BundleNumber = manifest.Number, ContentHash = hash, ImportedAt = now });
            else
            {
                previous.CollectorId = manifest.CollectorId;
                previous.BundleNumber = manifest.Number;
                previous.ContentHash = hash;
                previous.ImportedAt = now;
            }
            await db.SaveChangesAsync(ct);
            items++;
        }
        return (items, skipped);
    }

    /// <summary>Adds the item, or replaces an earlier copy's links and attachments.</summary>
    private async Task ApplyAsync(NomadInboxDbContext db, ZipArchive zip, BundleItem item, DateTimeOffset now, CancellationToken ct)
    {
        var email = await db.Emails.Include(e => e.Links).Include(e => e.Attachments).AsSplitQuery()
            .SingleOrDefaultAsync(e => e.MessageId == item.MessageId, ct);
        if (email is null)
        {
            email = new InboxEmail { MessageId = item.MessageId, Folder = item.Folder, FromAddress = item.FromAddress, Subject = item.Subject, CreatedAt = now };
            db.Emails.Add(email);
        }
        else
        {
            db.Links.RemoveRange(email.Links);
            db.Attachments.RemoveRange(email.Attachments);
            email.Links.Clear();
            email.Attachments.Clear();
        }

        email.Source = item.Source;
        email.Folder = item.Folder;
        email.FromAddress = item.FromAddress;
        email.FromName = item.FromName;
        email.Subject = item.Subject;
        email.ReceivedAt = item.ReceivedAt;
        email.Category = item.Category;
        email.Tags = [.. item.Tags ?? []];
        email.Fields = new Dictionary<string, string>(item.Fields ?? new Dictionary<string, string>());
        email.Title = item.Title ?? "";
        email.BodyText = item.BodyText;
        email.Status = EmailStatus.Done;
        email.StatusReason = null;
        email.FollowedChannelId = null; // channels are followed on the collector
        email.ProcessedAt = now;
        email.UpdatedAt = now;

        foreach (var l in item.Links ?? [])
        {
            email.Links.Add(new EmailLink
            {
                Url = l.Url, Kind = l.Kind, Position = l.Position, Status = l.Status, Error = l.Error, FinalUrl = l.FinalUrl,
                ContentType = l.ContentType, Title = l.Title, SiteName = l.SiteName, ContentText = l.ContentText, FetchedAt = l.FetchedAt,
                ExternalId = l.ExternalId, Author = l.Author, AuthorId = l.AuthorId, AuthorUrl = l.AuthorUrl, PublishedAt = l.PublishedAt,
                DurationSeconds = l.DurationSeconds, Description = l.Description, ContentSource = l.ContentSource, Chapters = l.Chapters,
                Summary = l.Summary, SummaryModel = l.SummaryModel, SummarizedAt = l.SummarizedAt,
            });
        }

        foreach (var a in item.Attachments ?? [])
        {
            var attachment = new EmailAttachment
            {
                FileName = AttachmentStore.SanitizeFileName(a.FileName), ContentType = a.ContentType, SizeBytes = a.SizeBytes, Sha256 = a.Sha256,
                StoredAsZip = a.StoredAsZip, ExtractedText = a.ExtractedText, Note = a.Note,
            };
            if (a.File is not null)
            {
                var entry = zip.GetEntry(a.File) ?? throw new BundleException($"{a.File} is listed but missing.");
                var bytes = await ReadAsync(entry, ct);
                var storedName = a.StoredAsZip ? attachment.FileName + ".zip" : attachment.FileName;
                attachment.StoredPath = await store.SaveAsync(email.Category, email.ReceivedAt, storedName, bytes, BundleFormat.Sha256(bytes), ct);
            }
            email.Attachments.Add(attachment);

            if (a.Image is { } image && a.Sha256 is not null && attachment.StoredPath is not null)
                await UpsertImageAsync(db, ImageDescription.KeyFor(a.Sha256), attachment.StoredPath, image, now, ct);
        }
        email.LinkCount = email.Links.Count;
        email.AttachmentCount = email.Attachments.Count;
    }

    /// <summary>The collector's description of an image (it had an AI), so this side doesn't describe it again.</summary>
    private static async Task UpsertImageAsync(NomadInboxDbContext db, string key, string storedPath, BundleImage image, DateTimeOffset now, CancellationToken ct)
    {
        var row = await db.ImageDescriptions.SingleOrDefaultAsync(d => d.ImageKey == key, ct)
                  ?? db.ImageDescriptions.Add(new ImageDescription { ImageKey = key, SourcePath = storedPath }).Entity;
        row.Source = ImageSource.Attachment;
        row.SourcePath = storedPath;
        row.Status = ImageDescriptionStatus.Done;
        row.Caption = image.Caption;
        row.ImageType = image.ImageType;
        row.Categories = [.. image.Categories ?? []];
        row.Tags = [.. image.Tags ?? []];
        row.ImageText = image.ImageText;
        row.Model = image.Model;
        row.Error = null;
        row.Attempts = 0;
        row.DescribedAt = now;
    }

    private async Task<ImportResult> RecordAsync(ImportedBundle record, ImportResult result, CancellationToken ct)
    {
        record.Status = result.Status;
        record.Items = result.Items;
        record.Skipped = result.Skipped;
        record.Message = result.Message;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.ImportedBundles.Add(record);
        await db.SaveChangesAsync(ct);
        if (result.Status == ImportStatus.Rejected) logger.LogWarning("Bundle {File} rejected: {Message}", record.FileName, result.Message);
        else logger.LogInformation("{Message}", result.Message);
        return result;
    }

    private static async Task<string> HashAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var stream = entry.Open();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > MaxEntryBytes || total > entry.Length) throw new BundleException($"{entry.FullName} is larger than it claims to be.");
            sha.AppendData(buffer, 0, read);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static async Task<byte[]> ReadAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var stream = entry.Open();
        using var buffer = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }
}

/// <summary>Air-gapped mode with the watched folder on: imports bundles placed in the import folder.</summary>
public sealed class BundleImportWorker(
    BundleImporter importer,
    SettingsService settingsService,
    ILogger<BundleImportWorker> logger) : BackgroundService
{
    /// <summary>A file still being copied is left alone until it hasn't changed for this long.</summary>
    private static readonly TimeSpan Settled = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var settings = await settingsService.GetAsync(stoppingToken);
                    if (settings.Mode == AppMode.AirGapped && settings.WatchImportFolder)
                        await ImportFolderAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Importing from the folder failed: {Message}", ex.Message);
                }
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Imports the folder's bundles, then moves each to "imported" or "rejected" beside them.</summary>
    public async Task<int> ImportFolderAsync(CancellationToken ct)
    {
        var folder = importer.ImportDirectory;
        if (!Directory.Exists(folder)) return 0;
        var count = 0;
        foreach (var path in Directory.GetFiles(folder, "*.zip").Order(StringComparer.OrdinalIgnoreCase))
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < Settled) continue;
            var result = await importer.ImportAsync(path, Path.GetFileName(path), ct);
            Move(path, Path.Combine(folder, result.Status == ImportStatus.Rejected ? "rejected" : "imported"));
            count++;
        }
        return count;
    }

    private static void Move(string path, string folder)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Path.GetFileName(path));
        for (var n = 2; File.Exists(target); n++)
            target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(path)} ({n}){Path.GetExtension(path)}");
        File.Move(path, target);
    }
}
