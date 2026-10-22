using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Ai;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.Ingest;
using NomadInbox.Infrastructure.Web;

namespace NomadInbox.Tests;

public sealed class FakeOllama : IOllamaApi
{
    public bool Up { get; set; } = true;
    public Func<string, string, string> Reply { get; set; } = (_, _) => "**A page.**\n- point";
    public List<(string Model, string System, string User)> Calls { get; } = [];

    public Task<string> VersionAsync(string baseUrl, CancellationToken ct) =>
        Up ? Task.FromResult("0.0-test") : throw new OllamaException("Can't reach Ollama");

    public Task<string> ChatAsync(string baseUrl, string model, string system, string user, int contextTokens, CancellationToken ct)
    {
        Calls.Add((model, system, user));
        return Task.FromResult(Reply(system, user));
    }
}

public class SummarizerTests
{
    private static readonly SummaryContext Context = new("Water", "https://example.com/water", "Survival/Water", ["diy"], "Filters");

    [Fact]
    public void Split_keeps_paragraphs_together_and_respects_the_limit()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 30).Select(i => $"Paragraph {i} " + new string('x', 80)));

        var chunks = Summarizer.Split(text, 500);

        Assert.All(chunks, c => Assert.True(c.Length <= 500));
        Assert.Equal(text.Replace("\n\n", ""), string.Concat(chunks).Replace("\n\n", ""));
        Assert.StartsWith("Paragraph 1 ", chunks[0]);
    }

    [Fact]
    public void Split_cuts_a_huge_single_line()
    {
        var chunks = Summarizer.Split(new string('a', 1200), 500);

        Assert.Equal([500, 500, 200], chunks.Select(c => c.Length));
    }

    [Fact]
    public async Task Short_text_is_one_call_with_context_in_the_prompt()
    {
        var ollama = new FakeOllama();
        var settings = new InboxSettings { OllamaModel = "gemma3:12b" };

        var result = await new Summarizer(ollama).SummarizeAsync(settings, Context, "Boil water for one minute.", default);

        var call = Assert.Single(ollama.Calls);
        Assert.Equal("gemma3:12b", call.Model);
        Assert.Equal(Summarizer.DefaultPrompt, call.System);
        Assert.Contains("Boil water for one minute.", call.User);
        Assert.Contains("Survival/Water, #diy, Filters", call.User);
        Assert.Equal(1, result.Chunks);
        Assert.Equal("**A page.**\n- point", result.Markdown);
    }

    [Fact]
    public async Task Long_text_is_condensed_per_chunk_then_combined()
    {
        var ollama = new FakeOllama { Reply = (system, user) => system == Summarizer.DefaultPrompt ? "final" : "- notes" };
        var settings = new InboxSettings { OllamaModel = "m", OllamaContextTokens = 2048, SummaryPrompt = Summarizer.DefaultPrompt };
        var chunkChars = Summarizer.CharsPerChunk(2048);
        var text = string.Join("\n\n", Enumerable.Range(0, 3).Select(i => new string((char)('a' + i), chunkChars - 10)));

        var result = await new Summarizer(ollama).SummarizeAsync(settings, Context, text, default);

        Assert.Equal(3, result.Chunks);
        Assert.Equal(4, ollama.Calls.Count); // 3 chunk notes + 1 final
        Assert.Contains("Notes from part 3", ollama.Calls[^1].User);
        Assert.Equal("final", result.Markdown);
    }

    [Fact]
    public async Task Custom_prompt_is_used_and_empty_reply_is_an_error()
    {
        var ollama = new FakeOllama { Reply = (_, _) => "  " };
        var settings = new InboxSettings { OllamaModel = "m", SummaryPrompt = "Be brief." };

        await Assert.ThrowsAsync<OllamaException>(() => new Summarizer(ollama).SummarizeAsync(settings, Context, "text", default));
        Assert.Equal("Be brief.", ollama.Calls.Single().System);
    }

    [Fact]
    public void Thinking_blocks_are_removed()
    {
        Assert.Equal("answer", OllamaClient.StripThinking("<think>hmm\nlet me see</think>answer").Trim());
    }
}

