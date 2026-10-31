namespace NomadInbox.Core.Domain;

/// <summary>A PDF queued for import from an Internet Archive collection; once downloaded it's an item with that PDF attached.</summary>
public class ArchiveFile
{
    public int Id { get; set; }

    /// <summary>The collection's identifier, as in <c>archive.org/details/{identifier}</c>.</summary>
    public required string Identifier { get; set; }
    public required string CollectionTitle { get; set; }
    /// <summary>The PDF's name in the collection.</summary>
    public required string FileName { get; set; }
    /// <summary>The file to download: the same, or the searchable-text version the archive made of a scan.</summary>
    public required string DownloadName { get; set; }
    public string? Md5 { get; set; }
    public long Bytes { get; set; }
    /// <summary>Category of the item it becomes.</summary>
    public string? Category { get; set; }

    public ArchiveFileStatus Status { get; set; } = ArchiveFileStatus.Queued;
    public string? Error { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public int? InboxEmailId { get; set; }
}

public enum ArchiveFileStatus
{
    Queued,
    Done,
    Failed,
}
