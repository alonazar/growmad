using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Bundles;

namespace NomadInbox.Web;

/// <summary>
/// Bundles for air-gapped operation: on a collector, "Create bundle now" and downloading bundles; on the air-gapped
/// NOMAD, uploading bundles (from a USB drive, through the browser) to import them.
/// </summary>
public static class BundleEndpoints
{
    /// <summary>Uploads can be large (bundles carry attachments, later videos).</summary>
    private const long MaxUploadBytes = 8L * 1024 * 1024 * 1024;

    public static void MapBundleEndpoints(this WebApplication app)
    {
        app.MapPost("/export/create", async (HttpContext context, IAntiforgery antiforgery, BundleExporter exporter) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            string message;
            try
            {
                var result = await exporter.ExportAsync(context.RequestAborted);
                message = result is null
                    ? "Nothing new to export yet."
                    : $"Bundle {result.Number} is ready: {result.FileName} ({result.Items} item(s)).";
            }
            catch (InvalidOperationException ex)
            {
                message = ex.Message;
            }
            return Results.LocalRedirect($"~/export?message={Uri.EscapeDataString(message)}");
        }).RequireAuthorization().DisableAntiforgery(); // validated explicitly above

        app.MapGet("/export/files/{name}", (string name, BundleExporter exporter) =>
        {
            var path = Path.Combine(exporter.ExportDirectory, name);
            return BundleFormat.FileName().IsMatch(name) && File.Exists(path)
                ? Results.File(path, "application/zip", name)
                : Results.NotFound();
        }).RequireAuthorization();

        app.MapPost("/import/upload", async (HttpContext context, IAntiforgery antiforgery, BundleImporter importer) =>
        {
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
                size.MaxRequestBodySize = MaxUploadBytes;
            // Read the form with room for large files first; the antiforgery check then uses it.
            var form = await context.Request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = MaxUploadBytes }, context.RequestAborted);
            await antiforgery.ValidateRequestAsync(context);

            var results = new List<string>();
            foreach (var file in form.Files.Where(f => f.Length > 0))
            {
                var temp = Path.Combine(Path.GetTempPath(), $"nomad-inbox-upload-{Guid.NewGuid():N}.zip");
                try
                {
                    await using (var target = File.Create(temp))
                        await file.CopyToAsync(target, context.RequestAborted);
                    var result = await importer.ImportAsync(temp, file.FileName, context.RequestAborted);
                    results.Add($"{Path.GetFileName(file.FileName)}: {(result.Status == ImportStatus.Rejected ? "rejected, " : "")}{result.Message}");
                }
                finally
                {
                    File.Delete(temp);
                }
            }
            var message = results.Count == 0 ? "Choose one or more bundle files (.zip) first." : string.Join(" · ", results);
            return Results.LocalRedirect($"~/import?message={Uri.EscapeDataString(message)}");
        }).RequireAuthorization().DisableAntiforgery(); // validated explicitly above
    }
}