public class ReadableTextTests
{
    private readonly ReadableText _readable = new(new TextExtractor());

    private static FetchedPage Page(string type, byte[] body, string? charset = null, string url = "https://example.com/guide") =>
        new(new Uri(url), type, charset, body);

    [Fact]
    public void Extracts_the_article_not_the_navigation()
    {
        var paragraphs = string.Concat(Enumerable.Range(1, 8).Select(i =>
            $"<p>Step {i}: rinse the ceramic filter element under clean running water and scrub it gently with a soft brush, " +
            "never with soap, then let it air dry completely before reassembling the filter housing.</p>"));
        var html = $"""
            <html><head><title>Cleaning ceramic filters | Prep Site</title></head>
            <body><nav><a href="/">Home</a> <a href="/shop">Shop</a> <a href="/login">Login</a></nav>
            <article><h1>Cleaning ceramic filters</h1>{paragraphs}</article>
            <footer>Copyright 2026 · Privacy · Cookies</footer></body></html>
            """;

        var content = _readable.Extract(Page("text/html", Encoding.UTF8.GetBytes(html)))!;

        Assert.Contains("Cleaning ceramic filters", content.Title);
        Assert.Contains("Step 8: rinse the ceramic filter", content.Text);
        Assert.DoesNotContain("Privacy", content.Text);
    }

    [Fact]
    public void Honours_the_meta_charset()
    {
        var html = "<html><head><meta charset=\"windows-1252\"><title>Café</title></head><body><p>Crème brûlée</p></body></html>";
        var bytes = CodePagesEncoding().GetBytes(html);

        var content = _readable.Extract(Page("text/html", bytes))!;

        Assert.Contains("Crème brûlée", content.Text);

        static Encoding CodePagesEncoding()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252);
        }
    }

    [Fact]
    public void Plain_text_is_read_and_binary_types_are_unsupported()
    {
        Assert.Equal("hello", _readable.Extract(Page("text/plain", "hello"u8.ToArray(), url: "https://x.example/notes.txt"))!.Text);
        Assert.Null(_readable.Extract(Page("image/png", [1, 2, 3])));
    }
}

public sealed class PageFetcherTests : IAsyncDisposable
{
    private readonly TestWebServer _server = new();
    private readonly PageFetcher _fetcher = new();

    public async ValueTask DisposeAsync()
    {
        _fetcher.Dispose();
        await _server.DisposeAsync();
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:192.168.0.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void Classifies_addresses(string ip, bool isPublic)
    {
        Assert.Equal(isPublic, PageFetcher.IsPublic(IPAddress.Parse(ip)));
    }

    [Fact]
    public async Task Refuses_local_network_unless_allowed()
    {
        _server.Add("/page", "text/html", "<p>local</p>");

        var blocked = await Assert.ThrowsAsync<LinkFetchException>(() => _fetcher.FetchAsync(_server.Url("/page"), false, default));
        Assert.Contains("local network", blocked.Message);

        var page = await _fetcher.FetchAsync(_server.Url("/page"), true, default);
        Assert.Equal("text/html", page.ContentType);
        Assert.Equal("<p>local</p>", Encoding.UTF8.GetString(page.Body));
    }

    [Fact]
    public async Task Http_errors_become_readable_messages()
    {
        var ex = await Assert.ThrowsAsync<LinkFetchException>(() => _fetcher.FetchAsync(_server.Url("/missing"), true, default));

        Assert.Contains("404", ex.Message);
    }
}

public sealed class LinkProcessorTests : IAsyncLifetime
{
    private readonly TestWebServer _server = new();
    private readonly PageFetcher _fetcher = new();
    private readonly FakeOllama _ollama = new();
    private TestDatabase _db = default!;
    private LinkProcessor _processor = default!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _processor = new LinkProcessor(_db.Factory, _db.Settings, _fetcher, new ReadableText(new TextExtractor()),
            new Summarizer(_ollama), _ollama, NullLogger<LinkProcessor>.Instance);
        // The test server is on 127.0.0.1, so local-network links must be allowed here.
        await _db.Settings.SaveAsync(new InboxSettings { AllowPrivateNetworkLinks = true }, null);
    }

