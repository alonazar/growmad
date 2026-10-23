using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Ai;
using NomadInbox.Infrastructure.Data;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.Ingest;
using NomadInbox.Infrastructure.Knowledge;
using NomadInbox.Infrastructure.Pages;
using NomadInbox.Infrastructure.Settings;

namespace NomadInbox.Infrastructure.Images;

public sealed class ImageTrigger : WorkSignal;

/// <summary>What one parsed AI answer says about an image.</summary>
public sealed record ImageFacts(string Caption, string ImageType, IReadOnlyList<string> Categories, IReadOnlyList<string> Tags, string? Text);

/// <summary>
/// Has NOMAD's Ollama describe images for the knowledge base: image attachments, and images on saved pages (the first
/// few per page, skipping icons). Each image gets a caption, a type, categories picked from the ones already used in
/// NOMAD Inbox, tags, and the text it shows. Images are keyed by content, so each picture is described once.
/// </summary>
public sealed class ImageDescriber(
    IDbContextFactory<NomadInboxDbContext> dbFactory,
    SettingsService settingsService,
    IOllamaApi ollama,
    AttachmentStore attachments,
    PageStore pages,
    KnowledgeTrigger knowledgeTrigger,
    ILogger<ImageDescriber> logger)
{
    public const int MaxAttempts = 3;
    public const long MaxImageBytes = 20 * 1024 * 1024;
    /// <summary>Images on saved pages smaller than this are icons, logos or spacers.</summary>
    public const long MinPageImageBytes = 5 * 1024;

    public static readonly string[] ImageTypes =
        ["photo", "diagram", "map", "chart", "screenshot", "document scan", "illustration", "other"];

    // Formats Ollama reads (tested: PNG, JPEG, WebP; SVG is refused). Other image formats are listed as skipped.
    private static readonly HashSet<string> Readable = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp" };
    private static readonly HashSet<string> OtherImages = new(StringComparer.OrdinalIgnoreCase)
        { ".svg", ".heic", ".heif", ".tif", ".tiff", ".ico", ".avif", ".raw", ".dng" };

    private static readonly JsonNode Format = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "caption": { "type": "string" },
            "type": { "type": "string" },
            "categories": { "type": "array", "items": { "type": "string" } },
            "tags": { "type": "array", "items": { "type": "string" } },
            "text": { "type": "string" }
          },
          "required": ["caption", "type", "categories", "tags", "text"]
        }
        """)!;

    /// <summary>Whether an attachment is an image at all (by name or declared type).</summary>
    public static bool IsImage(string fileName, string? contentType) =>
        Readable.Contains(Path.GetExtension(fileName)) || OtherImages.Contains(Path.GetExtension(fileName)) ||
        (contentType ?? "").StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    public static bool CanRead(string fileName) => Readable.Contains(Path.GetExtension(fileName));

    /// <summary>Finds images without a description and queues them. Returns how many were added.</summary>
    public async Task<int> QueueNewAsync(CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);
        if (!settings.DescribeImages) return 0;
        var from = settings.DescribeImagesFrom;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var known = (await db.ImageDescriptions.Select(d => d.ImageKey).ToListAsync(ct)).ToHashSet();
        var added = 0;

        void Add(string key, ImageSource source, string path, string fileName)
        {
            if (!known.Add(key)) return;
            var readable = CanRead(fileName);
            db.ImageDescriptions.Add(new ImageDescription
            {
                ImageKey = key, Source = source, SourcePath = path,
                Status = readable ? ImageDescriptionStatus.Pending : ImageDescriptionStatus.Skipped,
                Error = readable ? null : $"The AI can't read {Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant()} images.",
            });
            added++;
        }

        var files = await db.Attachments
            .Where(a => a.StoredPath != null && a.Sha256 != null && !a.StoredAsZip)
            .Join(db.Emails, a => a.InboxEmailId, e => e.Id,
                (a, e) => new { a.FileName, a.ContentType, a.StoredPath, a.Sha256, e.ReceivedAt, e.ProcessedAt })
            .ToListAsync(ct);
        foreach (var f in files.Where(f => IsImage(f.FileName, f.ContentType) && (from is null || (f.ProcessedAt ?? f.ReceivedAt) >= from)))
            Add(ImageDescription.KeyFor(f.Sha256!), ImageSource.Attachment, f.StoredPath!, f.FileName);

        var saved = await db.Pages.AsNoTracking().Include(p => p.Assets)
            .Where(p => p.Status == PageStatus.Done).ToListAsync(ct);
        foreach (var page in saved.Where(p => from is null || p.ArchivedAt >= from))
        {
            foreach (var asset in PageImages(page, settings))
                Add(Path.GetFileNameWithoutExtension(asset.FileName!), ImageSource.PageAsset, asset.FileName!, asset.FileName!);
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Queued {Count} image(s) for a description", added);
        }
        return added;
    }

    /// <summary>The images on a saved page that get a description: the first few, in page order, without icons.</summary>
    public static IEnumerable<PageAsset> PageImages(ArchivedPage page, InboxSettings settings) =>
        page.Assets
            .Where(a => a.Kind == AssetKind.Image && a.FileName is { Length: > 32 } && a.Bytes >= MinPageImageBytes)
            .OrderBy(a => a.Id)
            .Take(Math.Max(0, settings.MaxImagesPerPage));

    /// <summary>Describes the oldest waiting image. False when there's nothing to do or the AI isn't available.</summary>
    public async Task<bool> DescribeNextAsync(CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);
        if (!settings.IsImageAiConfigured) return false;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.ImageDescriptions
            .Where(d => d.Status == ImageDescriptionStatus.Pending)
            .OrderBy(d => d.CreatedAt).FirstOrDefaultAsync(ct);
        if (item is null) return false;

        try
        {
            await ollama.VersionAsync(settings.OllamaUrl, ct);
        }
        catch (OllamaException)
        {
            return false; // Ollama isn't running: try again later, without using up an attempt
        }

        var path = item.Source == ImageSource.Attachment ? attachments.ResolveFullPath(item.SourcePath) : pages.ResolveAsset(item.SourcePath);
        if (path is null || !File.Exists(path))
        {
            Finish(item, ImageDescriptionStatus.Failed, "The image file is missing.");
        }
        else if (new FileInfo(path).Length > MaxImageBytes)
        {
            Finish(item, ImageDescriptionStatus.Skipped, $"Too large to describe (over {MaxImageBytes / 1024 / 1024} MB).");
        }
        else
        {
            var categories = await KnownCategoriesAsync(db, ct);
            try
            {
                var bytes = await File.ReadAllBytesAsync(path, ct);
                var answer = await ollama.DescribeImageAsync(settings.OllamaUrl, settings.ImageModel, Prompt(categories), bytes, Format, ct);
                var facts = Parse(answer, categories);
                item.Caption = facts.Caption;
                item.ImageType = facts.ImageType;
                item.Categories = [.. facts.Categories];
                item.Tags = [.. facts.Tags];
                item.ImageText = facts.Text;
                item.Model = settings.ImageModel;
                item.DescribedAt = DateTimeOffset.UtcNow;
                Finish(item, ImageDescriptionStatus.Done, null);
                knowledgeTrigger.Signal(); // its knowledge base document changes
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                item.Attempts++;
                item.Error = ex.Message;
                if (item.Attempts >= MaxAttempts) item.Status = ImageDescriptionStatus.Failed;
                logger.LogWarning("Describing image {Key} failed (attempt {Attempt}): {Message}", item.ImageKey, item.Attempts, ex.Message);
            }
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Describes an image again (the Redescribe button).</summary>
    public async Task RedescribeAsync(string imageKey, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ImageDescriptions.Where(d => d.ImageKey == imageKey).ExecuteUpdateAsync(u => u
            .SetProperty(d => d.Status, ImageDescriptionStatus.Pending)
            .SetProperty(d => d.Attempts, 0)
            .SetProperty(d => d.Error, (string?)null), ct);
    }

    /// <summary>Describes the images that were already there when describing was turned on.</summary>
    public async Task DescribeExistingAsync(CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(ct);
        settings.DescribeImagesFrom = null;
        await settingsService.SaveAsync(settings, newPassword: null, ct);
    }

    public static string Prompt(IReadOnlyList<string> categories) =>
        $"""
        Describe this image for a searchable offline archive. Answer in JSON with:
        - caption: one or two plain sentences saying what the image shows.
        - type: exactly one of: {string.Join(", ", ImageTypes)}.
        - categories: {(categories.Count == 0
            ? "always an empty list."
            : $"up to 3 that the image is clearly about, chosen only from this list: {string.Join("; ", categories)}. Don't pick one just because a word in it appears in the image (water in the background isn't a water topic); use an empty list if none clearly fit.")}
        - tags: up to 8 short lowercase keywords.
        - text: the readable text in the image, or an empty string if there is none.
        """;

    /// <summary>Reads the AI's JSON answer, keeping only categories from the list and a known type.</summary>
    public static ImageFacts Parse(string answer, IReadOnlyList<string> categories)
    {
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start) throw new FormatException("The AI's answer wasn't JSON.");
        using var json = JsonDocument.Parse(answer[start..(end + 1)]);
        var root = json.RootElement;

        string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        IEnumerable<string> List(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0)
            : [];

        var caption = Str("caption");
        if (caption.Length == 0) throw new FormatException("The AI's answer had no caption.");
        var type = ImageTypes.FirstOrDefault(t => t.Equals(Str("type"), StringComparison.OrdinalIgnoreCase)) ?? "other";
        var picked = List("categories")
            .Select(c => categories.FirstOrDefault(k => k.Equals(c, StringComparison.OrdinalIgnoreCase)))
            .OfType<string>().Distinct().Take(3).ToList();
        var tags = List("tags").Select(t => t.ToLowerInvariant()).Distinct().Take(8).ToList();
        var text = Str("text");
        return new ImageFacts(caption, type, picked, tags, text.Length == 0 ? null : text);
    }

    private static async Task<IReadOnlyList<string>> KnownCategoriesAsync(NomadInboxDbContext db, CancellationToken ct) =>
        (await db.Emails.Where(e => e.Category != null).Select(e => e.Category!).Distinct().ToListAsync(ct))
            .Order(StringComparer.OrdinalIgnoreCase).Take(100).ToList();

    private static void Finish(ImageDescription item, ImageDescriptionStatus status, string? error)
    {
        item.Status = status;
        item.Error = error;
    }
}

/// <summary>Queues new images and describes them one at a time, apart from mail checks and link summaries.</summary>
public sealed class ImageDescriptionWorker(
    ImageDescriber describer,
    ImageTrigger trigger,
    SettingsService settingsService,
    ILogger<ImageDescriptionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        settingsService.Changed += trigger.Signal;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var didWork = false;
                try
                {
                    await describer.QueueNewAsync(stoppingToken);
                    didWork = await describer.DescribeNextAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Image descriptions failed: {Message}", ex.Message);
                }
                if (!didWork) await trigger.WaitAsync(TimeSpan.FromMinutes(2), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            settingsService.Changed -= trigger.Signal;
        }
    }
}
