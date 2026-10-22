using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace NomadInbox.Infrastructure.Web;

public sealed record RenderedPage(Uri FinalUrl, string Html);

/// <summary>Loads a page in a real browser so JavaScript-built content (SPAs, Blazor/WASM apps) exists.</summary>
public interface IPageRenderer
{
    Task<RenderedPage> RenderAsync(string url, bool allowPrivateNetwork, CancellationToken ct);
}

/// <summary>
/// Headless Chromium via Playwright. A fresh browser per render (renders are rare and this keeps nothing
/// running in between). Every request the page makes passes the same local-network check as
/// <see cref="PageFetcher"/>, and images, fonts and media are blocked to save time.
/// </summary>
public sealed class PlaywrightRenderer(ILogger<PlaywrightRenderer> logger) : IPageRenderer, IAsyncDisposable
{
    /// <summary>Installs the headless shell and its OS packages; run once while building the Docker image.</summary>
    public static int InstallBrowser() => Microsoft.Playwright.Program.Main(["install", "--with-deps", "--only-shell", "chromium"]);

    /// <summary>Starts the browser, runs a line of script, and reports; 0 when it works.</summary>
    public static async Task<int> CheckBrowserAsync()
    {
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, Args = ["--disable-dev-shm-usage"] });
            var page = await browser.NewPageAsync();
            await page.SetContentAsync("<p id=x></p><script>document.getElementById('x').textContent = 'ok'</script>");
            var text = await page.TextContentAsync("#x");
            Console.WriteLine($"Headless browser works ({browser.Version}): script output \"{text}\"");
            return text == "ok" ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Headless browser check failed: {ex.Message}");
            return 1;
        }
    }

    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan NetworkIdleTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _gate = new(1, 1); // one browser at a time
    private IPlaywright? _playwright;

    public async Task<RenderedPage> RenderAsync(string url, bool allowPrivateNetwork, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _playwright ??= await Playwright.CreateAsync();
            IBrowser browser;
            try
            {
                browser = await _playwright.Chromium.LaunchAsync(new()
                {
                    Headless = true,
                    Args = ["--disable-dev-shm-usage"], // Docker's /dev/shm is only 64 MB
                });
            }
            catch (PlaywrightException ex)
            {
                throw new LinkFetchException("The headless browser isn't installed, so JavaScript pages can't be rendered.", ex);
            }

            await using (browser)
            {
                await using var context = await browser.NewContextAsync(new()
                {
                    Locale = "en-US",
                    ViewportSize = new() { Width = 1280, Height = 900 },
                });
                var hostChecks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                await context.RouteAsync("**/*", async route =>
                {
                    var request = route.Request;
                    if (request.ResourceType is "image" or "media" or "font")
                    {
                        await route.AbortAsync();
                        return;
                    }
                    if (!allowPrivateNetwork && Uri.TryCreate(request.Url, UriKind.Absolute, out var u)
                        && u.Scheme is "http" or "https" && !await IsPublicHostAsync(u.Host, hostChecks))
                    {
                        logger.LogDebug("Blocked {Url} (local network)", request.Url);
                        await route.AbortAsync("blockedbyclient");
                        return;
                    }
                    await route.ContinueAsync();
                });

                var page = await context.NewPageAsync();
                try
                {
                    await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.Load, Timeout = (float)LoadTimeout.TotalMilliseconds });
                }
                catch (PlaywrightException ex)
                {
                    throw new LinkFetchException(ex.Message.Contains("ERR_BLOCKED_BY_CLIENT")
                        ? "The page is on a local network. Turn on \"Allow links to local-network addresses\" in Settings to fetch it."
                        : $"The browser couldn't load the page: {FirstLine(ex.Message)}", ex);
                }

                try
                {
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = (float)NetworkIdleTimeout.TotalMilliseconds });
                }
                catch (TimeoutException)
                {
                    // Pages with live connections never go idle; carry on with what has loaded.
                }

                await WaitForStableTextAsync(page, ct);
                return new RenderedPage(new Uri(page.Url), await page.ContentAsync());
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Single-page apps keep rendering after the network goes quiet: wait until the visible text stops changing.</summary>
    private static async Task WaitForStableTextAsync(IPage page, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + SettleTimeout;
        var last = -1;
        var stableFor = 0;
        while (DateTime.UtcNow < deadline)
        {
            var length = await page.EvaluateAsync<int>("() => document.body ? document.body.innerText.length : 0");
            stableFor = length == last ? stableFor + 1 : 0;
            if (stableFor >= 2 && length > 0) return; // unchanged for ~2 s
            last = length;
            await Task.Delay(1000, ct);
        }
    }

    private static async Task<bool> IsPublicHostAsync(string host, Dictionary<string, bool> cache)
    {
        if (cache.TryGetValue(host, out var known)) return known;
        bool result;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            result = PageFetcher.IsPublic(ip);
        }
        else
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host);
                result = addresses.Length > 0 && addresses.All(PageFetcher.IsPublic);
            }
            catch
            {
                result = false;
            }
        }
        cache[host] = result;
        return result;
    }

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();

    public ValueTask DisposeAsync()
    {
        _playwright?.Dispose();
        return ValueTask.CompletedTask;
    }
}
