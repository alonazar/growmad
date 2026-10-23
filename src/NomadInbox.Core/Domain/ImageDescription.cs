namespace NomadInbox.Core.Domain;

/// <summary>
/// What NOMAD's AI sees in one image: an image attachment or an image on a saved page. Keyed by content, so the same
/// picture in several emails or pages is described once.
/// </summary>
public class ImageDescription
{
    /// <summary>The first 32 hex characters of the image's SHA-256 (also how saved-page images are named on disk).</summary>
    public required string ImageKey { get; set; }

    /// <summary>Where the bytes are read from: an attachment's stored path, or a saved-page asset's file name.</summary>
    public ImageSource Source { get; set; }
    public required string SourcePath { get; set; }

    public ImageDescriptionStatus Status { get; set; } = ImageDescriptionStatus.Pending;
    /// <summary>One or two sentences.</summary>
    public string? Caption { get; set; }
    /// <summary>photo, diagram, map, chart, screenshot, document scan, illustration or other.</summary>
    public string? ImageType { get; set; }
    /// <summary>Picked from the categories already used in NOMAD Inbox.</summary>
    public List<string> Categories { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    /// <summary>Text visible in the image (signs, labels, screenshot text).</summary>
    public string? ImageText { get; set; }

    public string? Model { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DescribedAt { get; set; }

    public static string KeyFor(string sha256Hex) => (sha256Hex.Length > 32 ? sha256Hex[..32] : sha256Hex).ToLowerInvariant();
}

public enum ImageSource
{
    Attachment,
    PageAsset,
}

public enum ImageDescriptionStatus
{
    /// <summary>Waiting for the AI.</summary>
    Pending,
    Done,
    /// <summary>The AI couldn't describe it after a few tries.</summary>
    Failed,
    /// <summary>A format the AI can't read (SVG, HEIC, …).</summary>
    Skipped,
}
