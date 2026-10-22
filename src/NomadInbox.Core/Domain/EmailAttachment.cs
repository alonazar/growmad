namespace NomadInbox.Core.Domain;

/// <summary>A file attached to an email and filed under the attachment store.</summary>
public class EmailAttachment
{
    public int Id { get; set; }
    public int InboxEmailId { get; set; }

    /// <summary>File name as sent (sanitized).</summary>
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }

    /// <summary>Path relative to the attachment store root; null when the file was not stored (e.g. too large).</summary>
    public string? StoredPath { get; set; }
    /// <summary>True when an identical file was already stored and this record points at it.</summary>
    public bool IsDuplicate { get; set; }
    /// <summary>True when the stored file is a zip holding the original (programs and scripts), so it can't run by accident.</summary>
    public bool StoredAsZip { get; set; }

    /// <summary>Plain text extracted for search and summaries; null when the type is unsupported.</summary>
    public string? ExtractedText { get; set; }
    /// <summary>Why the file wasn't stored or why text extraction didn't work.</summary>
    public string? Note { get; set; }
}
