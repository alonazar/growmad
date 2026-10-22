using NomadInbox.Core.Content;
using NomadInbox.Core.Domain;

namespace NomadInbox.Tests;

public class LinkExtractorTests
{
    [Theory]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", LinkKind.YouTubeVideo)]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ&t=42s", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", LinkKind.YouTubeVideo)]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", LinkKind.YouTubeVideo)]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ?feature=share", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", LinkKind.YouTubeVideo)]
    [InlineData("https://www.youtube.com/playlist?list=PL123abc", "https://www.youtube.com/playlist?list=PL123abc", LinkKind.YouTubePlaylist)]
    [InlineData("https://www.youtube.com/@CityPrepping/videos", "https://www.youtube.com/@CityPrepping", LinkKind.YouTubeChannel)]
    [InlineData("https://youtube.com/channel/UCabc123/featured", "https://www.youtube.com/channel/UCabc123", LinkKind.YouTubeChannel)]
    [InlineData("https://www.youtube.com/c/SomeName", "https://www.youtube.com/c/SomeName", LinkKind.YouTubeChannel)]
    public void Normalizes_youtube_links(string input, string expected, LinkKind kind)
    {
        var link = LinkExtractor.Normalize(input)!;

        Assert.Equal(expected, link.Url);
        Assert.Equal(kind, link.Kind);
    }

    [Theory]
    [InlineData("https://Example.com/a?utm_source=x&id=5&fbclid=y", "https://example.com/a?id=5")]
    [InlineData("https://example.com:443/path#section", "https://example.com/path#section")]
    [InlineData("https://www.google.com/url?q=https://example.org/page%3Futm_medium%3Demail&sa=D", "https://example.org/page")]
    [InlineData("https://en.wikipedia.org/wiki/Water_(molecule)", "https://en.wikipedia.org/wiki/Water_(molecule)")]
    public void Cleans_web_links(string input, string expected)
    {
        var link = LinkExtractor.Normalize(input)!;

        Assert.Equal(LinkKind.Web, link.Kind);
        Assert.Equal(expected, link.Url);
    }

    [Theory]
    [InlineData("mailto:me@example.com")]
    [InlineData("ftp://example.com/file")]
    [InlineData("not a url")]
    public void Ignores_non_http_links(string input)
    {
        Assert.Null(LinkExtractor.Normalize(input));
    }

    [Fact]
    public void YouTube_pages_that_are_not_videos_or_channels_are_plain_web_links()
    {
        Assert.Equal(LinkKind.Web, LinkExtractor.Normalize("https://www.youtube.com/feed/subscriptions")!.Kind);
    }

    [Fact]
    public void Extracts_from_text_and_html_in_order_without_duplicates()
    {
        const string text = """
            Check this (https://example.com/guide).
            And https://youtu.be/dQw4w9WgXcQ, then https://example.com/guide again.
            """;
        const string html = """
            <p>Check <a href="https://example.com/guide?utm_campaign=z">this</a></p>
            <a href='https://www.youtube.com/watch?v=dQw4w9WgXcQ&amp;si=1'>video</a>
            <a href="mailto:me@example.com">me</a>
            <p>Plain https://other.example.net/x</p>
            """;

        var links = LinkExtractor.Extract(text, html);

        Assert.Equal(
            ["https://example.com/guide", "https://www.youtube.com/watch?v=dQw4w9WgXcQ", "https://other.example.net/x"],
            links.Select(l => l.Url));
    }
}
