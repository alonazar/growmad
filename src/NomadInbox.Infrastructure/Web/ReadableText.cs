using System.Text;
using System.Text.RegularExpressions;
using NomadInbox.Core.Content;
using NomadInbox.Infrastructure.Files;
using SmartReader;

namespace NomadInbox.Infrastructure.Web;

public sealed record ReadableContent(string? Title, string? SiteName, string Text, string? Note);

/// <summary>
/// Turns a fetched page into the text worth summarizing: the article body for HTML (SmartReader, a port of
/// Firefox's Reader View), or the document text for PDFs and plain text.
/// </summary>
public sealed partial class ReadableText(TextExtractor textExtractor)
{
    public const int MaxChars = 100_000;
    /// <summary>Less text than this from an HTML page suggests it's built by JavaScript and worth rendering.</summary>
    public const int ThinPageChars = 500;

    static ReadableText() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [GeneratedRegex(@"<meta[^>]+charset\s*=\s*[""']?(?<cs>[\w-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharset();

    [GeneratedRegex(@"<title[^>]*>(?<t>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    /// <summary>Null when the content type isn't something we can read.</summary>
    public ReadableContent? Extract(FetchedPage page)
    {
        var type = page.ContentType.ToLowerInvariant();

        if (type is "text/html" or "application/xhtml+xml")
            return FromHtml(page.FinalUrl, Decode(page.Body, page.Charset));

        if (type == "application/pdf" || type.StartsWith("text/"))
        {
            var name = Path.GetFileName(page.FinalUrl.AbsolutePath);
            var extraction = textExtractor.Extract(type == "application/pdf" && !name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? name + ".pdf" : name, type, page.Body);
            if (extraction.Text is null)
                return new ReadableContent(name, page.FinalUrl.Host, "", extraction.Note ?? "No text found.");
            return new ReadableContent(name, page.FinalUrl.Host, Cap(extraction.Text), extraction.Note);
        }

        return null;
    }

    /// <summary>For HTML that a headless browser has already rendered.</summary>
    public ReadableContent ExtractHtml(Uri url, string html) => FromHtml(url, html);

    private static ReadableContent FromHtml(Uri url, string html)
    {
        string? title = null, site = null, text = null, note = null;
        try
        {
            var article = new Reader(url.AbsoluteUri, html).GetArticle();
            title = Clean(article.Title);
            site = Clean(article.SiteName);
            if (article.IsReadable && !string.IsNullOrWhiteSpace(article.TextContent))
                text = Normalize(article.TextContent);
        }
        catch (Exception ex)
        {
            note = $"Reader view failed ({ex.Message}); used the whole page text.";
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            // Not an article (a shop page, a forum index, …): fall back to all visible text.
            text = HtmlText.ToPlainText(html);
            note ??= "No article body found; summarized the whole page text.";
        }

        title ??= TitleTag().Match(html) is { Success: true } m ? Clean(System.Net.WebUtility.HtmlDecode(m.Groups["t"].Value)) : null;
        return new ReadableContent(title, site ?? url.Host, Cap(text), note);
    }

    private static string Decode(byte[] body, string? headerCharset)
    {
        var charset = headerCharset;
        if (string.IsNullOrWhiteSpace(charset))
        {
            var head = Encoding.ASCII.GetString(body, 0, Math.Min(body.Length, 4096));
            charset = MetaCharset().Match(head) is { Success: true } m ? m.Groups["cs"].Value : null;
        }

        Encoding encoding;
        try
        {
            encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"', '\''));
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }
        return encoding.GetString(body);
    }

    private static string Normalize(string text)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim());
        return string.Join('\n', lines).Trim();
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : Regex.Replace(s.Trim(), @"\s+", " ");

    private static string Cap(string text) => text.Length > MaxChars ? text[..MaxChars] : text;
}
