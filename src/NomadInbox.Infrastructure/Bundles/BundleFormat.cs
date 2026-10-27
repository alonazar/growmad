using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NomadInbox.Core.Domain;

namespace NomadInbox.Infrastructure.Bundles;

/// <summary>
/// A bundle is a ZIP file a collector writes and the air-gapped NOMAD imports:
/// <list type="bullet">
/// <item><c>manifest.json</c>: format and version, the collector, the bundle number, and the SHA-256 of every other file;</item>
/// <item><c>items/{id}.json</c>: one email or followed-channel item with its links (text, transcripts, summaries) and
///       attachments (details, extracted text, image descriptions);</item>
/// <item><c>files/{sha256}</c>: attachment files, each stored once;</item>
/// <item><c>pages/{sha256}.html</c> and <c>assets/{key}.{ext}</c>: saved pages' content, and their images and videos
///       (named as in the assets folder, by the first 32 hex characters of their SHA-256);</item>
/// <item><c>videos/{key}.{ext}</c> and <c>videos/{key}-thumb.jpg</c>: downloaded YouTube videos and their thumbnails.</item>
/// </list>
/// A bundle is self-contained: everything its items refer to is in it. Bundles are split at a size limit (FAT32 USB
/// drives take files up to 4 GB).
/// The manifest has a place for a signature; bundles aren't signed yet.
/// </summary>
public static partial class BundleFormat
{
    public const string Format = "nomad-inbox-bundle";
    /// <summary>1: items and attachments. 2: also saved pages and videos.</summary>
    public const int Version = 2;
    public const string ManifestName = "manifest.json";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Entries a bundle may contain besides the manifest.</summary>
    [GeneratedRegex("^(items/[0-9a-f]{32}\\.json|files/[0-9a-f]{64}|pages/[0-9a-f]{64}\\.html|assets/[0-9a-f]{32}\\.[a-z0-9]{1,5}|videos/[0-9a-f]{32}(\\.[a-z0-9]{1,5}|-thumb\\.jpg))$")]
    public static partial Regex EntryName();

    /// <summary>A saved page's asset file name, as the page store writes it.</summary>
    [GeneratedRegex("^[0-9a-f]{32}\\.[a-z0-9]{1,5}$")]
    public static partial Regex AssetName();

    /// <summary>File names of bundles, as the exporter writes them.</summary>
    [GeneratedRegex("^nomad-inbox-[A-Za-z0-9_-]+-[0-9]+-[0-9]{8}-[0-9]{4}\\.zip$")]
    public static partial Regex FileName();

    public static string ItemEntry(string messageId) =>
        $"items/{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(messageId)))[..32]}.json";

    /// <summary>The entries for a downloaded video and its thumbnail, from its path in the videos folder.</summary>
    public static (string Video, string Thumbnail) VideoEntries(string videoPath)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(videoPath)))[..32];
        var ext = Path.GetExtension(videoPath).ToLowerInvariant();
        return ($"videos/{key}{(Regex.IsMatch(ext, "^\\.[a-z0-9]{1,5}$") ? ext : ".mp4")}", $"videos/{key}-thumb.jpg");
    }

    /// <summary>
    /// Whether an entry's name agrees with its content: page content is named by its SHA-256, page assets by its first
    /// 32 characters (the air-gapped side stores and describes them under that name).
    /// </summary>
    public static bool NameMatchesContent(string entry, string sha256) =>
        entry.StartsWith("pages/", StringComparison.Ordinal) ? entry[6..^5] == sha256
        : !entry.StartsWith("assets/", StringComparison.Ordinal) || sha256.StartsWith(entry[7..39], StringComparison.Ordinal);

    public static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    public static string Slug(string name)
    {
        var slug = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "collector" : slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
    }
}

public sealed record BundleManifest(
    string Format,
    int Version,
    string CollectorId,
    string CollectorName,
    int Number,
    DateTimeOffset CreatedAt,
    int Items,
    IReadOnlyDictionary<string, string> Files,
    string? Signature = null);

public sealed record BundleItem(
    string MessageId,
    ItemSource Source,
    string Folder,
    string FromAddress,
    string? FromName,
    string Subject,
    DateTimeOffset ReceivedAt,
    string? Category,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> Fields,
    string Title,
    string? BodyText,
    IReadOnlyList<BundleLink> Links,
    IReadOnlyList<BundleAttachment> Attachments,
    IReadOnlyList<BundlePage>? Pages = null);

/// <param name="Video">The <c>videos/…</c> entry with the downloaded video, or null when it isn't in the bundle.</param>
/// <param name="VideoThumbnail">The <c>videos/…-thumb.jpg</c> entry, when the video has a thumbnail.</param>
public sealed record BundleLink(
    string Url,
    LinkKind Kind,
    int Position,
    LinkStatus Status,
    string? Error,
    string? FinalUrl,
    string? ContentType,
    string? Title,
    string? SiteName,
    string? ContentText,
    DateTimeOffset? FetchedAt,
    string? ExternalId,
    string? Author,
    string? AuthorId,
    string? AuthorUrl,
    DateTimeOffset? PublishedAt,
    int? DurationSeconds,
    string? Description,
    string? ContentSource,
    string? Chapters,
    string? Summary,
    string? SummaryModel,
    DateTimeOffset? SummarizedAt,
    VideoStatus VideoStatus = VideoStatus.None,
    string? VideoPath = null,
    long? VideoBytes = null,
    string? VideoError = null,
    string? Video = null,
    string? VideoThumbnail = null);

/// <param name="File">The <c>files/…</c> entry holding the stored file (a zip for programs), or null when it wasn't stored.</param>
/// <param name="Sha256">SHA-256 of the original file, as the collector computed it.</param>
public sealed record BundleAttachment(
    string FileName,
    string ContentType,
    long SizeBytes,
    string? Sha256,
    bool StoredAsZip,
    string? ExtractedText,
    string? Note,
    string? File,
    BundleImage? Image);

public sealed record BundleImage(
    string Caption,
    string? ImageType,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Tags,
    string? ImageText,
    string? Model);

/// <summary>A saved page that belongs to the item (directly, or saved from one of its pages).</summary>
/// <param name="Content">The <c>pages/…</c> entry with the page's sanitized HTML.</param>
/// <param name="SourceLinkPosition">Position of the item's link that asked for the page, if any.</param>
/// <param name="SavedFromUrl">URL of the saved page whose link led here, if any.</param>
public sealed record BundlePage(
    string Url,
    string? FinalUrl,
    string? Title,
    string? SiteName,
    string? Byline,
    string? Excerpt,
    string? TextContent,
    DateTimeOffset RequestedAt,
    DateTimeOffset? ArchivedAt,
    long Bytes,
    int? SourceLinkPosition,
    string? SavedFromUrl,
    string? Content,
    IReadOnlyList<BundlePageAsset> Assets,
    IReadOnlyList<BundlePageLink> Links);

/// <param name="File">The <c>assets/…</c> entry, or null when the asset wasn't saved or isn't in the bundle.</param>
public sealed record BundlePageAsset(
    string OriginalUrl,
    AssetKind Kind,
    string? FileName,
    string? ContentType,
    long Bytes,
    string? Error,
    string? File,
    BundleImage? Image);

public sealed record BundlePageLink(string Url, string? Text, int Position);
