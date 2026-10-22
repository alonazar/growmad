using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Data;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.YouTube;

namespace NomadInbox.Infrastructure.Ingest;

public sealed record DeletionOutcome(bool Found, int FilesDeleted);

/// <summary>
/// Deletes an email (or followed-channel item) with its links and attachments. Optionally removes its files from disk,
/// but only files no other item still uses (attachments are de-duplicated across emails). The Message-ID is remembered
/// so a folder rescan or channel check doesn't bring it back. The knowledge-base sync removes its documents from NOMAD.
/// </summary>
public sealed class ItemDeletionService(
    IDbContextFactory<NomadInboxDbContext> dbFactory,
    AttachmentStore attachments,
    VideoDownloader videos,
    ILogger<ItemDeletionService> logger,
    Pages.PageService? pages = null)
{
    public async Task<DeletionOutcome> DeleteAsync(int emailId, bool deleteFiles, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var email = await db.Emails.Include(e => e.Links).Include(e => e.Attachments).AsSplitQuery()
            .SingleOrDefaultAsync(e => e.Id == emailId, ct);
        if (email is null) return new DeletionOutcome(false, 0);

        var filesDeleted = 0;
        if (deleteFiles)
        {
            foreach (var path in email.Attachments.Select(a => a.StoredPath).OfType<string>().Distinct())
            {
                var shared = await db.Attachments.AnyAsync(a => a.StoredPath == path && a.InboxEmailId != emailId, ct);
                if (!shared && attachments.ResolveFullPath(path) is { } full && TryDelete(full)) filesDeleted++;
            }
            foreach (var path in email.Links.Select(l => l.VideoPath).OfType<string>().Distinct())
            {
                var shared = await db.Links.AnyAsync(l => l.VideoPath == path && l.InboxEmailId != emailId, ct);
                if (shared || videos.ResolveFullPath(path) is not { } video) continue;
                var stem = Path.Combine(Path.GetDirectoryName(video)!, Path.GetFileNameWithoutExtension(video));
                foreach (var file in new[] { video, stem + ".nfo", stem + "-thumb.jpg" })
                    if (TryDelete(file)) filesDeleted++;
            }
        }

        if (!await db.DeletedMessages.AnyAsync(d => d.MessageId == email.MessageId, ct))
            db.DeletedMessages.Add(new DeletedMessage { MessageId = email.MessageId });
        db.Emails.Remove(email); // links and attachments cascade
        await db.SaveChangesAsync(ct);

        // Saved pages that came from this email: deleted along with its files, otherwise kept (without an email).
        var pageIds = await db.Pages.Where(p => p.InboxEmailId == emailId).Select(p => p.Id).ToListAsync(ct);
        if (deleteFiles && pages is not null)
        {
            foreach (var pageId in pageIds) await pages.DeleteAsync(pageId, ct);
            filesDeleted += pageIds.Count;
        }
        else if (pageIds.Count > 0)
        {
            await db.Pages.Where(p => p.InboxEmailId == emailId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.InboxEmailId, (int?)null), ct);
        }

        logger.LogInformation("Deleted item {Id} \"{Subject}\" ({Files} file(s) removed)", emailId, email.Subject, filesDeleted);
        return new DeletionOutcome(true, filesDeleted);
    }

    private bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not delete {Path}: {Message}", path, ex.Message);
            return false;
        }
    }
}
