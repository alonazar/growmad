using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NomadInbox.Core.Domain;
using NomadInbox.Core.Mail;
using NomadInbox.Core.Subjects;
using NomadInbox.Infrastructure.Data;
using NomadInbox.Infrastructure.Settings;

namespace NomadInbox.Infrastructure.Ingest;

public sealed record IngestOutcome(int Matched, int Added, int Ignored, int Duplicates);

/// <summary>
/// One poll: scan the mailbox for new matching messages and record them.
/// Phase 1 stops at recording; the processing pipeline (links, attachments, summaries) comes in Phase 2+.
/// </summary>
public sealed class MailIngestService(
    IDbContextFactory<NomadInboxDbContext> dbFactory,
    SettingsService settingsService,
    IMailboxReader reader,
    ILogger<MailIngestService> logger)
{
    public async Task<IngestOutcome> RunOnceAsync(CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);
        var account = settingsService.CreateAccount(settings)
                      ?? throw new InvalidOperationException("The mail account is not configured.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var cursor = await db.Cursors.FindAsync([settings.Folder], ct);

        var scan = await reader.ScanAsync(account, new MailScanRequest(
            settings.SubjectPrefix,
            cursor?.UidValidity ?? 0,
            cursor?.LastSeenUid ?? 0,
            settings.ImportSince), ct);

        var ids = scan.Matches.Select(m => m.MessageId).ToList();
        var known = (await db.Emails.Where(e => ids.Contains(e.MessageId)).Select(e => e.MessageId).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        // Deleted on purpose: don't bring them back on a rescan.
        known.UnionWith(await db.DeletedMessages.Where(d => ids.Contains(d.MessageId)).Select(d => d.MessageId).ToListAsync(ct));

        int added = 0, ignored = 0, duplicates = 0;
        foreach (var header in scan.Matches)
        {
            if (!known.Add(header.MessageId)) { duplicates++; continue; }

            var parsed = SubjectParser.Parse(header.Subject, settings.SubjectPrefix)!;
            var allowed = settings.IsSenderAllowed(header.FromAddress);

            db.Emails.Add(new InboxEmail
            {
                MessageId = header.MessageId,
                ImapUid = header.Uid,
                UidValidity = scan.UidValidity,
                Folder = settings.Folder,
                FromAddress = header.FromAddress,
                FromName = header.FromName,
                Subject = header.Subject,
                ReceivedAt = header.ReceivedAt,
                Category = parsed.Category,
                Tags = parsed.Tags.ToList(),
                Fields = new Dictionary<string, string>(parsed.Fields),
                Title = parsed.Title,
                Status = allowed ? EmailStatus.Pending : EmailStatus.Ignored,
                StatusReason = allowed ? null : "Sender is not on the allowlist.",
            });

            if (allowed) added++; else ignored++;
        }

        if (cursor is null)
        {
            cursor = new MailboxCursor { Folder = settings.Folder };
            db.Cursors.Add(cursor);
        }
        cursor.UidValidity = scan.UidValidity;
        cursor.LastSeenUid = scan.HighestUid;
        cursor.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        var outcome = new IngestOutcome(scan.Matches.Count, added, ignored, duplicates);
        logger.LogInformation("Poll of {Folder} finished: {Outcome}", settings.Folder, outcome);
        return outcome;
    }

    /// <summary>Forgets the scan position so the next poll searches the whole folder again (duplicates are skipped).</summary>
    public async Task ResetCursorAsync(string folder, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Cursors.Where(c => c.Folder == folder).ExecuteDeleteAsync(ct);
        logger.LogInformation("Scan position for {Folder} reset", folder);
    }
}
