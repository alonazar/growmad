using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NomadInbox.Infrastructure.Settings;

namespace NomadInbox.Infrastructure.Ingest;

/// <summary>Wakes the link worker early: new links were recorded, a retry was requested, or settings changed.</summary>
public sealed class LinkTrigger : WorkSignal;

/// <summary>Wakes the video download worker: a video was queued.</summary>
public sealed class VideoTrigger : WorkSignal;

/// <summary>A coalescing wake-up signal for a background worker.</summary>
public abstract class WorkSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    public void Signal() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }
}

/// <summary>
/// Runs <see cref="LinkProcessor"/> separately from the mail poller, because summaries can take minutes
/// and mail checks shouldn't wait for them.
/// </summary>
public sealed class LinkWorker(
    LinkProcessor processor,
    LinkTrigger trigger,
    SettingsService settingsService,
    ILogger<LinkWorker> logger,
    Knowledge.KnowledgeTrigger? knowledgeTrigger = null) : BackgroundService
{
    /// <summary>Also re-checks on this interval, so links waiting for the AI get summarized once Ollama is up.</summary>
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        settingsService.Changed += trigger.Signal;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                LinkOutcome outcome;
                try
                {
                    outcome = await processor.RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Link processing failed");
                    outcome = new LinkOutcome(0, 0, 0);
                }

                if (outcome.Summarized > 0) knowledgeTrigger?.Signal(); // new summaries for NOMAD's knowledge base
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
