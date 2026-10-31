using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NomadInbox.Core.Domain;
using NomadInbox.Core.Mail;
using NomadInbox.Infrastructure.Data;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.Ingest;
using NomadInbox.Infrastructure.Knowledge;
using NomadInbox.Infrastructure.Settings;
using NomadInbox.Infrastructure.Web;

namespace NomadInbox.Infrastructure.Archive;

/// <summary>Wakes the Internet Archive worker: files were queued.</summary>
public sealed class ArchiveTrigger : WorkSignal;

/// <summary>A collection that can't be imported, with a message meant for the UI.</summary>
public sealed class ArchiveImportException(string message, Exception? inner = null) : Exception(message, inner);

/// <param name="State">What already happened to this file here: "Imported", "Queued", "Failed", "Deleted", or null when it's new.</param>
/// <param name="Suggested">Whether the link pointed at this file (or at no file in particular).</param>
public sealed record ArchivePreviewFile(string Name, string DownloadName, string? Md5, long Bytes, bool Scanned, string? State, bool Suggested);

public sealed record ArchivePreview(string Identifier, string Title, string? LicenseUrl, IReadOnlyList<ArchivePreviewFile> Files);

public enum ArchiveStep
{
    Idle,
    Done,
    /// <summary>A download failed and will be tried again; don't hurry.</summary>
    Retry,
}

