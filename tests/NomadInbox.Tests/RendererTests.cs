using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Ai;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.Ingest;
using NomadInbox.Infrastructure.Web;

namespace NomadInbox.Tests;

/// <summary>Needs headless Chromium: run `dotnet src/NomadInbox.Web/bin/Debug/net10.0/NomadInbox.Web.dll install-browser` once.</summary>
[Trait("Category", "Browser")]
public sealed class PlaywrightRendererTests : IAsyncDisposable
{
    private readonly TestWebServer _server = new();
    private readonly PlaywrightRenderer _renderer = new(NullLogger<PlaywrightRenderer>.Instance);

    // Like a WebAssembly app: the server sends a loading shell, the content appears only after scripts run.
    private const string SlowAppPage = """
        <html><head><title>Event Finder</title></head>
        <body><div id="app">Loading…</div>
        <script>
          setTimeout(() => {
            const app = document.getElementById('app');
            app.innerHTML = '<article><h1>Spring Trail Run</h1>' +
              Array.from({ length: 8 }, (_, i) => '<p>Stage ' + (i + 1) + ': runners follow the marked forest trail ' +
                'past the reservoir, carry at least one litre of water, and check in at the aid station.</p>').join('') +
              '</article>';
          }, 2500);
        </script></body></html>
        """;

    public async ValueTask DisposeAsync()
    {
        await _renderer.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Waits_for_script_built_content()
    {
        _server.Add("/app", "text/html", SlowAppPage);

        var page = await _renderer.RenderAsync(_server.Url("/app"), allowPrivateNetwork: true, default);
        var content = new ReadableText(new TextExtractor()).ExtractHtml(page.FinalUrl, page.Html);

        Assert.Contains("Stage 8: runners follow the marked forest trail", content.Text);
        Assert.DoesNotContain("Loading…", content.Text);
    }

    [Fact]
    public async Task Refuses_local_network_unless_allowed()
    {
        _server.Add("/app", "text/html", SlowAppPage);

        var ex = await Assert.ThrowsAsync<LinkFetchException>(() => _renderer.RenderAsync(_server.Url("/app"), false, default));

        Assert.Contains("local network", ex.Message);
    }
}

public sealed class LinkRenderingTests : IAsyncLifetime
{
    private readonly TestWebServer _server = new();
    private readonly PageFetcher _fetcher = new();
    private readonly FakeRenderer _renderer = new();
    private TestDatabase _db = default!;
    private LinkProcessor _processor = default!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        var ollama = new FakeOllama();
        _processor = new LinkProcessor(_db.Factory, _db.Settings, _fetcher, new ReadableText(new TextExtractor()),
            new Summarizer(ollama), ollama, NullLogger<LinkProcessor>.Instance, _renderer);
        await _db.Settings.SaveAsync(new InboxSettings { AllowPrivateNetworkLinks = true }, null);
    }

    public async Task DisposeAsync()
    {
        _fetcher.Dispose();
        await _server.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Thin_pages_are_rendered_and_the_richer_text_is_kept()
    {
        _server.Add("/app", "text/html", "<html><body><div id=app>🎬 DEMO MODE</div><script src=app.js></script></body></html>");
        _renderer.Html = "<html><head><title>Events</title></head><body><article>" +
                         string.Concat(Enumerable.Repeat("<p>Find running and cycling events near you, filter by distance and date.</p>", 10)) +
                         "</article></body></html>";
        var id = await AddLinkAsync(_server.Url("/app"));

        await _processor.RunOnceAsync(default);

        var link = await LoadAsync(id);
        Assert.Equal(1, _renderer.Calls);
        Assert.Equal(LinkStatus.Fetched, link.Status);
        Assert.Contains("filter by distance", link.ContentText);
        Assert.Contains("headless browser", link.Error);
    }

    [Fact]
    public async Task Rich_pages_and_disabled_setting_skip_rendering()
    {
        _server.Add("/article", "text/html", "<html><body><article>" +
            string.Concat(Enumerable.Repeat("<p>Plenty of server-rendered text about water filters and how to clean them.</p>", 12)) +
            "</article></body></html>");
        await AddLinkAsync(_server.Url("/article"));
        var settings = await _db.Settings.GetAsync();
        settings.RenderJavaScriptPages = false;
        await _db.Settings.SaveAsync(settings, null);
        _server.Add("/thin", "text/html", "<html><body>Loading…</body></html>");
        await AddLinkAsync(_server.Url("/thin"));

        await _processor.RunOnceAsync(default);

        Assert.Equal(0, _renderer.Calls);
    }

    [Fact]
    public async Task Render_failure_keeps_the_plain_text()
    {
        _server.Add("/app", "text/html", "<html><body>Loading the app…</body></html>");
        _renderer.Error = new LinkFetchException("boom");
        var id = await AddLinkAsync(_server.Url("/app"));

        await _processor.RunOnceAsync(default);

        var link = await LoadAsync(id);
        Assert.Equal(LinkStatus.Fetched, link.Status);
        Assert.Contains("Loading the app", link.ContentText);
        Assert.Contains("rendering it failed: boom", link.Error);
    }

    private async Task<int> AddLinkAsync(string url)
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        var email = await db.Emails.FirstOrDefaultAsync() ?? db.Emails.Add(new InboxEmail
        {
            MessageId = "m@x", Folder = "INBOX", FromAddress = "me@gmail.com", Subject = "NOMAD: x", ReceivedAt = DateTimeOffset.UtcNow,
        }).Entity;
        var link = new EmailLink { Url = url, Kind = LinkKind.Web };
        email.Links.Add(link);
        await db.SaveChangesAsync();
        return link.Id;
    }

    private async Task<EmailLink> LoadAsync(int id)
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        return await db.Links.SingleAsync(l => l.Id == id);
    }

    private sealed class FakeRenderer : IPageRenderer
    {
        public string Html { get; set; } = "<html></html>";
        public Exception? Error { get; set; }
        public int Calls { get; private set; }

        public Task<RenderedPage> RenderAsync(string url, bool allowPrivateNetwork, CancellationToken ct)
        {
            Calls++;
            return Error is not null ? Task.FromException<RenderedPage>(Error) : Task.FromResult(new RenderedPage(new Uri(url), Html));
        }
    }
}
