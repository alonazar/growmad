using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Data;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.Ingest;
using NomadInbox.Infrastructure.Settings;

namespace NomadInbox.Infrastructure.Knowledge;

public sealed record KnowledgeOutcome(int Uploaded, int Removed, int Indexed, int Failed)
{
    public bool DidWork => Uploaded + Removed > 0;
}

/// <summary>Wakes the knowledge sync: something was summarized, processed, deleted, or settings changed.</summary>
public sealed class KnowledgeTrigger : WorkSignal;

/// <summary>
/// Keeps NOMAD's knowledge base in step with NOMAD Inbox by reconciling, not by events:
/// each run compares the documents that *should* exist (<see cref="KnowledgeDocumentBuilder"/>) with what has been
/// sent (<see cref="KnowledgeDocument"/> rows), then uploads new ones, replaces changed ones, removes ones whose item
/// is gone, and refreshes embedding state from NOMAD. Restarts, deletes and edits all converge on the next run.
/// </summary>
public sealed class KnowledgeSyncService(
    IDbContextFactory<NomadInboxDbContext> dbFactory,
    SettingsService settingsService,
    INomadKnowledgeBase nomad,
    AttachmentStore store,
    ILogger<KnowledgeSyncService> logger)
{
    /// <summary>NOMAD embeds one file at a time; don't queue up hundreds at once.</summary>
    public const int UploadsPerRun = 10;

    public async Task<KnowledgeOutcome> RunOnceAsync(CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);
        if (!settings.KnowledgeBaseEnabled || !await nomad.IsAvailableAsync(settings.NomadUrl, ct))
            return new KnowledgeOutcome(0, 0, 0, 0);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sent = await db.KnowledgeDocuments.ToDictionaryAsync(d => d.ItemKey, ct);

        // 1. Refresh state (and learn NOMAD's id for each file) from NOMAD's own list.
        var remote = await nomad.ListFilesAsync(settings.NomadUrl, ct);
        var byFileName = remote.GroupBy(f => f.FileName).ToDictionary(g => g.Key, g => g.First());
        int indexed = 0, failed = 0;
        foreach (var doc in sent.Values.Where(d => d.NomadFilePath is not null))
        {
            if (!byFileName.TryGetValue(Path.GetFileName(doc.NomadFilePath!), out var file)) continue;
            doc.NomadSource = file.Source;
            doc.Chunks = file.Chunks;
            var state = file.State.ToLowerInvariant() switch
            {
                "indexed" or "completed" or "done" => KnowledgeState.Indexed,
                "failed" or "error" => KnowledgeState.Failed,
                _ => KnowledgeState.Uploaded,
            };
            if (state != doc.State)
            {
                if (state == KnowledgeState.Indexed) indexed++;
                if (state == KnowledgeState.Failed) { failed++; doc.Error = "NOMAD couldn't embed this file (see NOMAD's knowledge base page)."; }
                doc.State = state;
                doc.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        // 2. Work out what should be there.
        var emails = await db.Emails.AsNoTracking()
            .Where(e => e.Status == EmailStatus.Done)
            .Include(e => e.Links).Include(e => e.Attachments).AsSplitQuery()
            .ToListAsync(ct);
        // One item that can't be turned into documents is skipped, not allowed to stop the sync for everything else.
        var skipped = new HashSet<int>();
        var desired = emails.SelectMany(e =>
            {
                try
                {
                    return KnowledgeDocumentBuilder.Build(e, settings, store).ToList();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Skipping email {Id} in the knowledge base sync: {Message}", e.Id, ex.Message);
                    skipped.Add(e.Id);
                    return [];
                }
            })
            .GroupBy(d => d.ItemKey).ToDictionary(g => g.Key, g => g.First());

        // 3. Remove documents whose item is gone (deleted, reprocessed into something else, ...). A skipped email's
        // documents stay in NOMAD as they are.
        var removed = 0;
        foreach (var doc in sent.Values.Where(d => !desired.ContainsKey(d.ItemKey) && !skipped.Contains(d.InboxEmailId)).ToList())
        {
            if (await RemoveRemoteAsync(doc, settings, byFileName, ct))
            {
                db.KnowledgeDocuments.Remove(doc);
                removed++;
            }
        }

        // 4. Upload new and changed documents (replacing the old copy).
        var uploaded = 0;
        foreach (var want in desired.Values.OrderBy(d => d.InboxEmailId).ThenBy(d => d.ItemKey))
        {
            sent.TryGetValue(want.ItemKey, out var doc);
            // Same content already sent. A failure with the same content isn't retried forever: "Resend" clears the hash.
            if (doc is not null && doc.ContentHash == want.Hash) continue;
            if (uploaded >= UploadsPerRun) break;

            if (doc is null)
            {
                doc = new KnowledgeDocument { ItemKey = want.ItemKey, FileName = want.FileName };
                db.KnowledgeDocuments.Add(doc);
            }
            else if (!await RemoveRemoteAsync(doc, settings, byFileName, ct))
            {
                continue; // couldn't remove the old copy; try again next run rather than duplicating it
            }

            doc.InboxEmailId = want.InboxEmailId;
            doc.FileName = want.FileName;
            doc.Collection = want.Collection;
            try
            {
                await using var content = want.Open();
                var result = await nomad.UploadAsync(settings.NomadUrl, want.FileName, want.ContentType, content, want.Collection, ct);
                doc.NomadFilePath = result.FilePath;
                doc.NomadSource = null;
                doc.ContentHash = want.Hash;
                doc.State = KnowledgeState.Uploaded;
                doc.Chunks = 0;
                doc.Error = null;
                doc.UploadedAt = DateTimeOffset.UtcNow;
                uploaded++;
                logger.LogInformation("Sent {Key} to NOMAD's knowledge base as {File} (collection {Collection})",
                    want.ItemKey, result.FileName, want.Collection ?? "none");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("Sending {Key} to NOMAD failed: {Message}", want.ItemKey, ex.Message);
                doc.State = KnowledgeState.Failed;
                doc.ContentHash = want.Hash;
                doc.NomadFilePath = null;
                doc.Error = ex.Message;
                failed++;
            }
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        await db.SaveChangesAsync(ct);
        var outcome = new KnowledgeOutcome(uploaded, removed, indexed, failed);
        if (uploaded + removed + indexed + failed > 0) logger.LogInformation("Knowledge base: {Outcome}", outcome);
        return outcome;
    }

    /// <summary>Forget what was sent so the next run sends everything again (Settings → Resend everything).</summary>
    public async Task<int> ResendAllAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.KnowledgeDocuments.ExecuteUpdateAsync(u => u.SetProperty(d => d.ContentHash, (string?)null), ct);
    }

    public async Task<int> ResendEmailAsync(int emailId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.KnowledgeDocuments.Where(d => d.InboxEmailId == emailId)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.ContentHash, (string?)null), ct);
    }

    private async Task<bool> RemoveRemoteAsync(KnowledgeDocument doc, InboxSettings settings,
        IReadOnlyDictionary<string, NomadFile> remote, CancellationToken ct)
    {
        if (doc.NomadFilePath is null) return true; // never made it to NOMAD
        var source = doc.NomadSource
                     ?? (remote.TryGetValue(Path.GetFileName(doc.NomadFilePath), out var f) ? f.Source : null);
        if (source is null) return true; // NOMAD no longer lists it

        try
        {
            await nomad.DeleteAsync(settings.NomadUrl, source, ct);
            logger.LogInformation("Removed {Key} from NOMAD's knowledge base", doc.ItemKey);
            return true;
        }
        catch (NomadException ex)
        {
            logger.LogWarning("Removing {Key} from NOMAD failed: {Message}", doc.ItemKey, ex.Message);
            return false;
        }
    }
}

/// <summary>Runs the sync after changes (signalled) and every few minutes to pick up embedding progress.</summary>
public sealed class KnowledgeSyncWorker(
    KnowledgeSyncService sync,
    KnowledgeTrigger trigger,
    SettingsService settingsService,
    ILogger<KnowledgeSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        settingsService.Changed += trigger.Signal;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var outcome = new KnowledgeOutcome(0, 0, 0, 0);
                try
                {
                    outcome = await sync.RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Knowledge base sync failed: {Message}", ex.Message);
                }
                if (!outcome.DidWork) await trigger.WaitAsync(Idle, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            settingsService.Changed -= trigger.Signal;
        }
    }
}
