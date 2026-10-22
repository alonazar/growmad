using System.Net;
using System.Text;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.Web;
using SmartReader;

namespace NomadInbox.Infrastructure.Pages;

public sealed record ArchiveResult(IReadOnlyList<string> YouTubeIds, IReadOnlyList<string> ReplacedAssetFiles);

/// <summary>
/// Saves one page: fetch (headless browser for JavaScript-built pages), extract the main content as HTML,
/// sanitize it, download its images and videos within the size limits, and record its links.
/// A link that points straight at a file (e.g. a PDF) is saved as that file.
/// </summary>
public sealed class PageArchiver(
    PageFetcher fetcher,
    PageStore store,
    TextExtractor textExtractor,
    ILogger<PageArchiver> logger,
    IPageRenderer? renderer = null)
{
    private const int MaxTextChars = 100_000;
    /// <summary>Images smaller than this are tracking pixels or spacers.</summary>
    private const int MinImageBytes = 300;

    public async Task<ArchiveResult> ArchiveAsync(ArchivedPage page, InboxSettings settings, CancellationToken ct)
    {
        var assetLimit = Math.Max(1, settings.MaxPageAssetMegabytes) * 1024L * 1024L;
        var fetched = await fetcher.FetchAsync(page.Url, settings.AllowPrivateNetworkLinks, ct, maxBytes: Math.Max(PageFetcher.MaxBytes, assetLimit));
        page.FinalUrl = fetched.FinalUrl.AbsoluteUri == page.Url ? null : fetched.FinalUrl.AbsoluteUri;

        var oldFiles = page.Assets.Select(a => a.FileName).OfType<string>().ToList();
        page.Assets.Clear();
        page.Links.Clear();

        if (fetched.ContentType is not ("text/html" or "application/xhtml+xml"))
        {
            // A file, not a page: our own small HTML around it, so nothing to sanitize or download.
            var documentHtml = await SaveDocumentAsync(page, fetched, ct);
            await store.WriteContentAsync(page.Id, documentHtml, ct);
            page.ContentPath = Path.GetRelativePath(Path.GetDirectoryName(store.ContentRoot)!, store.ContentPathFor(page.Id)).Replace('\\', '/');
            page.Bytes = Encoding.UTF8.GetByteCount(documentHtml) + page.Assets.Sum(a => a.Bytes);
            page.ArchivedAt = DateTimeOffset.UtcNow;
            page.Error = null;
            return new ArchiveResult([], oldFiles.Except(page.Assets.Select(a => a.FileName).OfType<string>()).ToList());
        }

        var article = await ExtractAsync(fetched, settings, page.Url, ct);
        page.Title = article.Title;
        page.SiteName = article.Site ?? fetched.FinalUrl.Host;
        page.Byline = article.Byline;
        page.Excerpt = article.Excerpt;
        page.TextContent = Cap(article.Text);

        var prepared = PageContentProcessor.Prepare(article.Html, fetched.FinalUrl);
        var budget = Math.Max(1, settings.MaxPageMegabytes) * 1024L * 1024L - Encoding.UTF8.GetByteCount(prepared.Html);
        var saved = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var asset in prepared.Assets)
        {
            var record = new PageAsset { OriginalUrl = asset.Url, Kind = asset.Kind };
            page.Assets.Add(record);
            if (budget <= 0) { record.Error = $"Page size limit ({settings.MaxPageMegabytes} MB) reached."; continue; }
            try
            {
                var media = await fetcher.FetchAsync(asset.Url, settings.AllowPrivateNetworkLinks, ct,
                    maxBytes: Math.Min(assetLimit, budget), referrer: fetched.FinalUrl.AbsoluteUri);
                var expected = asset.Kind == AssetKind.Video ? "video/" : "image/";
                if (!media.ContentType.StartsWith(expected, StringComparison.OrdinalIgnoreCase)
                    && !(asset.Kind == AssetKind.Video && media.ContentType == "application/octet-stream"))
                {
                    record.Error = $"Not {(asset.Kind == AssetKind.Video ? "a video" : "an image")} ({media.ContentType}).";
                    continue;
                }
                if (asset.Kind == AssetKind.Image && media.Body.Length < MinImageBytes) { record.Error = "Tracking pixel or spacer."; continue; }

                record.FileName = await store.SaveAssetAsync(media.Body, media.ContentType, asset.Url, ct);
                record.ContentType = media.ContentType;
                record.Bytes = media.Body.LongLength;
                budget -= media.Body.LongLength;
                saved[asset.Url] = record.FileName;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                record.Error = ex is LinkFetchException ? ex.Message : $"Download failed: {ex.Message}";
            }
        }

        var html = PageContentProcessor.Finalize(prepared.Html, saved);
        await store.WriteContentAsync(page.Id, html, ct);

        page.ContentPath = Path.GetRelativePath(Path.GetDirectoryName(store.ContentRoot)!, store.ContentPathFor(page.Id)).Replace('\\', '/');
        foreach (var link in prepared.Links)
            page.Links.Add(new PageLink { Url = link.Url, Text = link.Text, Position = link.Position });
        page.Bytes = Encoding.UTF8.GetByteCount(html) + page.Assets.Sum(a => a.Bytes);
        page.ArchivedAt = DateTimeOffset.UtcNow;
        page.Error = page.Assets.Count(a => a.FileName is null) is var missing and > 0
            ? $"{missing} of {page.Assets.Count} media file(s) couldn't be saved."
            : null;

        logger.LogInformation("Saved page {Url}: {Links} links, {Saved}/{Total} media, {Kilobytes:N0} KB",
            page.Url, page.Links.Count, saved.Count, page.Assets.Count, page.Bytes / 1024);
        return new ArchiveResult(prepared.YouTubeIds, oldFiles.Except(page.Assets.Select(a => a.FileName).OfType<string>()).ToList());
    }

    private sealed record Article(string? Title, string? Site, string? Byline, string? Excerpt, string Html, string Text);

    /// <summary>The article (Reader View); for pages with almost no text, the version rendered in a headless browser.</summary>
    private async Task<Article> ExtractAsync(FetchedPage fetched, InboxSettings settings, string url, CancellationToken ct)
    {
        var article = Read(fetched.FinalUrl, ReadableText.DecodeHtml(fetched));
        if (article.Text.Length < ReadableText.ThinPageChars && settings.RenderJavaScriptPages && renderer is not null)
        {
            try
            {
                var rendered = await renderer.RenderAsync(url, settings.AllowPrivateNetworkLinks, ct);
                var better = Read(rendered.FinalUrl, rendered.Html);
                if (better.Text.Length > article.Text.Length) article = better;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("Rendering {Url} for the archive failed: {Message}", url, ex.Message);
            }
        }
        return article;
    }

    private static Article Read(Uri url, string html)
    {
        try
        {
            var article = new Reader(url.AbsoluteUri, html).GetArticle();
            if (article.IsReadable && !string.IsNullOrWhiteSpace(article.Content))
                return new Article(Clean(article.Title), Clean(article.SiteName), Clean(article.Byline), Clean(article.Excerpt),
                    article.Content, article.TextContent?.Trim() ?? "");
        }
        catch
        {
            // fall through to the whole page
        }

        // Not an article (a listing, an app): keep the whole body; the sanitizer strips what doesn't belong.
        var doc = new HtmlParser().ParseDocument(html);
        foreach (var junk in doc.QuerySelectorAll("script,style,nav,header,footer,form,aside").ToList()) junk.Remove();
        return new Article(Clean(doc.Title), null, null, null, doc.Body?.InnerHtml ?? "", doc.Body?.TextContent.Trim() ?? "");
    }

    /// <summary>The link is a file (PDF, image, …): keep the file and show its text, if any.</summary>
    private async Task<string> SaveDocumentAsync(ArchivedPage page, FetchedPage fetched, CancellationToken ct)
    {
        var name = AttachmentStore.SanitizeFileName(Path.GetFileName(fetched.FinalUrl.AbsolutePath));
        var file = await store.SaveAssetAsync(fetched.Body, fetched.ContentType, fetched.FinalUrl.AbsoluteUri, ct);
        page.Assets.Add(new PageAsset
        {
            OriginalUrl = fetched.FinalUrl.AbsoluteUri, Kind = AssetKind.Document, FileName = file,
            ContentType = fetched.ContentType, Bytes = fetched.Body.LongLength,
        });
        page.Title = name;
        page.SiteName = fetched.FinalUrl.Host;

        var text = textExtractor.Extract(name, fetched.ContentType, fetched.Body).Text;
        page.TextContent = Cap(text);
        var sb = new StringBuilder();
        sb.Append($"<p>This link is a file: <a href=\"{PageContentProcessor.AssetScheme}{file}\">{WebUtility.HtmlEncode(name)}</a> ");
        sb.Append($"({fetched.ContentType}, {fetched.Body.Length / 1024:N0} KB).</p>");
        if (!string.IsNullOrWhiteSpace(text)) sb.Append($"<pre>{WebUtility.HtmlEncode(Cap(text))}</pre>");
        return sb.ToString();
    }

    private static string? Cap(string? s) => s is null ? null : s.Length > MaxTextChars ? s[..MaxTextChars] : s;

    private static string? Clean(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+", " ");
}
