using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NomadInbox.Infrastructure.YouTube;

public sealed record YtDlpResult(int ExitCode, string Output, string Error);

/// <summary>
/// Runs the yt-dlp executable. YouTube needs a JavaScript runtime to solve its player challenges; the Node.js
/// binary that ships with Playwright is used for that, so nothing extra has to be installed.
/// </summary>
public sealed class YtDlp(ILogger<YtDlp> logger)
{
    /// <summary>Standalone binary in the Docker image, writable by the app so it can update itself.</summary>
    public const string ImagePath = "/opt/yt-dlp/yt-dlp";

    public string Executable { get; } = Environment.GetEnvironmentVariable("YTDLP_PATH")
                                        ?? (File.Exists(ImagePath) ? ImagePath : "yt-dlp");

    private readonly string? _node = FindPlaywrightNode();

    public async Task<YtDlpResult> RunAsync(IEnumerable<string> args, string? workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
        };
        foreach (var a in CommonArgs()) start.ArgumentList.Add(a);
        foreach (var a in args) start.ArgumentList.Add(a);

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new YouTubeException($"yt-dlp isn't available ({Executable}): {ex.Message}", ex);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new YouTubeException($"yt-dlp took longer than {timeout.TotalSeconds:0}s.");
        }

        var result = new YtDlpResult(process.ExitCode, await stdout, await stderr);
        if (result.ExitCode != 0)
            logger.LogDebug("yt-dlp exited {Code}: {Error}", result.ExitCode, result.Error);
        return result;
    }

    /// <summary>The most useful line of yt-dlp's error output ("ERROR: …"), for showing in the UI.</summary>
    public static string ErrorMessage(YtDlpResult result)
    {
        var lines = result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var error = lines.LastOrDefault(l => l.StartsWith("ERROR:")) ?? lines.LastOrDefault() ?? $"exit code {result.ExitCode}";
        return error.StartsWith("ERROR: ") ? error["ERROR: ".Length..] : error;
    }

    private IEnumerable<string> CommonArgs()
    {
        yield return "--ignore-config";
        yield return "--no-progress";
        yield return "--no-color";
        if (_node is not null)
        {
            yield return "--js-runtimes";
            yield return $"node:{_node}";
        }
    }

    private static string? FindPlaywrightNode()
    {
        var root = Path.Combine(AppContext.BaseDirectory, ".playwright", "node");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateFiles(root, OperatingSystem.IsWindows() ? "node.exe" : "node", SearchOption.AllDirectories)
            .FirstOrDefault();
    }
}
