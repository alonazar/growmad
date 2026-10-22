namespace NomadInbox.Infrastructure.YouTube;

public sealed record VideoChapter(double StartSeconds, string Title);

public sealed record VideoDetails(
    string Id,
    string Title,
    string? Channel,
    string? ChannelId,
    string? ChannelUrl,
    DateTimeOffset? PublishedAt,
    int? DurationSeconds,
    string? Description,
    IReadOnlyList<VideoChapter> Chapters,
    string? Transcript,
    string? TranscriptSource,
    string? Note);

public sealed record RecentVideo(string Id, string Title, int? DurationSeconds, long? Views);

/// <summary>A channel's uploads tab or a playlist, as listed by yt-dlp.</summary>
public sealed record ChannelDetails(
    string Id,
    string Title,
    string? ChannelId,
    string? Url,
    string? Uploader,
    string? Description,
    long? Followers,
    bool IsPlaylist,
    IReadOnlyList<RecentVideo> Videos);

public sealed record FeedEntry(string VideoId, string Title, DateTimeOffset PublishedAt);

public sealed class YouTubeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What the link pipeline needs from YouTube; faked in tests.</summary>
public interface IYouTubeClient
{
    Task<VideoDetails> GetVideoAsync(string url, IReadOnlyList<string> preferredLanguages, CancellationToken ct);
    Task<ChannelDetails> GetChannelAsync(string url, int recentVideos, CancellationToken ct);
    /// <summary>The newest ~15 uploads from the channel's public RSS feed.</summary>
    Task<IReadOnlyList<FeedEntry>> GetFeedAsync(string channelId, CancellationToken ct);
}
