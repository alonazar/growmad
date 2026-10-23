using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Markdig;
using Microsoft.EntityFrameworkCore;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Data;
using Scriban;
using Scriban.Runtime;

namespace NomadInbox.Infrastructure.Pages;

public enum PageView
{
    /// <summary>Inside NOMAD Inbox: local links go to /pages/{id}, unsaved ones to "Save this page".</summary>
    App,
    /// <summary>The plain-HTML copy: relative links between files; unsaved links go to the original site.</summary>
    Export,
}

public sealed class PageTemplateException(IReadOnlyList<string> errors)
    : Exception("The page template has errors: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Renders a saved page through the (editable) Scriban template. The stored content holds markers for media and links;
/// they're resolved here against what is saved now, so links light up as more pages are saved, and a template change
/// restyles every page.
/// </summary>
public sealed partial class PageRenderer(IDbContextFactory<NomadInboxDbContext> dbFactory, PageStore store)
{
    public static string DefaultTemplate { get; } = Embedded("page.html");
    public static string DefaultStylesheet { get; } = Embedded("page.css");
    public static string IndexTemplate { get; } = Embedded("index.html");

    private static readonly MarkdownPipeline Markdown = new MarkdownPipelineBuilder().DisableHtml().UseEmphasisExtras().UsePipeTables().Build();

    [GeneratedRegex(@"<body\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BodyTag();

    /// <summary>Syntax errors in a template, empty when it's fine.</summary>
    public static IReadOnlyList<string> Validate(string template)
    {
        var parsed = Template.Parse(template);
        return parsed.HasErrors ? parsed.Messages.Select(m => m.ToString()).ToList() : [];
    }

    /// <param name="appBar">HTML inserted at the top of the body in the app (navigation and actions).</param>
    /// <param name="template">Template to use instead of the saved one (the editor's preview).</param>
    /// <param name="stylesheetUrl">Stylesheet URL instead of the default for the view.</param>
    public async Task<string?> RenderAsync(int pageId, PageView view, InboxSettings settings, string? appBar = null,
        string? template = null, string? stylesheetUrl = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var page = await db.Pages.AsNoTracking().Include(p => p.Links).SingleOrDefaultAsync(p => p.Id == pageId, ct);
        if (page is null || page.Status != PageStatus.Done) return null;
        var content = await store.ReadContentAsync(pageId, ct) ?? "";

        var targetUrls = page.Links.Select(l => l.Url).Distinct().ToList();
        var savedTargets = await db.Pages.AsNoTracking()
            .Where(p => targetUrls.Contains(p.Url) && p.Status == PageStatus.Done)
            .Select(p => new { p.Id, p.Url, p.Title })
            .ToDictionaryAsync(p => p.Url, p => (p.Id, p.Title), ct);

        var email = page.InboxEmailId is { } emailId
            ? await db.Emails.AsNoTracking().Include(e => e.Links).SingleOrDefaultAsync(e => e.Id == emailId, ct)
            : null;
        var summary = email?.Links.FirstOrDefault(l => l.Id == page.SourceLinkId && l.Summary is not null)?.Summary
                      ?? email?.Links.FirstOrDefault(l => l.Url == page.Url && l.Summary is not null)?.Summary;
        var videos = email?.Links
            .Where(l => l.Kind == LinkKind.YouTubeVideo && l.ExternalId is not null && l.VideoPath is not null)
            .GroupBy(l => l.ExternalId!).ToDictionary(g => g.Key, g => g.First().Id) ?? [];

        var urls = new Urls(view);
        var linkHrefs = page.Links.ToDictionary(l => l.Position, l => savedTargets.TryGetValue(l.Url, out var t)
            ? (Href: urls.Page(t.Id, t.Title), Saved: true)
            : (Href: urls.Unsaved(l), Saved: false));

        // The AI's captions become alt text for images the page didn't describe itself.
        var assetKeys = (await db.PageAssets.AsNoTracking()
                .Where(a => a.ArchivedPageId == pageId && a.Kind == AssetKind.Image && a.FileName != null)
                .Select(a => a.FileName!).ToListAsync(ct))
            .Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList();
        var captions = await db.ImageDescriptions.AsNoTracking()
            .Where(d => assetKeys.Contains(d.ImageKey) && d.Status == ImageDescriptionStatus.Done && d.Caption != null)
            .ToDictionaryAsync(d => d.ImageKey, d => d.Caption!, ct);

        var contentHtml = ResolveMarkers(content, linkHrefs, videos, urls, captions);
        var linkList = new ScriptArray(page.Links.OrderBy(l => l.Position).Select(l => new ScriptObject
        {
            ["text"] = l.Text,
            ["url"] = l.Url,
            ["label"] = l.Text ?? l.Url,
            ["href"] = linkHrefs[l.Position].Href,
            ["saved"] = linkHrefs[l.Position].Saved,
        }));

        var model = new ScriptObject
        {
            ["page"] = new ScriptObject
            {
                ["title"] = page.Title ?? page.Url,
                ["site"] = page.SiteName ?? Host(page.Url),
                ["host"] = Host(page.FinalUrl ?? page.Url),
                ["url"] = page.FinalUrl ?? page.Url,
                ["byline"] = page.Byline,
                ["excerpt"] = page.Excerpt,
                ["saved_at"] = page.ArchivedAt?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["size"] = FormatSize(page.Bytes),
                ["category"] = email?.Category,
                ["tags"] = new ScriptArray(email?.Tags.Where(t => t is not ("archive" or "download" or "follow")) ?? []),
                ["summary_html"] = summary is null ? null : Markdig.Markdown.ToHtml(summary, Markdown),
                ["content_html"] = contentHtml,
                ["link_count"] = page.Links.Count,
                ["saved_link_count"] = linkHrefs.Values.Count(v => v.Saved),
            },
            ["links"] = linkList,
            ["home_url"] = urls.Home,
            ["stylesheet_url"] = stylesheetUrl ?? urls.Stylesheet,
            ["in_app"] = view == PageView.App,
        };

        var html = Render(template ?? (string.IsNullOrWhiteSpace(settings.PageTemplate) ? DefaultTemplate : settings.PageTemplate), model);
        if (appBar is not null)
        {
            var body = BodyTag().Match(html);
            html = body.Success ? html.Insert(body.Index + body.Length, appBar) : appBar + html;
        }
        return html;
    }

    /// <summary>Index page of the exported copy, grouped by top-level category.</summary>
    public async Task<string> RenderIndexAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pages = await db.Pages.AsNoTracking().Where(p => p.Status == PageStatus.Done).ToListAsync(ct);
        var emailIds = pages.Select(p => p.InboxEmailId).OfType<int>().Distinct().ToList();
        var categories = await db.Emails.AsNoTracking().Where(e => emailIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Category, ct);

        var groups = pages
            .GroupBy(p => p.InboxEmailId is { } id && categories.GetValueOrDefault(id) is { } c ? c.Split('/')[0] : "Uncategorized")
            .OrderBy(g => g.Key == "Uncategorized").ThenBy(g => g.Key)
            .Select(g => new ScriptObject
            {
                ["name"] = g.Key,
                ["pages"] = new ScriptArray(g.OrderByDescending(p => p.ArchivedAt).Select(p => new ScriptObject
                {
                    ["title"] = p.Title ?? p.Url,
                    ["site"] = p.SiteName ?? Host(p.Url),
                    ["saved_at"] = p.ArchivedAt?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["href"] = $"pages/{ExportFolder(p.Id, p.Title)}/index.html",
                })),
            });

        return Render(IndexTemplate, new ScriptObject
        {
            ["groups"] = new ScriptArray(groups),
            ["total"] = pages.Count,
            ["updated_at"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        });
    }

    /// <summary>Folder of a page in the exported copy, e.g. "12-cleaning-ceramic-filters".</summary>
    public static string ExportFolder(int id, string? title)
    {
        var slug = Regex.Replace((title ?? "page").ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 60) slug = slug[..60].TrimEnd('-');
        return slug.Length == 0 ? id.ToString(CultureInfo.InvariantCulture) : $"{id}-{slug}";
    }

    private static string Render(string templateText, ScriptObject model)
    {
        var template = Template.Parse(templateText);
        if (template.HasErrors) throw new PageTemplateException(template.Messages.Select(m => m.ToString()).ToList());

        var context = new TemplateContext { LoopLimit = 20_000, RecursiveLimit = 64, StrictVariables = false };
        context.PushGlobal(model);
        try
        {
            return template.Render(context);
        }
        catch (Scriban.Syntax.ScriptRuntimeException ex)
        {
            throw new PageTemplateException([ex.Message]);
        }
    }

    private static string ResolveMarkers(string content, IReadOnlyDictionary<int, (string Href, bool Saved)> links,
        IReadOnlyDictionary<string, int> videos, Urls urls, IReadOnlyDictionary<string, string>? captions = null)
    {
        var doc = new HtmlParser().ParseDocument("<!doctype html><html><body></body></html>");
        doc.Body!.InnerHtml = content;

        foreach (var el in doc.QuerySelectorAll("[src],[poster],[href]"))
        {
            foreach (var attr in new[] { "src", "poster", "href" })
            {
                if (el.GetAttribute(attr) is not { } value || !value.StartsWith(PageContentProcessor.AssetScheme, StringComparison.Ordinal))
                    continue;
                var file = value[PageContentProcessor.AssetScheme.Length..];
                el.SetAttribute(attr, urls.Asset(file));
                if (attr == "src" && el.LocalName == "img" && string.IsNullOrWhiteSpace(el.GetAttribute("alt")) &&
                    captions?.GetValueOrDefault(Path.GetFileNameWithoutExtension(file)) is { } caption)
                    el.SetAttribute("alt", caption);
            }
        }

        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href")!;
            if (!href.StartsWith(PageContentProcessor.LinkScheme, StringComparison.Ordinal)) continue;
            if (int.TryParse(href[PageContentProcessor.LinkScheme.Length..], CultureInfo.InvariantCulture, out var position)
                && links.TryGetValue(position, out var link))
            {
                a.SetAttribute("href", link.Href);
                a.ClassList.Add(link.Saved ? "nomad-saved" : "nomad-not-saved");
                if (!link.Saved) a.SetAttribute("title", urls.UnsavedHint);
            }
            else
            {
                a.RemoveAttribute("href");
            }
        }

        foreach (var figure in doc.QuerySelectorAll($"figure[{PageContentProcessor.YouTubeAttribute}]").ToList())
            ResolveVideo(doc, figure, videos, urls);

        return doc.Body.InnerHtml;
    }

    private static void ResolveVideo(IDocument doc, IElement figure, IReadOnlyDictionary<string, int> videos, Urls urls)
    {
        var id = figure.GetAttribute(PageContentProcessor.YouTubeAttribute)!;
        var thumb = figure.GetAttribute(PageContentProcessor.ThumbnailAttribute);
        figure.RemoveAttribute(PageContentProcessor.YouTubeAttribute);
        figure.RemoveAttribute(PageContentProcessor.ThumbnailAttribute);
        figure.ClassName = "nomad-video";

        var watch = $"https://www.youtube.com/watch?v={id}";
        string caption;
        if (urls.View == PageView.App && videos.TryGetValue(id, out var linkId))
        {
            var video = doc.CreateElement("video");
            video.SetAttribute("controls", "");
            video.SetAttribute("preload", "metadata");
            video.SetAttribute("src", $"/videos/{linkId}");
            if (thumb is not null) video.SetAttribute("poster", urls.Asset(thumb));
            figure.AppendChild(video);
            caption = "YouTube video, saved for offline viewing.";
        }
        else
        {
            var a = doc.CreateElement("a");
            a.SetAttribute("href", watch);
            if (thumb is not null)
            {
                var img = doc.CreateElement("img");
                img.SetAttribute("src", urls.Asset(thumb));
                img.SetAttribute("alt", "YouTube video");
                a.AppendChild(img);
            }
            else
            {
                a.TextContent = watch;
            }
            figure.AppendChild(a);
            caption = urls.View == PageView.App
                ? "YouTube video, not downloaded. It's on the email's page; tag the email #download or use Download video there."
                : "YouTube video (online). Saved videos are in NOMAD Inbox and Jellyfin.";
        }
        var figcaption = doc.CreateElement("figcaption");
        figcaption.TextContent = caption;
        figure.AppendChild(figcaption);
    }

    private sealed class Urls(PageView view)
    {
        public PageView View => view;
        public string Asset(string file) => view == PageView.App ? $"/pages/assets/{WebUtility.UrlEncode(file)}" : $"../../assets/{file}";
        public string Page(int id, string? title) => view == PageView.App ? $"/pages/{id}" : $"../{ExportFolder(id, title)}/index.html";
        public string Unsaved(PageLink link) => view == PageView.App ? $"/pages/links/{link.Id}" : link.Url;
        public string Stylesheet => view == PageView.App ? "/pages/style.css" : "../../style.css";
        public string Home => view == PageView.App ? "/library" : "../../index.html";
        public string UnsavedHint => view == PageView.App ? "Not saved yet: open to save it" : "Not saved: opens the original site";
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    private static string Embedded(string name)
    {
        using var stream = typeof(PageRenderer).Assembly.GetManifestResourceStream($"NomadInbox.Pages.{name}")
                           ?? throw new InvalidOperationException($"Missing embedded template {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
