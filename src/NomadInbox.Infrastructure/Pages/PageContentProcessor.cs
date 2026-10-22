using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;
using NomadInbox.Core.Content;
using NomadInbox.Core.Domain;

namespace NomadInbox.Infrastructure.Pages;

public sealed record AssetRef(string Url, AssetKind Kind);
public sealed record LinkRef(int Position, string Url, string? Text);
public sealed record PreparedContent(string Html, IReadOnlyList<AssetRef> Assets, IReadOnlyList<LinkRef> Links, IReadOnlyList<string> YouTubeIds);

/// <summary>
/// Turns a page's article HTML into the stored form, in two steps:
/// <list type="number">
/// <item><see cref="Prepare"/>: fix lazy-loaded images and srcset, turn YouTube embeds into markers, sanitize with an
///       allowlist (structure and inline formatting such as &lt;b&gt;/&lt;i&gt; survive; scripts, iframes, styles and
///       classes don't), make URLs absolute, and record media and links. Links become <c>nomad-link:N</c>.</item>
/// <item><see cref="Finalize"/>: once media is downloaded, point it at local files (<c>nomad-asset:FILE</c>) or
///       replace what couldn't be saved with a link to the original.</item>
/// </list>
/// The markers are resolved when a page is shown, so links light up as more pages are saved and template changes
/// apply to every page.
/// </summary>
public static partial class PageContentProcessor
{
    public const string AssetScheme = "nomad-asset:";
    public const string LinkScheme = "nomad-link:";
    public const string YouTubeAttribute = "data-nomad-youtube";
    public const string ThumbnailAttribute = "data-nomad-thumb";

    private static readonly string[] LazySourceAttributes = ["data-src", "data-lazy-src", "data-original", "data-url", "data-hi-res-src"];

    [GeneratedRegex(@"(?:youtube(?:-nocookie)?\.com/(?:embed|v)/|youtu\.be/)([A-Za-z0-9_-]{11})")]
    private static partial Regex YouTubeEmbed();

    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    public static PreparedContent Prepare(string html, Uri pageUrl)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument("<!doctype html><html><body></body></html>");
        doc.Body!.InnerHtml = html;

        foreach (var noscript in doc.QuerySelectorAll("noscript").ToList()) noscript.Remove(); // lazy-load fallbacks duplicate images
        foreach (var picture in doc.QuerySelectorAll("picture").ToList()) UnwrapPicture(picture);
        foreach (var img in doc.QuerySelectorAll("img")) FixImageSource(img);
        foreach (var iframe in doc.QuerySelectorAll("iframe").ToList()) ReplaceEmbed(doc, iframe);
        foreach (var el in doc.QuerySelectorAll("[src],[href],[poster]")) MakeAbsolute(el, pageUrl);

        var clean = Sanitizer.Sanitize(doc.Body.InnerHtml, pageUrl.AbsoluteUri);
        doc.Body.InnerHtml = clean;

        var assets = new List<AssetRef>();
        void AddAsset(string? url, AssetKind kind)
        {
            if (url is not null && url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && assets.All(a => a.Url != url))
                assets.Add(new AssetRef(url, kind));
        }

        foreach (var img in doc.QuerySelectorAll("img")) AddAsset(img.GetAttribute("src"), AssetKind.Image);
        foreach (var video in doc.QuerySelectorAll("video"))
        {
            video.SetAttribute("controls", "");
            video.SetAttribute("preload", "metadata");
            AddAsset(video.GetAttribute("src"), AssetKind.Video);
            foreach (var source in video.QuerySelectorAll("source")) AddAsset(source.GetAttribute("src"), AssetKind.Video);
            AddAsset(video.GetAttribute("poster"), AssetKind.Image);
        }

        var youTubeIds = new List<string>();
        foreach (var figure in doc.QuerySelectorAll($"figure[{YouTubeAttribute}]"))
        {
            var id = figure.GetAttribute(YouTubeAttribute)!;
            if (!youTubeIds.Contains(id)) youTubeIds.Add(id);
            AddAsset(ThumbnailUrl(id), AssetKind.Image);
        }

        var links = new List<LinkRef>();
        var selfUrl = LinkExtractor.Normalize(pageUrl.AbsoluteUri)?.Url;
        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href")!;
            if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue; // mailto:, #anchors

            var target = WithoutFragment(LinkExtractor.Normalize(href)?.Url);
            if (target is null) { a.RemoveAttribute("href"); continue; }

            // A link back into this same page (a table of contents, "back to top"): keep it as an in-page anchor.
            if (target == WithoutFragment(selfUrl) && Uri.TryCreate(href, UriKind.Absolute, out var u))
            {
                a.SetAttribute("href", string.IsNullOrEmpty(u.Fragment) ? "#" : u.Fragment);
                continue;
            }

