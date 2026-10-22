using System.Threading.Channels;

namespace NomadInbox.Infrastructure.Ingest;

public enum PollerState { Starting, NotConfigured, Disabled, Idle, Polling, Error }

public sealed record PollerSnapshot(
    PollerState State,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? NextRunAt,
    IngestOutcome? LastOutcome,
    string? LastError,
    ProcessingOutcome? LastProcessing = null);

/// <summary>Live poller state for the dashboard. Thread-safe; raises <see cref="Changed"/> on every update.</summary>
public sealed class PollerStatus
{
    private readonly Lock _lock = new();
    private PollerSnapshot _current = new(PollerState.Starting, null, null, null, null, null);

    public event Action? Changed;

    public PollerSnapshot Current { get { lock (_lock) return _current; } }

    public void Update(Func<PollerSnapshot, PollerSnapshot> change)
    {
        lock (_lock) _current = change(_current);
        Changed?.Invoke();
    }
}

public enum PollTriggerReason { SettingsChanged, PollNow }

/// <summary>Wakes the poller early: "Poll now" from the UI, or a settings change.</summary>
public sealed class PollTrigger
{
    private readonly Channel<PollTriggerReason> _channel = Channel.CreateUnbounded<PollTriggerReason>();

    public void Signal(PollTriggerReason reason) => _channel.Writer.TryWrite(reason);

    /// <summary>Waits for a signal or the timeout. Returns null on timeout; PollNow wins if several signals queued up.</summary>
    public async Task<PollTriggerReason?> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != Timeout.InfiniteTimeSpan) cts.CancelAfter(timeout);

        PollTriggerReason reason;
        try
        {
            reason = await _channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        while (_channel.Reader.TryRead(out var extra))
            if (extra == PollTriggerReason.PollNow) reason = extra;
        return reason;
    }
}
