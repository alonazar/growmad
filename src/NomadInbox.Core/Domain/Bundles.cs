namespace NomadInbox.Core.Domain;

/// <summary>How this NOMAD Inbox is used (Settings → Mode).</summary>
public enum AppMode
{
    /// <summary>Next to NOMAD: checks mail, fetches, summarizes and sends to NOMAD's knowledge base.</summary>
    Connected,
    /// <summary>A wired machine without NOMAD: does everything that needs the internet and exports bundles (ZIP files).</summary>
    Collector,
    /// <summary>The air-gapped NOMAD: imports bundles, then summarizes and sends to NOMAD's knowledge base.</summary>
    AirGapped,
}

/// <summary>A bundle this collector has written.</summary>
public class ExportedBundle
{
    public int Number { get; set; }
    public required string FileName { get; set; }
    public int Items { get; set; }
    public long Bytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>What was last exported for an item, so only new and changed items go into the next bundle.</summary>
public class ExportedItem
{
    public required string MessageId { get; set; }
    public required string ContentHash { get; set; }
    public int BundleNumber { get; set; }
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A bundle the air-gapped side tried to import, and what happened.</summary>
public class ImportedBundle
{
    public int Id { get; set; }
    public required string FileName { get; set; }
    public string? CollectorId { get; set; }
    public string? CollectorName { get; set; }
    public int? Number { get; set; }
    public ImportStatus Status { get; set; }
    public int Items { get; set; }
    public int Skipped { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum ImportStatus
{
    Imported,
    /// <summary>The same bundle had been imported before; nothing changed.</summary>
    AlreadyImported,
    /// <summary>Not a valid bundle, or its files don't match (nothing was imported).</summary>
    Rejected,
}

/// <summary>Which bundle an imported item came from, so an older bundle imported later doesn't overwrite it.</summary>
public class ImportedItem
{
    public required string MessageId { get; set; }
    public required string CollectorId { get; set; }
    public int BundleNumber { get; set; }
    public required string ContentHash { get; set; }
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
}
