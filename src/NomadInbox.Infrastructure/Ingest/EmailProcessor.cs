using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NomadInbox.Core.Content;
using NomadInbox.Core.Domain;
using NomadInbox.Core.Mail;
using NomadInbox.Infrastructure.Data;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.Settings;

namespace NomadInbox.Infrastructure.Ingest;

public sealed record ProcessingOutcome(int Processed, int Failed);

/// <summary>
/// Turns Pending emails into Done ones: fetches the full message, keeps its text, extracts links,
/// files attachments (with text extraction), then labels the message in Gmail.
/// </summary>
public sealed class EmailProcessor(
    IDbContextFactory<NomadInboxDbContext> dbFactory,
    SettingsService settingsService,
    IMailboxReader reader,
    AttachmentStore store,
    TextExtractor textExtractor,
    ILogger<EmailProcessor> logger,
    LinkTrigger? linkTrigger = null,
    Pages.PageService? pages = null)
{
    public const int BatchSize = 25;
    private const int MaxBodyChars = 100_000;

    public async Task<ProcessingOutcome> ProcessPendingAsync(CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);
        var account = settingsService.CreateAccount(settings)
                      ?? throw new InvalidOperationException("The mail account is not configured.");

        List<int> pending;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // Only one processor runs, so anything still "Processing" was interrupted (e.g. by a restart).
            await db.Emails.Where(e => e.Status == EmailStatus.Processing)
                .ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, EmailStatus.Pending), ct);

            pending = await db.Emails.Where(e => e.Status == EmailStatus.Pending)
                .OrderBy(e => e.ReceivedAt).Select(e => e.Id).Take(BatchSize).ToListAsync(ct);
        }
        if (pending.Count == 0) return new ProcessingOutcome(0, 0);

        await using var session = await reader.OpenSessionAsync(account, ct);
        int processed = 0, failed = 0;
        foreach (var id in pending)
        {
            if (await ProcessOneAsync(session, id, settings, ct)) processed++; else failed++;
        }

        logger.LogInformation("Processed {Processed} email(s), {Failed} failed", processed, failed);
        if (processed > 0) linkTrigger?.Signal(); // new links to fetch and summarize
        return new ProcessingOutcome(processed, failed);
    }

    private async Task<bool> ProcessOneAsync(IMailboxSession session, int id, InboxSettings settings, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var email = await db.Emails.Include(e => e.Links).Include(e => e.Attachments).AsSplitQuery()
            .SingleAsync(e => e.Id == id, ct);

        email.Status = EmailStatus.Processing;
        email.Attempts++;
        email.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        bool success;
        try
        {
            var message = await session.FetchAsync(email.ImapUid, email.UidValidity, email.MessageId, ct)
                          ?? throw new InvalidOperationException("The message is no longer in the mailbox folder.");
            email.ImapUid = message.Uid;
            email.UidValidity = session.UidValidity;

            var body = string.IsNullOrWhiteSpace(message.TextBody) ? HtmlText.ToPlainText(message.HtmlBody) : message.TextBody.Trim();
            email.BodyText = body.Length > MaxBodyChars ? body[..MaxBodyChars] : body;

            email.Links.Clear();
            var position = 0;
            foreach (var link in LinkExtractor.Extract(message.TextBody, message.HtmlBody))
                email.Links.Add(new EmailLink { Url = link.Url, Kind = link.Kind, Position = position++ });

            email.Attachments.Clear();
            foreach (var attachment in message.Attachments)
                email.Attachments.Add(await FileAttachmentAsync(db, email, attachment, settings, store, textExtractor, ct));

            email.LinkCount = email.Links.Count;
            email.AttachmentCount = email.Attachments.Count;
            email.Status = EmailStatus.Done;
            email.StatusReason = null;
            email.ProcessedAt = DateTimeOffset.UtcNow;
            success = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Processing email {Id} ({Subject}) failed", email.Id, email.Subject);
            db.ChangeTracker.Clear();
            email = await db.Emails.SingleAsync(e => e.Id == id, ct);
            email.Status = EmailStatus.Failed;
            email.StatusReason = ex.Message;
            success = false;
        }

        email.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // Saved pages: web links of #archive emails (or all, per Settings) get a local copy.
        if (success && pages is not null)
        {
            try { await pages.QueueForEmailAsync(email, settings, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Queueing pages failed: {Message}", ex.Message); }
        }

        if (settings.ApplyLabels)
        {
            var label = success ? settings.ProcessedLabel : settings.FailedLabel;
            if (!string.IsNullOrWhiteSpace(label))
            {
                try
                {
                    await session.AddLabelAsync(email.ImapUid, label.Trim(), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A label is a convenience; never fail the email over it.
                    logger.LogWarning(ex, "Could not add Gmail label {Label} to email {Id}", label, email.Id);
                }
            }
        }

        return success;
    }

    /// <summary>Files one attachment: size and kind checks, de-duplication, storing, text extraction.</summary>
    public static async Task<EmailAttachment> FileAttachmentAsync(
        NomadInboxDbContext db, InboxEmail email, FetchedAttachment file, InboxSettings settings, AttachmentStore store, TextExtractor textExtractor, CancellationToken ct)
    {
        var name = AttachmentStore.SanitizeFileName(file.FileName);
        var attachment = new EmailAttachment
        {
            FileName = name,
            ContentType = file.ContentType,
            SizeBytes = file.Content.LongLength,
        };

        var maxBytes = Math.Max(1, settings.MaxAttachmentMegabytes) * 1024L * 1024L;
        if (file.Content.LongLength > maxBytes)
        {
            attachment.Note = $"Not stored: larger than {settings.MaxAttachmentMegabytes} MB.";
            return attachment;
        }

        var kind = AttachmentClassifier.Classify(name, file.ContentType, file.Content);
        if (!settings.AllowedAttachmentKinds.HasFlag(kind))
        {
            attachment.Note = $"Not stored: {KindName(kind)} aren't allowed (Settings → Attachments).";
            return attachment;
        }

        attachment.Sha256 = AttachmentStore.ComputeSha256(file.Content);
        var existing = await db.Attachments
            .Where(a => a.Sha256 == attachment.Sha256 && a.StoredPath != null && a.InboxEmailId != email.Id)
            .Select(a => new { Path = a.StoredPath!, a.StoredAsZip })
            .FirstOrDefaultAsync(ct);

        if (existing is not null && store.Exists(existing.Path))
        {
            attachment.StoredPath = existing.Path;
            attachment.StoredAsZip = existing.StoredAsZip;
            attachment.IsDuplicate = true;
        }
        else if (kind == AttachmentKinds.Executables)
        {
            // Zipped, so opening it from the files folder or File Browser can't run it.
            var zip = ZipSingleFile(name, file.Content, email.ReceivedAt);
            attachment.StoredPath = await store.SaveAsync(email.Category, email.ReceivedAt, name + ".zip", zip, AttachmentStore.ComputeSha256(zip), ct);
            attachment.StoredAsZip = true;
        }
        else
        {
            attachment.StoredPath = await store.SaveAsync(email.Category, email.ReceivedAt, name, file.Content, attachment.Sha256, ct);
        }

        if (kind == AttachmentKinds.Executables)
        {
            attachment.Note = attachment.StoredAsZip ? "Program or script: stored inside a zip." : null;
            return attachment;
        }

        var extraction = textExtractor.Extract(name, file.ContentType, file.Content);
        attachment.ExtractedText = extraction.Text;
        attachment.Note = extraction.Note;
        return attachment;
    }

    private static string KindName(AttachmentKinds kind) => kind switch
    {
        AttachmentKinds.Executables => "programs and scripts",
        AttachmentKinds.Images => "images",
        AttachmentKinds.Video => "videos",
        AttachmentKinds.Audio => "audio files",
        AttachmentKinds.Documents => "documents",
        AttachmentKinds.Archives => "archives",
        _ => "files of this type",
    };

    // The entry keeps the email's date so the same file always zips to the same bytes (the store de-duplicates by content).
    private static byte[] ZipSingleFile(string name, byte[] content, DateTimeOffset modified)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = modified.Year is >= 1980 and <= 2107 ? modified : new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var stream = entry.Open();
            stream.Write(content);
        }
        return buffer.ToArray();
    }
}
