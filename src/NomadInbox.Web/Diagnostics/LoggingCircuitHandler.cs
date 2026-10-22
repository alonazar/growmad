using Microsoft.AspNetCore.Components.Server.Circuits;

namespace NomadInbox.Web.Diagnostics;

/// <summary>
/// Logs when a browser's live Blazor connection opens, drops and comes back. Interactive pages
/// (dashboard, settings) only work while it's up, so this is the first thing to check when buttons do nothing.
/// </summary>
public sealed class LoggingCircuitHandler(ILogger<LoggingCircuitHandler> logger) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken ct)
    {
        logger.LogInformation("Browser connection {Circuit} opened", circuit.Id[..8]);
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken ct)
    {
        logger.LogInformation("Browser connection {Circuit} dropped", circuit.Id[..8]);
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken ct)
    {
        logger.LogInformation("Browser connection {Circuit} up", circuit.Id[..8]);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken ct)
    {
        logger.LogInformation("Browser connection {Circuit} closed", circuit.Id[..8]);
        return Task.CompletedTask;
    }
}
