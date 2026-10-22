namespace NomadInbox.Core.Domain;

/// <summary>
/// A local copy of a web page's main content: sanitized HTML (paragraphs, headings, bold/italic, lists, tables …),
/// its images and videos downloaded, and its links recorded. It's shown through an editable template; links to pages
/// that are saved too open the local copy, the rest can be saved on demand.
/// </summary>
public class ArchivedPage
{
    public int Id { get; set; }

    /// <summary>Normalized URL (same normalization as <see cref="EmailLink.Url"/>), unique.</summary>
    public required string Url { get; set; }
    public string? FinalUrl { get; set; }

    public PageStatus Status { get; set; } = PageStatus.Queued;
    public string? Error { get; set; }

    public string? Title { get; set; }
    public string? SiteName { get; set; }
    public string? Byline { get; set; }
    public string? Excerpt { get; set; }
    /// <summary>Plain text of the content, for searching the Library.</summary>
    public string? TextContent { get; set; }
    /// <summary>Where the page's sanitized HTML is stored, relative to the data folder.</summary>
    public string? ContentPath { get; set; }

    /// <summary>The email this page belongs to (for pages saved from other pages: the email that started the chain).</summary>
    public int? InboxEmailId { get; set; }
    /// <summary>The email link that asked for this page, if any.</summary>
    public int? SourceLinkId { get; set; }
    /// <summary>The saved page whose link led here, if any.</summary>
    public int? SavedFromPageId { get; set; }

    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ArchivedAt { get; set; }
    /// <summary>Content plus all media, in bytes.</summary>
    public long Bytes { get; set; }

    public List<PageAsset> Assets { get; set; } = [];
    public List<PageLink> Links { get; set; } = [];
}

public enum PageStatus
{
    Queued,
    Saving,
    Done,
    Failed,
}

/// <summary>An image, video or video poster used by a saved page, stored once by content hash.</summary>
public class PageAsset
{
    public int Id { get; set; }
    public int ArchivedPageId { get; set; }

    public required string OriginalUrl { get; set; }
    public AssetKind Kind { get; set; }
    /// <summary>File in the shared assets folder ("{sha256}.{ext}"); null when not saved.</summary>
    public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public long Bytes { get; set; }
    public string? Error { get; set; }
}

public enum AssetKind
{
    Image,
    Video,
    /// <summary>A file the link pointed to directly (e.g. a PDF).</summary>
    Document,
}

/// <summary>A link found in a saved page's content. Whether it opens a local copy is decided when the page is shown.</summary>
public class PageLink
{
    public int Id { get; set; }
    public int ArchivedPageId { get; set; }

    /// <summary>Normalized target URL, without fragment.</summary>
    public required string Url { get; set; }
    public string? Text { get; set; }
    /// <summary>Order in the content; the content HTML refers to links by this number.</summary>
    public int Position { get; set; }
}
