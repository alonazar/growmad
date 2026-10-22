namespace NomadInbox.Core.Domain;

/// <summary>
/// A document NOMAD Inbox has put into NOMAD's knowledge base (the AI Assistant's RAG store), so it can be
/// replaced when the item changes and removed when the item is deleted. Deliberately no foreign keys:
/// rows outlive their items until the sync worker has removed the document from NOMAD.
/// </summary>
public class KnowledgeDocument
{
    public int Id { get; set; }

    /// <summary>Which item this is: "email:{id}:note", "link:{id}" or "file:{id}".</summary>
    public required string ItemKey { get; set; }
    public int InboxEmailId { get; set; }

    /// <summary>Name sent to NOMAD (NOMAD adds a random suffix when storing it).</summary>
    public required string FileName { get; set; }
    public string? Collection { get; set; }
    /// <summary>SHA-256 of what was sent plus its collection; a different hash means the document must be replaced.</summary>
    public string? ContentHash { get; set; }

    /// <summary>Path NOMAD returned on upload, e.g. "/storage/kb_uploads/…".</summary>
    public string? NomadFilePath { get; set; }
    /// <summary>NOMAD's identifier for the file (its full path), used to check state and to delete it.</summary>
    public string? NomadSource { get; set; }

    public KnowledgeState State { get; set; } = KnowledgeState.Pending;
    public int Chunks { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? UploadedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum KnowledgeState
{
    /// <summary>Not sent yet (or must be re-sent).</summary>
    Pending,
    /// <summary>Uploaded; NOMAD is embedding it.</summary>
    Uploaded,
    /// <summary>Embedded and searchable in NOMAD's AI chat.</summary>
    Indexed,
    Failed,
}

/// <summary>Message-IDs of deleted items, so a folder rescan or a channel check doesn't bring them back.</summary>
public class DeletedMessage
{
    public required string MessageId { get; set; }
    public DateTimeOffset DeletedAt { get; set; } = DateTimeOffset.UtcNow;
}
