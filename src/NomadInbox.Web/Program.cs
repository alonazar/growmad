using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components.Server.Circuits;
using NomadInbox.Web.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure;
using NomadInbox.Infrastructure.Data;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.Images;
using NomadInbox.Infrastructure.Ingest;
using NomadInbox.Infrastructure.Knowledge;
using NomadInbox.Infrastructure.Settings;
using NomadInbox.Web;
using NomadInbox.Web.Auth;
using NomadInbox.Web.Components;

// `dotnet NomadInbox.Web.dll install-browser` installs headless Chromium (used while building the Docker image).
if (args is ["install-browser"])
{
    Environment.Exit(NomadInbox.Infrastructure.Web.PlaywrightRenderer.InstallBrowser());
}
// `check-browser` launches it once as the current user; the Docker build runs this as the app user.
if (args is ["check-browser"])
{
    Environment.Exit(await NomadInbox.Infrastructure.Web.PlaywrightRenderer.CheckBrowserAsync());
}

var builder = WebApplication.CreateBuilder(args);

var configuredDataDirectory = builder.Configuration["NomadInbox:DataDirectory"];
var dataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredDataDirectory)
    ? Path.Combine(builder.Environment.ContentRootPath, "data")
    : configuredDataDirectory);
var videoDirectory = builder.Configuration["NomadInbox:VideoDirectory"];
var pagesDirectory = builder.Configuration["NomadInbox:PagesExportDirectory"];
var exportDirectory = builder.Configuration["NomadInbox:ExportDirectory"];
var importDirectory = builder.Configuration["NomadInbox:ImportDirectory"];
builder.Services.AddNomadInbox(dataDirectory,
    string.IsNullOrWhiteSpace(videoDirectory) ? null : videoDirectory,
    string.IsNullOrWhiteSpace(pagesDirectory) ? null : pagesDirectory,
    string.IsNullOrWhiteSpace(exportDirectory) ? null : exportDirectory,
    string.IsNullOrWhiteSpace(importDirectory) ? null : importDirectory);

builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.Section));
builder.Services.AddSingleton<AdminCredentialValidator>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.ReturnUrlParameter = "returnUrl";
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
        o.Cookie.Name = "nomadinbox.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(o => o.DetailedErrors = builder.Environment.IsDevelopment());
builder.Services.AddScoped<CircuitHandler, LoggingCircuitHandler>();

var app = builder.Build();

// Fail fast on a missing admin password rather than running an unprotected UI.
app.Services.GetRequiredService<AdminCredentialValidator>().EnsureConfigured();

await using (var db = await app.Services.GetRequiredService<IDbContextFactory<NomadInboxDbContext>>().CreateDbContextAsync())
{
    await db.Database.MigrateAsync();
}
app.Logger.LogInformation("Data directory: {DataDirectory}", dataDirectory);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
// Served over plain HTTP on the LAN (next to NOMAD), so no HTTPS redirection or HSTS here.
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.MapPost("/account/logout", async (HttpContext context, [FromForm] string? returnUrl) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("~/login");
});

// The top bar's light/dark toggle: System → Light → Dark → System.
app.MapPost("/appearance/mode", async (SettingsService settings, [FromForm] string? returnUrl) =>
{
    var current = await settings.GetAsync();
    await settings.SaveAppearanceAsync(current.Skin, UiAppearance.Next(current.ColorMode));
    var local = returnUrl is ['/', not '/' and not '\\', ..] or "/";
    return Results.LocalRedirect(local ? returnUrl! : "~/");
}).RequireAuthorization();

// The Redescribe button on an image: ask the AI again.
app.MapPost("/images/{key:regex(^[0-9a-f]{{32}}$)}/redescribe", async (string key, ImageDescriber describer, ImageTrigger trigger,
    [FromForm] string? returnUrl) =>
{
    await describer.RedescribeAsync(key, CancellationToken.None);
    trigger.Signal();
    var local = returnUrl is ['/', not '/' and not '\\', ..];
    return Results.LocalRedirect(local ? returnUrl! : "~/");
}).RequireAuthorization();

app.MapGet("/attachments/{id:int}", async (int id, IDbContextFactory<NomadInboxDbContext> dbFactory, AttachmentStore store) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var file = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id);
    var path = file?.StoredPath is { } relative ? store.ResolveFullPath(relative) : null;
    if (path is null || !File.Exists(path)) return Results.NotFound();
    // Programs and scripts are stored zipped; download them as the zip, never as something that runs.
    return file!.StoredAsZip
        ? Results.File(path, "application/zip", file.FileName + ".zip")
        : Results.File(path, file.ContentType, file.FileName);
}).RequireAuthorization();

app.MapPost("/emails/{id:int}/delete", async (int id, HttpContext context, IAntiforgery antiforgery,
    ItemDeletionService deletion, KnowledgeTrigger knowledgeTrigger) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var form = await context.Request.ReadFormAsync();
    var result = await deletion.DeleteAsync(id, deleteFiles: form["deleteFiles"] == "on", context.RequestAborted);
    knowledgeTrigger.Signal(); // removes its documents from NOMAD's knowledge base
    var message = result.Found ? $"Deleted{(result.FilesDeleted > 0 ? $", along with {result.FilesDeleted} file(s)" : "")}." : "It was already gone.";
    return Results.LocalRedirect($"~/emails?message={Uri.EscapeDataString(message)}");
}).RequireAuthorization().DisableAntiforgery(); // validated explicitly above