    public async Task DisposeAsync()
    {
        _fetcher.Dispose();
        await _server.DisposeAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Fetches_and_waits_for_the_AI_then_summarizes_once_configured()
    {
        _server.Add("/guide", "text/html", "<html><head><title>Guide</title></head><body><p>Boil water for one minute.</p></body></html>");
        var linkId = await AddLinkAsync(_server.Url("/guide"));

        var first = await _processor.RunOnceAsync(default);
        var fetched = await LoadAsync(linkId);
        Assert.Equal(new LinkOutcome(1, 0, 0), first);
        Assert.Equal(LinkStatus.Fetched, fetched.Status);
        Assert.Equal("Guide", fetched.Title);
        Assert.Contains("Boil water", fetched.ContentText);
        Assert.Empty(_ollama.Calls); // no model chosen yet

        await ConfigureAiAsync();
        var second = await _processor.RunOnceAsync(default);
        var done = await LoadAsync(linkId);
        Assert.Equal(new LinkOutcome(0, 1, 0), second);
        Assert.Equal(LinkStatus.Done, done.Status);
        Assert.Equal("**A page.**\n- point", done.Summary);
        Assert.Equal("gemma3:12b", done.SummaryModel);
        Assert.Contains("Survival/Water", _ollama.Calls.Single().User);
    }

    [Fact]
    public async Task Waits_while_Ollama_is_down()
    {
        _server.Add("/a", "text/plain", "some text");
        var linkId = await AddLinkAsync(_server.Url("/a"));
        await ConfigureAiAsync();
        _ollama.Up = false;

        await _processor.RunOnceAsync(default);

        Assert.Equal(LinkStatus.Fetched, (await LoadAsync(linkId)).Status);
        Assert.Empty(_ollama.Calls);
    }

    [Fact]
    public async Task Summary_errors_retry_then_fail()
    {
        _server.Add("/a", "text/plain", "some text");
        var linkId = await AddLinkAsync(_server.Url("/a"));
        await ConfigureAiAsync();
        _ollama.Reply = (_, _) => throw new OllamaException("model 'gemma3:12b' not found");

        for (var i = 0; i < LinkProcessor.MaxSummaryAttempts; i++) await _processor.RunOnceAsync(default);

        var link = await LoadAsync(linkId);
        Assert.Equal(LinkStatus.Failed, link.Status);
        Assert.Contains("not found", link.Error);
    }

    [Fact]
    public async Task Broken_links_fail_binary_ones_are_skipped_and_youtube_waits()
    {
        _server.Pages["/img.png"] = (200, "image/png", [1, 2, 3]);
        var missing = await AddLinkAsync(_server.Url("/nope"));
        var image = await AddLinkAsync(_server.Url("/img.png"));
        var video = await AddLinkAsync("https://www.youtube.com/watch?v=dQw4w9WgXcQ", LinkKind.YouTubeVideo);

        await _processor.RunOnceAsync(default);

        Assert.Equal(LinkStatus.Failed, (await LoadAsync(missing)).Status);
        Assert.Equal(LinkStatus.Skipped, (await LoadAsync(image)).Status);
        Assert.Equal(LinkStatus.Pending, (await LoadAsync(video)).Status);
    }

    private async Task ConfigureAiAsync()
    {
        var settings = await _db.Settings.GetAsync();
        settings.OllamaModel = "gemma3:12b";
        await _db.Settings.SaveAsync(settings, null);
    }

    private async Task<int> AddLinkAsync(string url, LinkKind kind = LinkKind.Web)
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        var email = await db.Emails.FirstOrDefaultAsync() ?? db.Emails.Add(new InboxEmail
        {
            MessageId = "m@x", Folder = "INBOX", FromAddress = "me@gmail.com", Subject = "NOMAD: x",
            Category = "Survival/Water", Tags = ["diy"], Title = "Filters", ReceivedAt = DateTimeOffset.UtcNow,
        }).Entity;
        var link = new EmailLink { Url = url, Kind = kind };
        email.Links.Add(link);
        await db.SaveChangesAsync();
        return link.Id;
    }

    private async Task<EmailLink> LoadAsync(int id)
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        return await db.Links.SingleAsync(l => l.Id == id);
    }
}
