using System.Net;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Data;
using NomadInbox.Infrastructure.Pages;
using NomadInbox.Infrastructure.Settings;

namespace NomadInbox.Web;

/// <summary>
/// Saved pages: viewing (through the editable template), their media, "Not saved yet" pages for links,
/// and the actions (save, refresh, delete, save linked pages). Saved pages never run scripts: a strict
/// Content-Security-Policy is sent with every page, on top of the sanitizer.
/// </summary>
public static class PageEndpoints
{
    /// <summary>No scripts at all; media, styles and forms only from NOMAD Inbox itself.</summary>
    private const string ContentSecurityPolicy =
        "default-src 'none'; img-src 'self' data:; media-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self' data:; " +
        "form-action 'self'; base-uri 'none'; frame-ancestors 'self'";

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapPageEndpoints(this WebApplication app)
    {
        var pages = app.MapGroup("/pages").RequireAuthorization();

        pages.MapGet("/{id:int}", async (int id, HttpContext context, IAntiforgery antiforgery, PageRenderer renderer,
            SettingsService settingsService, IDbContextFactory<NomadInboxDbContext> dbFactory) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var page = await db.Pages.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id);
            if (page is null) return Results.NotFound();
            var token = antiforgery.GetAndStoreTokens(context).RequestToken!;
            var settings = await settingsService.GetAsync();

            string html;
            if (page.Status != PageStatus.Done)
            {
                html = StatusPage(page, token, settings.UsesInternet);
            }
            else
            {
                try
                {
                    html = await renderer.RenderAsync(id, PageView.App, settings, AppBar(page, token, settings.UsesInternet))
                           ?? StatusPage(page, token, settings.UsesInternet);
                }
                catch (PageTemplateException ex)
                {
                    html = Simple("The page template has an error", $"<p>{Encode(string.Join(" ", ex.Errors))}</p>" +
                        "<p><a href=\"/settings/page-template\">Fix the template</a></p>", page.Url);
                }
            }
            return PageResult(context, html);
        });

        pages.MapGet("/style.css", async (SettingsService settingsService) =>
        {
            var settings = await settingsService.GetAsync();
            var css = string.IsNullOrWhiteSpace(settings.PageStylesheet) ? PageRenderer.DefaultStylesheet : settings.PageStylesheet;
            return Results.Text(css, "text/css", Encoding.UTF8);
        });

        pages.MapGet("/assets/{file}", (string file, PageStore store) =>
        {
            var path = store.ResolveAsset(file);
            if (path is null) return Results.NotFound();
            var type = ContentTypes.TryGetContentType(file, out var t) ? t : "application/octet-stream";
            // Named by content hash, so they never change.
            return Results.File(path, type, enableRangeProcessing: true,
                lastModified: File.GetLastWriteTimeUtc(path), entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{file}\""));
        });

        // A link in a saved page whose target isn't saved (yet).
        pages.MapGet("/links/{linkId:int}", async (int linkId, HttpContext context, IAntiforgery antiforgery,
            IDbContextFactory<NomadInboxDbContext> dbFactory, SettingsService settingsService) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var link = await db.PageLinks.AsNoTracking().SingleOrDefaultAsync(l => l.Id == linkId);
            if (link is null) return Results.NotFound();
            var saved = await db.Pages.AsNoTracking().Where(p => p.Url == link.Url).Select(p => (int?)p.Id).FirstOrDefaultAsync();
            if (saved is { } savedId) return Results.Redirect($"/pages/{savedId}");

            var token = antiforgery.GetAndStoreTokens(context).RequestToken!;
            var save = (await settingsService.GetAsync()).UsesInternet
                ? $"""<form method="post" action="/pages/links/{linkId}/save">{Token(token)}<button class="nomad-button" type="submit">Save this page</button></form>"""
                : "<p>This NOMAD is air-gapped: pages are saved on the collector and arrive in bundles.</p>";
            var body = $"""
                <p>This page isn't saved yet.</p>
                <p><strong>{Encode(link.Text ?? link.Url)}</strong><br><a href="{Encode(link.Url)}">{Encode(link.Url)}</a> (online)</p>
                {save}
                <p><a href="/pages/{link.ArchivedPageId}">← Back to the page that links here</a></p>
                """;
            return PageResult(context, Simple("Not saved yet", body, null));
        });

        pages.MapPost("/links/{linkId:int}/save", async (int linkId, HttpContext context, IAntiforgery antiforgery, PageService service,
            SettingsService settingsService) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            if (!(await settingsService.GetAsync()).UsesInternet) return Results.Redirect($"/pages/links/{linkId}");
            var result = await service.SaveLinkAsync(linkId, context.RequestAborted);
            return result switch
            {
                { PageId: { } id } => Results.Redirect($"/pages/{id}"),
                { EmailId: { } emailId } => Results.Redirect($"/emails/{emailId}"),
                _ => Results.Redirect("/library"),
            };
        }).DisableAntiforgery(); // validated explicitly above

        pages.MapPost("/{id:int}/{action:regex(^(refresh|delete|save-links)$)}", async (int id, string action,
            HttpContext context, IAntiforgery antiforgery, PageService service, SettingsService settingsService) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            // The air-gapped NOMAD can't fetch pages; it gets them from the collector.
            if (action != "delete" && !(await settingsService.GetAsync()).UsesInternet) return Results.Redirect($"/pages/{id}");
            switch (action)
            {
                case "refresh":
                    await service.RefreshAsync(id, context.RequestAborted);
                    return Results.Redirect($"/pages/{id}");
                case "delete":
                    await service.DeleteAsync(id, context.RequestAborted);
                    return Results.Redirect($"/library?message={Uri.EscapeDataString("Saved page deleted.")}");
                default:
                    var form = await context.Request.ReadFormAsync();
                    var count = await service.SaveLinkedPagesAsync(id, form["anySite"] == "on", context.RequestAborted);
                    return Results.Redirect($"/library?message={Uri.EscapeDataString(count == 0 ? "No unsaved links to save." : $"Saving {count} linked page(s)…")}");
            }
        }).DisableAntiforgery(); // validated explicitly above

        // "Save page" for a web link on an email's page.
        app.MapPost("/links/{id:int}/archive", async (int id, HttpContext context, IAntiforgery antiforgery,
            IDbContextFactory<NomadInboxDbContext> dbFactory, PageService service) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            await using var db = await dbFactory.CreateDbContextAsync();
            var link = await db.Links.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id);
            if (link is null || link.Kind != LinkKind.Web) return Results.NotFound();
            var pageId = await service.QueueAsync(link.Url, link.InboxEmailId, link.Id, null, context.RequestAborted);
            return Results.Redirect($"/pages/{pageId}");
        }).RequireAuthorization().DisableAntiforgery(); // validated explicitly above
    }

    private static IResult PageResult(HttpContext context, string html)
    {
        context.Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.Content(html, "text/html; charset=utf-8");
    }

    /// <param name="online">False on the air-gapped NOMAD: nothing can be fetched there.</param>
    private static string AppBar(ArchivedPage page, string token, bool online) => $"""
        <nav class="nomad-appbar">
          <a href="/library">← Library</a>
          {(page.InboxEmailId is { } emailId ? $"<a href=\"/emails/{emailId}\">Email</a>" : "")}
          <a href="{Encode(page.FinalUrl ?? page.Url)}">Original</a>
          <span class="spacer"></span>
          {(online ? $"""
          <form method="post" action="/pages/{page.Id}/save-links">{Token(token)}<button type="submit" title="Save the pages this one links to (same site)">Save linked pages</button></form>
          <form method="post" action="/pages/{page.Id}/refresh">{Token(token)}<button type="submit">Refresh</button></form>
          """ : "")}
          <form method="post" action="/pages/{page.Id}/delete">{Token(token)}<button type="submit">Delete</button></form>
        </nav>
        """;

    private static string StatusPage(ArchivedPage page, string token, bool online)
    {
        var (title, body, refresh) = page.Status switch
        {
            PageStatus.Failed => ("Couldn't save this page",
                $"<p>{Encode(page.Error ?? "Unknown error.")}</p>" + (online
                    ? $"<form method=\"post\" action=\"/pages/{page.Id}/refresh\">{Token(token)}<button class=\"nomad-button\" type=\"submit\">Try again</button></form>"
                    : ""),
                false),
            _ when !online => ("Not saved yet", "<p>This NOMAD is air-gapped: the page arrives once the collector has saved it.</p>", false),
            _ => ("Saving this page…", "<p>This page refreshes by itself until it's done.</p>", true),
        };
        return Simple(title, body + $"<p><a href=\"{Encode(page.Url)}\">{Encode(page.Url)}</a> · <a href=\"/library\">Library</a></p>", null, refresh);
    }

    /// <summary>A small page in the saved-page style, for statuses and "Not saved yet".</summary>
    private static string Simple(string title, string body, string? url, bool refresh = false) => $"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        {(refresh ? "<meta http-equiv=\"refresh\" content=\"4\">" : "")}
        <title>{Encode(title)} · NOMAD Inbox</title><link rel="stylesheet" href="/pages/style.css"></head>
        <body><article class="page"><header class="page-header"><p class="page-site">NOMAD Inbox · Saved pages</p><h1>{Encode(title)}</h1></header>
        {body}{(url is null ? "" : $"<p><a href=\"{Encode(url)}\">{Encode(url)}</a></p>")}
        </article></body></html>
        """;

    private static string Token(string token) => $"<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"{Encode(token)}\">";

    private static string Encode(string s) => WebUtility.HtmlEncode(s);
}
