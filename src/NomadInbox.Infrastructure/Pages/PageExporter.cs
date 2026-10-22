using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Data;
using NomadInbox.Infrastructure.Settings;

namespace NomadInbox.Infrastructure.Pages;

/// <summary>
/// Keeps the plain-HTML copy in the export folder (NOMAD's File Browser) current: one folder per page with relative
/// links to other saved pages and to the shared assets, plus index.html and style.css. Saving or deleting a page also
/// refreshes the pages that link to it, so their links point at the right place.
/// </summary>
public sealed class PageExporter(
    IDbContextFactory<NomadInboxDbContext> dbFactory,
    PageRenderer renderer,
    PageStore store,
    SettingsService settingsService,
    ILogger<PageExporter> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string PagesRoot => Path.Combine(store.ExportRoot, "pages");

    /// <summary>A page was saved, refreshed or deleted: re-export it and the pages linking to it, then the index.</summary>
    public async Task PageChangedAsync(int pageId, string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var settings = await settingsService.GetAsync(ct);
            await WriteStylesheetAsync(settings, ct);
            await ExportPageAsync(pageId, settings, ct);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var linking = await db.PageLinks.Where(l => l.Url == url && l.ArchivedPageId != pageId)
                .Select(l => l.ArchivedPageId).Distinct().ToListAsync(ct);
            foreach (var id in linking) await ExportPageAsync(id, settings, ct);

            await WriteIndexAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Updating the exported pages failed: {Message}", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Everything again: after a template or stylesheet change.</summary>
    public async Task ExportAllAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var settings = await settingsService.GetAsync(ct);
            await WriteStylesheetAsync(settings, ct);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var ids = await db.Pages.Where(p => p.Status == PageStatus.Done).Select(p => p.Id).ToListAsync(ct);
            foreach (var id in ids) await ExportPageAsync(id, settings, ct);
            await WriteIndexAsync(ct);
            logger.LogInformation("Exported {Count} page(s) to {Folder}", ids.Count, store.ExportRoot);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ExportPageAsync(int pageId, InboxSettings settings, CancellationToken ct)
    {
        RemoveFolders(pageId);
        string? html;
        try
        {
            html = await renderer.RenderAsync(pageId, PageView.Export, settings, ct: ct);
        }
        catch (PageTemplateException ex)
        {
            logger.LogWarning("Page {Id} wasn't exported: {Message}", pageId, ex.Message);
            return;
        }
        if (html is null) return; // deleted or not saved (yet)

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var title = await db.Pages.Where(p => p.Id == pageId).Select(p => p.Title).SingleOrDefaultAsync(ct);
        var folder = Path.Combine(PagesRoot, PageRenderer.ExportFolder(pageId, title));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "index.html"), html, ct);
    }

    /// <summary>Removes a page's folder(s); the title, and so the folder name, may have changed since the last export.</summary>
    private void RemoveFolders(int pageId)
    {
        if (!Directory.Exists(PagesRoot)) return;
        foreach (var dir in Directory.EnumerateDirectories(PagesRoot, $"{pageId}-*").Append(Path.Combine(PagesRoot, pageId.ToString())))
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private async Task WriteIndexAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(store.ExportRoot);
        await File.WriteAllTextAsync(Path.Combine(store.ExportRoot, "index.html"), await renderer.RenderIndexAsync(ct), ct);
    }

    private async Task WriteStylesheetAsync(InboxSettings settings, CancellationToken ct)
    {
        Directory.CreateDirectory(store.ExportRoot);
        var css = string.IsNullOrWhiteSpace(settings.PageStylesheet) ? PageRenderer.DefaultStylesheet : settings.PageStylesheet;
        await File.WriteAllTextAsync(Path.Combine(store.ExportRoot, "style.css"), css, ct);
    }
}
