namespace NomadInbox.Core.Domain;

/// <summary>A link found in an email body, with its fetched text and AI summary.</summary>
public class EmailLink
{
    public int Id { get; set; }
    public int InboxEmailId { get; set; }

    /// <summary>Normalized URL (tracking parameters removed, YouTube forms unified).</summary>
    public required string Url { get; set; }
    public LinkKind Kind { get; set; }
    /// <summary>Order of first appearance in the email.</summary>
    public int Position { get; set; }

    public LinkStatus Status { get; set; } = LinkStatus.Pending;
    /// <summary>Why the link failed or was skipped, or the last summary error.</summary>
    public string? Error { get; set; }
    public int Attempts { get; set; }

    // Filled in when the page is fetched.
    public string? FinalUrl { get; set; }
    public string? ContentType { get; set; }
    public string? Title { get; set; }
    public string? SiteName { get; set; }
    /// <summary>Readable text of the page (article body, not navigation), capped.</summary>
    public string? ContentText { get; set; }
    public DateTimeOffset? FetchedAt { get; set; }

    // YouTube details (also filled for web pages where known).
    /// <summary>Video, channel or playlist ID.</summary>
    public string? ExternalId { get; set; }
    /// <summary>Channel name for videos; uploader for playlists.</summary>
    public string? Author { get; set; }
    /// <summary>YouTube channel ID (UC…) of the video's channel, or of the channel itself.</summary>
    public string? AuthorId { get; set; }
    public string? AuthorUrl { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int? DurationSeconds { get; set; }
    /// <summary>Video/channel description as published.</summary>
    public string? Description { get; set; }
    /// <summary>Where <see cref="ContentText"/> came from, e.g. "Auto-captions (en-orig)".</summary>
    public string? ContentSource { get; set; }
    /// <summary>Video chapters, one per line as "m:ss Title".</summary>
    public string? Chapters { get; set; }

    // The video file itself (YouTube videos, when downloading is on).
    public VideoStatus VideoStatus { get; set; } = VideoStatus.None;
    /// <summary>Path relative to the videos folder, e.g. "MachSquawk/2026-10-21 Title [id].mp4".</summary>
    public string? VideoPath { get; set; }
    public long? VideoBytes { get; set; }
    public string? VideoError { get; set; }

    // Filled in when the text is summarized.
    /// <summary>Markdown summary written by the AI model.</summary>
    public string? Summary { get; set; }
    public string? SummaryModel { get; set; }
    public DateTimeOffset? SummarizedAt { get; set; }
}

public enum LinkKind
{
    Web,
    YouTubeVideo,
    YouTubePlaylist,
    YouTubeChannel,
}

public enum VideoStatus
{
    None,
    Queued,
    Downloaded,
    /// <summary>Over the size limit in Settings.</summary>
    TooLarge,
    Failed,
}

public enum LinkStatus
{
    /// <summary>Waiting to be fetched (YouTube links wait for Phase 4).</summary>
    Pending,
    /// <summary>Text extracted; waiting for an AI summary.</summary>
    Fetched,
    Done,
    /// <summary>Not something we can read (e.g. an image or a zip file).</summary>
    Skipped,
    Failed,
}
