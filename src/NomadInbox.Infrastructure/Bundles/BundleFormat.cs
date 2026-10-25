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
/// <item><c>files/{sha256}</c>: attachment files, each stored once.</item>
/// </list>
/// The manifest has a place for a signature; bundles aren't signed yet.
/// </summary>
public static partial class BundleFormat
{
    public const string Format = "nomad-inbox-bundle";
    public const int Version = 1;
    public const string ManifestName = "manifest.json";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Entries a bundle may contain besides the manifest.</summary>
    [GeneratedRegex("^(items/[0-9a-f]{32}\\.json|files/[0-9a-f]{64})$")]
    public static partial Regex EntryName();

    /// <summary>File names of bundles, as the exporter writes them.</summary>
    [GeneratedRegex("^nomad-inbox-[A-Za-z0-9_-]+-[0-9]+-[0-9]{8}-[0-9]{4}\\.zip$")]
    public static partial Regex FileName();

    public static string ItemEntry(string messageId) =>
        $"items/{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(messageId)))[..32]}.json";

    public static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

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
    IReadOnlyList<BundleAttachment> Attachments);

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
    DateTimeOffset? SummarizedAt);

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
