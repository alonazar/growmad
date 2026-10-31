namespace NomadInbox.Core.Domain;

/// <summary>
/// A Gmail message whose subject matched the configured prefix.
/// The poller records the envelope and parsed subject; the processor then adds the body, links and attachments.
/// </summary>
public class InboxEmail
{
    public int Id { get; set; }

    /// <summary>RFC 5322 Message-ID; used to de-duplicate across UIDVALIDITY resets.</summary>
    public required string MessageId { get; set; }

    public uint ImapUid { get; set; }
    /// <summary>UIDVALIDITY the <see cref="ImapUid"/> belongs to; if the folder's changes, the message is found by Message-ID.</summary>
    public uint UidValidity { get; set; }
    public required string Folder { get; set; }

    public required string FromAddress { get; set; }
    public string? FromName { get; set; }
    public required string Subject { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }

    // Parsed from the subject line (see SubjectParser).
    public string? Category { get; set; }
    public List<string> Tags { get; set; } = [];
    public Dictionary<string, string> Fields { get; set; } = [];
    public string Title { get; set; } = "";

    public EmailStatus Status { get; set; } = EmailStatus.Pending;
    public string? StatusReason { get; set; }

    /// <summary>A real email, or an item created for a new video on a followed channel.</summary>
    public ItemSource Source { get; set; } = ItemSource.Email;
    public int? FollowedChannelId { get; set; }

    // Filled in by the processor.
    public string? BodyText { get; set; }
    public List<EmailLink> Links { get; set; } = [];
    public List<EmailAttachment> Attachments { get; set; } = [];
    /// <summary>Denormalized so lists can show counts without loading the collections.</summary>
    public int LinkCount { get; set; }
    public int AttachmentCount { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum ItemSource
{
    Email,
    FollowedChannel,
    /// <summary>A PDF imported from an Internet Archive collection.</summary>
    InternetArchive,
}

public enum EmailStatus
{
    /// <summary>Recorded, waiting for the processor to fetch the full message.</summary>
    Pending,
    /// <summary>Sender is not on the allowlist; kept for visibility but never processed.</summary>
    Ignored,
    Processing,
    Done,
    Failed,
}
