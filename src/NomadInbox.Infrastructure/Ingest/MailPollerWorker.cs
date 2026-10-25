using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NomadInbox.Infrastructure.Settings;

namespace NomadInbox.Infrastructure.Ingest;

/// <summary>
/// Runs <see cref="MailIngestService"/> and then <see cref="EmailProcessor"/> on the configured interval,
/// or immediately when triggered.
/// </summary>
public sealed class MailPollerWorker(
    MailIngestService ingest,
    EmailProcessor processor,
    SettingsService settingsService,
    PollTrigger trigger,
    Knowledge.KnowledgeTrigger knowledgeTrigger,
    PollerStatus status,
    ILogger<MailPollerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        settingsService.Changed += OnSettingsChanged;
        try
        {
            PollTriggerReason? reason = null;
            while (!stoppingToken.IsCancellationRequested)
            {
                var settings = await settingsService.GetAsync(stoppingToken);
                var wait = Timeout.InfiniteTimeSpan;

                if (!settings.UsesInternet)
                {
                    status.Update(s => s with { State = PollerState.Disabled, NextRunAt = null }); // air-gapped: mail arrives in bundles
                }
                else if (!settings.IsMailConfigured)
                {
                    status.Update(s => s with { State = PollerState.NotConfigured, NextRunAt = null });
                }
                else if (!settings.PollingEnabled && reason != PollTriggerReason.PollNow)
                {
                    status.Update(s => s with { State = PollerState.Disabled, NextRunAt = null });
                }
                else
                {
                    await PollAsync(stoppingToken);
                    if (settings.PollingEnabled)
                    {
                        wait = TimeSpan.FromMinutes(Math.Max(1, settings.PollIntervalMinutes));
                        var next = DateTimeOffset.UtcNow + wait;
                        status.Update(s => s with { NextRunAt = next });
                    }
                    else
                    {
                        status.Update(s => s with { State = s.State == PollerState.Error ? PollerState.Error : PollerState.Disabled, NextRunAt = null });
                    }
                }

                reason = await trigger.WaitAsync(wait, stoppingToken);
                if (reason is not null) logger.LogInformation("Poller woken: {Reason}", reason);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            settingsService.Changed -= OnSettingsChanged;
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        status.Update(s => s with { State = PollerState.Polling, LastRunAt = started });
        try
        {
            var outcome = await ingest.RunOnceAsync(ct);
            status.Update(s => s with { LastOutcome = outcome });

            // Work through the backlog in batches; each batch reuses one IMAP connection.
            ProcessingOutcome total = new(0, 0);
            ProcessingOutcome batch;
            do
            {
                batch = await processor.ProcessPendingAsync(ct);
                total = new ProcessingOutcome(total.Processed + batch.Processed, total.Failed + batch.Failed);
            } while (batch.Processed + batch.Failed == EmailProcessor.BatchSize);

            status.Update(s => s with
            {
                State = PollerState.Idle,
                LastSuccessAt = DateTimeOffset.UtcNow,
                LastProcessing = total,
                LastError = null,
            });
            if (total.Processed > 0) knowledgeTrigger.Signal(); // attachments and notes for NOMAD's knowledge base
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Mail poll failed");
            status.Update(s => s with { State = PollerState.Error, LastError = ex.Message });
        }
    }

    private void OnSettingsChanged() => trigger.Signal(PollTriggerReason.SettingsChanged);
}