app.MapPost("/emails/{id:int}/knowledge/resend", async (int id, HttpContext context, IAntiforgery antiforgery,
    KnowledgeSyncService sync, KnowledgeTrigger knowledgeTrigger) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await sync.ResendEmailAsync(id, context.RequestAborted);
    knowledgeTrigger.Signal();
    return Results.LocalRedirect($"~/emails/{id}");
}).RequireAuthorization().DisableAntiforgery(); // validated explicitly above

// Saved videos, streamed with range support so the browser's player can seek.
app.MapGet("/videos/{linkId:int}", async (int linkId, IDbContextFactory<NomadInboxDbContext> dbFactory,
    NomadInbox.Infrastructure.YouTube.VideoDownloader videos) =>
{
    await using var db = await dbFactory.CreateDbContextAsync();
    var path = await db.Links.Where(l => l.Id == linkId).Select(l => l.VideoPath).SingleOrDefaultAsync();
    var full = path is null ? null : videos.ResolveFullPath(path);
    return full is not null && File.Exists(full)
        ? Results.File(full, "video/mp4", Path.GetFileName(full), enableRangeProcessing: true)
        : Results.NotFound();
}).RequireAuthorization();

app.MapPost("/emails/{id:int}/reprocess", async (int id, HttpContext context, IAntiforgery antiforgery,
    IDbContextFactory<NomadInboxDbContext> dbFactory, PollTrigger trigger) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await using var db = await dbFactory.CreateDbContextAsync();
    var updated = await db.Emails.Where(e => e.Id == id && e.Status != EmailStatus.Processing)
        .ExecuteUpdateAsync(u => u
            .SetProperty(e => e.Status, EmailStatus.Pending)
            .SetProperty(e => e.StatusReason, (string?)null));
    if (updated > 0) trigger.Signal(PollTriggerReason.PollNow);
    return Results.LocalRedirect($"~/emails/{id}");
}).RequireAuthorization().DisableAntiforgery(); // validated explicitly above

// "refetch": fetch the page again (after a failure); "resummarize": keep the text, ask the model again;
// "follow": follow the YouTube channel of a channel or video link.
app.MapPost("/links/{id:int}/{mode:regex(^(refetch|resummarize|follow|download)$)}", async (int id, string mode, HttpContext context,
    IAntiforgery antiforgery, IDbContextFactory<NomadInboxDbContext> dbFactory, LinkTrigger trigger, FollowService follows,
    VideoTrigger videoTrigger) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await using var db = await dbFactory.CreateDbContextAsync();
    var link = await db.Links.SingleOrDefaultAsync(l => l.Id == id);
    if (link is null) return Results.NotFound();

    if (mode == "download")
    {
        if (link.Kind != LinkKind.YouTubeVideo) return Results.BadRequest("Only YouTube videos can be downloaded.");
        link.VideoStatus = VideoStatus.Queued;
        link.VideoError = null;
        await db.SaveChangesAsync();
        videoTrigger.Signal();
        return Results.LocalRedirect($"~/emails/{link.InboxEmailId}");
    }

    if (mode == "follow")
    {
        if (link.AuthorId is null) return Results.BadRequest("This link has no YouTube channel yet; wait until it has been fetched.");
        var email = await db.Emails.AsNoTracking().SingleAsync(e => e.Id == link.InboxEmailId);
        var name = (link.Kind == LinkKind.YouTubeChannel ? link.Title : link.Author) ?? link.AuthorId;
        await follows.FollowAsync(link.AuthorId, name, link.AuthorUrl ?? $"https://www.youtube.com/channel/{link.AuthorId}",
            email.Category, email.Tags, context.RequestAborted);
        return Results.LocalRedirect($"~/emails/{link.InboxEmailId}");
    }

    if (mode == "resummarize" && !string.IsNullOrEmpty(link.ContentText))
        link.Status = LinkStatus.Fetched;
    else
        link.Status = LinkStatus.Pending;
    link.Error = null;
    link.Attempts = 0;
    await db.SaveChangesAsync();
    trigger.Signal();
    return Results.LocalRedirect($"~/emails/{link.InboxEmailId}");
}).RequireAuthorization().DisableAntiforgery(); // validated explicitly above

app.MapPost("/channels/{id:int}/{mode:regex(^(check|toggle|delete)$)}", async (int id, string mode, HttpContext context,
    IAntiforgery antiforgery, IDbContextFactory<NomadInboxDbContext> dbFactory, FollowService follows) =>
{
    await antiforgery.ValidateRequestAsync(context);
    string? message = null;
    if (mode == "check")
    {
        var found = await follows.CheckAsync(id, context.RequestAborted);
        message = found == 0 ? "No new videos." : $"{found} new video(s) added; summaries will follow.";
    }
    else
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var channel = await db.FollowedChannels.SingleOrDefaultAsync(c => c.Id == id);
        if (channel is null) return Results.NotFound();
        if (mode == "delete")
        {
            db.FollowedChannels.Remove(channel);
            message = $"Stopped following {channel.Name}.";
        }
        else
        {
            channel.Enabled = !channel.Enabled;
            if (channel.Enabled) channel.FollowedAt = DateTimeOffset.UtcNow; // don't backfill the paused period
        }
        await db.SaveChangesAsync();
    }
    return Results.LocalRedirect(message is null ? "~/channels" : $"~/channels?message={Uri.EscapeDataString(message)}");
}).RequireAuthorization().DisableAntiforgery(); // validated explicitly above

app.MapPageEndpoints();
app.MapBundleEndpoints();

// Every page requires sign-in unless it opts out with [AllowAnonymous] (login, error, not found).
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();

app.Run();