/// <summary>
/// Imports the PDFs of an Internet Archive collection. The archive's metadata lists every file; the chosen ones are
/// queued and downloaded one at a time, each becoming an item with its PDF attached, so the whole PDF goes to NOMAD's
/// knowledge base and into bundles like any attachment. A scan is replaced by the searchable-text PDF the archive made.
/// </summary>
public sealed partial class InternetArchiveImporter(
    IDbContextFactory<NomadInboxDbContext> dbFactory,
    SettingsService settingsService,
    PageFetcher fetcher,
    AttachmentStore store,
    TextExtractor textExtractor,
    KnowledgeTrigger knowledgeTrigger,
    ArchiveTrigger trigger,
    ILogger<InternetArchiveImporter> logger,
    string baseUrl = "https://archive.org")
{
    public const int MaxAttempts = 3;
    private const long MaxMetadataBytes = 64 * 1024 * 1024;

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex Identifier();

    /// <summary>The collection and the rest of the path (a file or folder in it, if any) of an archive.org link; null if it isn't one.</summary>
    public static (string Identifier, string? Path)? ParseLink(string? link)
    {
        if (!Uri.TryCreate(link?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            uri.Host.ToLowerInvariant() is not ("archive.org" or "www.archive.org"))
            return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || segments[0] is not ("details" or "download") || !Identifier().IsMatch(segments[1])) return null;
        var rest = string.Join('/', segments.Skip(2).Select(Uri.UnescapeDataString));
        return (segments[1], rest.Length == 0 ? null : rest);
    }

    /// <summary>The collection's PDFs and what's already been done with them; throws <see cref="ArchiveImportException"/>.</summary>
    public async Task<ArchivePreview> PreviewAsync(string link, CancellationToken ct)
    {
        if (ParseLink(link) is not { } parsed)
            throw new ArchiveImportException("That isn't an Internet Archive link. It should look like https://archive.org/details/….");
        var settings = await settingsService.GetAsync(ct);
        if (!settings.UsesInternet) throw new ArchiveImportException("This NOMAD is air-gapped: import on the collector instead.");

        FetchedPage page;
        try
        {
            page = await fetcher.FetchAsync($"{baseUrl}/metadata/{parsed.Identifier}", settings.AllowPrivateNetworkLinks, ct, MaxMetadataBytes);
        }
        catch (LinkFetchException ex)
        {
            throw new ArchiveImportException($"Couldn't reach the Internet Archive: {ex.Message}", ex);
        }

        using var json = JsonDocument.Parse(page.Body);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("files", out var filesJson) || filesJson.ValueKind != JsonValueKind.Array)
            throw new ArchiveImportException($"The Internet Archive has no collection called {parsed.Identifier}.");
        var metadata = root.TryGetProperty("metadata", out var m) ? m : default;
        if (root.TryGetProperty("is_dark", out var dark) && dark.ValueKind == JsonValueKind.True || First(metadata, "access-restricted-item") == "true")
            throw new ArchiveImportException("This collection is restricted, so its files can't be downloaded.");

        var all = filesJson.EnumerateArray().Select(f => (
            Name: First(f, "name") ?? "",
            Source: First(f, "source"),
            Format: First(f, "format"),
            Md5: First(f, "md5"),
            Size: long.TryParse(First(f, "size"), out var size) ? size : 0)).ToList();
        var byName = all.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var pdfs = all.Where(f => f.Source == "original" && f.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (pdfs.Count == 0) throw new ArchiveImportException("This collection has no PDF files.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ArchiveFiles.AsNoTracking().Where(f => f.Identifier == parsed.Identifier)
            .ToDictionaryAsync(f => f.FileName, f => f.Status, ct);
        var ids = pdfs.Select(f => MessageIdFor(parsed.Identifier, f.Name)).ToList();
        var deleted = (await db.DeletedMessages.Where(d => ids.Contains(d.MessageId)).Select(d => d.MessageId).ToListAsync(ct)).ToHashSet();

        var linked = pdfs.Where(f => Matches(f.Name, parsed.Path)).Select(f => f.Name).ToHashSet();
        var files = pdfs.Select(f =>
        {
            var hasText = byName.TryGetValue(f.Name[..^4] + "_text.pdf", out var text) && text.Format == "Additional Text PDF";
            var state = deleted.Contains(MessageIdFor(parsed.Identifier, f.Name)) ? "Deleted"
                : rows.TryGetValue(f.Name, out var status) ? status == ArchiveFileStatus.Done ? "Imported" : status.ToString()
                : null;
            return new ArchivePreviewFile(f.Name, hasText ? text.Name : f.Name, hasText ? text.Md5 : f.Md5, hasText ? text.Size : f.Size,
                hasText, state, linked.Count == 0 || linked.Contains(f.Name));
        }).ToList();
        return new ArchivePreview(parsed.Identifier, First(metadata, "title") ?? parsed.Identifier, First(metadata, "licenseurl"), files);
    }

    /// <summary>Whether a link's path points at this file: its name, its name without ".pdf" (with anything after it, like a page number), or its folder.</summary>
    private static bool Matches(string name, string? path)
    {
        if (path is null) return false;
        var clean = path.TrimEnd('/');
        var stem = name[..^4];
        return clean == name || clean == stem || clean.StartsWith(stem + "/", StringComparison.Ordinal) || clean.StartsWith(name + "/", StringComparison.Ordinal) ||
               name.StartsWith(clean + "/", StringComparison.Ordinal);
    }

    /// <summary>A value that may be a string, a number, or a list (the archive uses all three): the first one, as text.</summary>
    private static string? First(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Array) value = value.EnumerateArray().FirstOrDefault();
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => null,
        };
    }

    /// <summary>Queues the files. Ones already imported, queued or deleted here are left alone; failed ones are tried again.</summary>
    public async Task<int> QueueAsync(ArchivePreview preview, IEnumerable<ArchivePreviewFile> files, string? category, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ArchiveFiles.Where(f => f.Identifier == preview.Identifier).ToDictionaryAsync(f => f.FileName, ct);
        var deleted = (await db.DeletedMessages.Select(d => d.MessageId).ToListAsync(ct)).ToHashSet();
        var cleanCategory = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        var queued = 0;
        foreach (var file in files)
        {
            if (deleted.Contains(MessageIdFor(preview.Identifier, file.Name))) continue;
            if (rows.TryGetValue(file.Name, out var row))
            {
                if (row.Status != ArchiveFileStatus.Failed) continue;
                row.Status = ArchiveFileStatus.Queued;
                row.Error = null;
                row.Attempts = 0;
                row.Category = cleanCategory;
            }
            else
            {
                db.ArchiveFiles.Add(new ArchiveFile
                {
                    Identifier = preview.Identifier, CollectionTitle = preview.Title, FileName = file.Name, DownloadName = file.DownloadName,
                    Md5 = file.Md5, Bytes = file.Bytes, Category = cleanCategory,
                });
            }
            queued++;
        }
        await db.SaveChangesAsync(ct);
        trigger.Signal();
        return queued;
    }

    /// <summary>Tries a collection's failed files again.</summary>
    public async Task<int> RetryFailedAsync(string identifier, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var count = await db.ArchiveFiles.Where(f => f.Identifier == identifier && f.Status == ArchiveFileStatus.Failed).ExecuteUpdateAsync(u => u
            .SetProperty(f => f.Status, ArchiveFileStatus.Queued)
            .SetProperty(f => f.Error, (string?)null)
            .SetProperty(f => f.Attempts, 0), ct);
        trigger.Signal();
        return count;
    }

    /// <summary>Downloads the next queued file and makes it an item.</summary>
    public async Task<ArchiveStep> ImportNextAsync(CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);
        if (!settings.UsesInternet) return ArchiveStep.Idle;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var file = await db.ArchiveFiles.Where(f => f.Status == ArchiveFileStatus.Queued)
            .OrderBy(f => f.Attempts).ThenBy(f => f.Id).FirstOrDefaultAsync(ct);
        if (file is null) return ArchiveStep.Idle;

        var messageId = MessageIdFor(file.Identifier, file.FileName);
        file.Attempts++;
        try
        {
            var existing = await db.Emails.Where(e => e.MessageId == messageId).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
            file.InboxEmailId = existing ?? await CreateItemAsync(db, file, messageId, settings, ct);
            file.Status = ArchiveFileStatus.Done;
            file.Error = null;
            file.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            knowledgeTrigger.Signal();
            logger.LogInformation("Imported {File} from the Internet Archive ({Identifier})", file.FileName, file.Identifier);
            return ArchiveStep.Done;
        }
        catch (ArchiveImportException ex)
        {
            // Trying again wouldn't help (too large, or that kind of file isn't allowed).
            file.Status = ArchiveFileStatus.Failed;
            file.Error = ex.Message;
            file.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return ArchiveStep.Done;
        }
        catch (Exception ex) when (ex is LinkFetchException or IOException or HttpRequestException or InvalidDataException)
        {
            file.Error = ex.Message;
            if (file.Attempts >= MaxAttempts)
            {
                file.Status = ArchiveFileStatus.Failed;
                file.FinishedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Importing {File} from the Internet Archive failed (try {Attempt}): {Message}", file.FileName, file.Attempts, ex.Message);
            return ArchiveStep.Retry;
        }
    }

    private async Task<int> CreateItemAsync(NomadInboxDbContext db, ArchiveFile file, string messageId, InboxSettings settings, CancellationToken ct)
    {
        var maxBytes = Math.Max(1, settings.MaxAttachmentMegabytes) * 1024L * 1024L;
        if (file.Bytes > maxBytes) throw new ArchiveImportException($"Not imported: larger than {settings.MaxAttachmentMegabytes} MB (Settings → Attachments).");

        var path = string.Join('/', file.DownloadName.Split('/').Select(Uri.EscapeDataString));
        var download = await fetcher.FetchAsync($"{baseUrl}/download/{file.Identifier}/{path}", settings.AllowPrivateNetworkLinks, ct, maxBytes);
        if (file.Md5 is not null && !file.Md5.Equals(Convert.ToHexString(MD5.HashData(download.Body)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The download doesn't match the checksum the archive lists.");

        var now = DateTimeOffset.UtcNow;
        var title = TitleFor(file.FileName);
        var email = new InboxEmail
        {
            MessageId = messageId, Source = ItemSource.InternetArchive, Folder = "Internet Archive", FromAddress = "archive.org", FromName = file.CollectionTitle,
            Subject = title, Title = title, Category = file.Category, ReceivedAt = now, ProcessedAt = now, Status = EmailStatus.Done,
            BodyText = $"From the Internet Archive: {file.CollectionTitle}\n{baseUrl}/details/{file.Identifier}",
        };
        var attachment = await EmailProcessor.FileAttachmentAsync(db, email, new FetchedAttachment(file.FileName.Split('/')[^1], "application/pdf", download.Body),
            settings, store, textExtractor, ct);
        if (attachment.StoredPath is null) throw new ArchiveImportException(attachment.Note ?? "Not stored.");
        email.Attachments.Add(attachment);
        email.AttachmentCount = 1;
        db.Emails.Add(email);
        await db.SaveChangesAsync(ct);
        return email.Id;
    }

    public static string MessageIdFor(string identifier, string fileName) => $"archive:{identifier}/{fileName}";

    /// <summary>A readable title from a file name: hyphens stand for spaces only in a name without any ("Alpine-Living-2008", not "4-h Beekeeping").</summary>
    public static string TitleFor(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName.Split('/')[^1]).Replace('_', ' ').Trim();
        return name.Contains(' ') ? name : name.Replace('-', ' ');
    }
}

/// <summary>Downloads queued files one at a time, with a pause so the archive isn't hammered.</summary>
public sealed class InternetArchiveWorker(InternetArchiveImporter importer, ArchiveTrigger trigger, ILogger<InternetArchiveWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var step = ArchiveStep.Idle;
                try
                {
                    step = await importer.ImportNextAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Internet Archive import failed");
                }
                switch (step)
                {
                    case ArchiveStep.Done: await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); break;
                    case ArchiveStep.Retry: await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); break;
                    default: await trigger.WaitAsync(TimeSpan.FromMinutes(5), stoppingToken); break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