            var existing = links.FirstOrDefault(l => l.Url == target);
            var position = existing?.Position ?? links.Count;
            if (existing is null)
            {
                var text = Regex.Replace(a.TextContent, @"\s+", " ").Trim();
                links.Add(new LinkRef(position, target, text.Length > 200 ? text[..200] : text.Length == 0 ? null : text));
            }
            a.SetAttribute("href", LinkScheme + position.ToString(CultureInfo.InvariantCulture));
        }

        return new PreparedContent(doc.Body.InnerHtml, assets, links, youTubeIds);
    }

    /// <summary>Points media at downloaded files; anything that couldn't be saved becomes a link to the original.</summary>
    public static string Finalize(string preparedHtml, IReadOnlyDictionary<string, string> savedFiles)
    {
        var doc = new HtmlParser().ParseDocument("<!doctype html><html><body></body></html>");
        doc.Body!.InnerHtml = preparedHtml;

        foreach (var img in doc.QuerySelectorAll("img").ToList())
        {
            var src = img.GetAttribute("src");
            if (src is not null && savedFiles.TryGetValue(src, out var file)) img.SetAttribute("src", AssetScheme + file);
            else img.Replace(MissingMedia(doc, src, img.GetAttribute("alt") is { Length: > 0 } alt ? $"Image: {alt}" : "Image"));
        }

        foreach (var video in doc.QuerySelectorAll("video").ToList())
        {
            var sources = new[] { video }.Concat(video.QuerySelectorAll("source")).Where(e => e.HasAttribute("src")).ToList();
            var anySaved = false;
            foreach (var el in sources)
            {
                if (savedFiles.TryGetValue(el.GetAttribute("src")!, out var file)) { el.SetAttribute("src", AssetScheme + file); anySaved = true; }
                else if (el != video) el.Remove();
                else el.RemoveAttribute("src");
            }
            if (video.GetAttribute("poster") is { } poster)
            {
                if (savedFiles.TryGetValue(poster, out var file)) video.SetAttribute("poster", AssetScheme + file);
                else video.RemoveAttribute("poster");
            }
            if (!anySaved) video.Replace(MissingMedia(doc, sources.FirstOrDefault()?.GetAttribute("src"), "Video"));
        }

        foreach (var figure in doc.QuerySelectorAll($"figure[{YouTubeAttribute}]"))
        {
            if (savedFiles.TryGetValue(ThumbnailUrl(figure.GetAttribute(YouTubeAttribute)!), out var thumb))
                figure.SetAttribute(ThumbnailAttribute, thumb);
        }

        return doc.Body.InnerHtml;
    }

    public static string ThumbnailUrl(string youTubeId) => $"https://i.ytimg.com/vi/{youTubeId}/hqdefault.jpg";

    private static IElement MissingMedia(IDocument doc, string? url, string label)
    {
        var a = doc.CreateElement("a");
        a.ClassName = "nomad-missing-media";
        if (url is not null) a.SetAttribute("href", url);
        a.TextContent = $"[{label}: not saved — open the original]";
        return a;
    }

    private static void UnwrapPicture(IElement picture)
    {
        var img = picture.QuerySelector("img");
        if (img is null) { picture.Remove(); return; }
        // A <source srcset> usually lists bigger images than the fallback <img>.
        if (picture.QuerySelector("source[srcset]")?.GetAttribute("srcset") is { } srcset && !img.HasAttribute("srcset"))
            img.SetAttribute("srcset", srcset);
        picture.Replace(img);
    }

    private static void FixImageSource(IElement img)
    {
        var src = img.GetAttribute("src");
        var placeholder = string.IsNullOrWhiteSpace(src) || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
        if (placeholder && LazySourceAttributes.Select(img.GetAttribute).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) is { } lazy)
            img.SetAttribute("src", lazy);

        var srcset = img.GetAttribute("srcset") ?? img.GetAttribute("data-srcset");
        if (BestFromSrcset(srcset) is { } best) img.SetAttribute("src", best);
        img.RemoveAttribute("srcset");
        img.RemoveAttribute("sizes");
    }

    /// <summary>The largest candidate in a srcset ("a.jpg 400w, b.jpg 800w" or "a.jpg 1x, b.jpg 2x").</summary>
    public static string? BestFromSrcset(string? srcset)
    {
        if (string.IsNullOrWhiteSpace(srcset)) return null;
        return srcset.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length > 0)
            .Select(p => (Url: p[0], Size: p.Length > 1 && double.TryParse(p[1].TrimEnd('w', 'x', 'W', 'X'), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 1))
            .OrderByDescending(c => c.Size)
            .Select(c => c.Url)
            .FirstOrDefault();
    }

    private static void ReplaceEmbed(IDocument doc, IElement iframe)
    {
        var match = YouTubeEmbed().Match(iframe.GetAttribute("src") ?? "");
        if (!match.Success) { iframe.Remove(); return; }
        var figure = doc.CreateElement("figure");
        figure.SetAttribute(YouTubeAttribute, match.Groups[1].Value);
        iframe.Replace(figure);
    }

    private static void MakeAbsolute(IElement el, Uri pageUrl)
    {
        foreach (var name in new[] { "src", "href", "poster" })
        {
            var value = el.GetAttribute(name);
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith('#') || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            if (Uri.TryCreate(pageUrl, value.Trim(), out var absolute)) el.SetAttribute(name, absolute.AbsoluteUri);
        }
    }

    private static string? WithoutFragment(string? url) => url is null ? null : url.Split('#')[0];

    private static HtmlSanitizer CreateSanitizer()
    {
        var options = new HtmlSanitizerOptions
        {
            AllowedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "p", "br", "hr", "h1", "h2", "h3", "h4", "h5", "h6", "b", "strong", "i", "em", "u", "s", "strike", "del", "ins",
                "mark", "small", "sub", "sup", "code", "pre", "kbd", "samp", "var", "blockquote", "q", "cite", "abbr", "dfn", "time",
                "ul", "ol", "li", "dl", "dt", "dd", "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption", "colgroup", "col",
                "figure", "figcaption", "img", "video", "source", "a", "span", "div", "section", "article", "header", "footer",
                "aside", "details", "summary",
            },
            AllowedAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "href", "src", "alt", "title", "poster", "controls", "colspan", "rowspan", "start", "reversed", "type", "datetime",
                "cite", "open", YouTubeAttribute,
            },
            AllowedSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" },
            UriAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "href", "src", "poster", "cite" },
            AllowedCssProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };
        return new HtmlSanitizer(options) { KeepChildNodes = true };
    }
}
