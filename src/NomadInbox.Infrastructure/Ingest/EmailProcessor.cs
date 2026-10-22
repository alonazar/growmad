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
    LinkTrigger? linkTrigger = null)
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
                email.Attachments.Add(await FileAttachmentAsync(db, email, attachment, settings, ct));

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

    private async Task<EmailAttachment> FileAttachmentAsync(
        NomadInboxDbContext db, InboxEmail email, FetchedAttachment file, InboxSettings settings, CancellationToken ct)
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

        attachment.Sha256 = AttachmentStore.ComputeSha256(file.Content);
        var existing = await db.Attachments
            .Where(a => a.Sha256 == attachment.Sha256 && a.StoredPath != null && a.InboxEmailId != email.Id)
            .Select(a => a.StoredPath!)
            .FirstOrDefaultAsync(ct);

        if (existing is not null && store.Exists(existing))
        {
            attachment.StoredPath = existing;
            attachment.IsDuplicate = true;
        }
        else
        {
            attachment.StoredPath = await store.SaveAsync(email.Category, email.ReceivedAt, name, file.Content, attachment.Sha256, ct);
        }

        var extraction = textExtractor.Extract(name, file.ContentType, file.Content);
        attachment.ExtractedText = extraction.Text;
        attachment.Note = extraction.Note;
        return attachment;
    }
}
