namespace NomadInbox.Core.Domain;

/// <summary>A YouTube channel whose new videos are picked up and summarized automatically.</summary>
public class FollowedChannel
{
    public int Id { get; set; }

    /// <summary>YouTube channel ID (UC…), used for the channel's RSS feed.</summary>
    public required string ChannelId { get; set; }
    public required string Name { get; set; }
    public required string Url { get; set; }

    // New videos are filed like the email that started the follow.
    public string? Category { get; set; }
    public List<string> Tags { get; set; } = [];

    public bool Enabled { get; set; } = true;
    /// <summary>Only videos published after this are picked up (no backfill of the whole channel).</summary>
    public DateTimeOffset FollowedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastCheckedAt { get; set; }
    public string? LastError { get; set; }
    public int VideosFound { get; set; }
}
