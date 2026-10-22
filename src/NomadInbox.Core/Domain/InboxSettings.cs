namespace NomadInbox.Core.Domain;

/// <summary>
/// User-editable settings, stored as a single row (Id = 1) so they can be changed from the web UI.
/// </summary>
public class InboxSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public string ImapHost { get; set; } = "imap.gmail.com";
    public int ImapPort { get; set; } = 993;
    public string Username { get; set; } = "";

    /// <summary>App password, encrypted with ASP.NET Data Protection. Never store it in plain text.</summary>
    public string? ProtectedPassword { get; set; }

    public string Folder { get; set; } = "INBOX";
    public string SubjectPrefix { get; set; } = "NOMAD:";

    /// <summary>One address or @domain per line. Empty means "accept everyone" (not recommended).</summary>
    public string AllowedSenders { get; set; } = "";

    /// <summary>On the first scan, ignore mail older than this. Null scans the whole folder.</summary>
    public DateTimeOffset? ImportSince { get; set; }

    public bool PollingEnabled { get; set; }
    public int PollIntervalMinutes { get; set; } = 5;

    /// <summary>Tag processed mail with Gmail labels (needs a Gmail server; ignored elsewhere).</summary>
    public bool ApplyLabels { get; set; } = true;
    public string ProcessedLabel { get; set; } = "NOMAD/Processed";
    public string FailedLabel { get; set; } = "NOMAD/Failed";

    /// <summary>Attachments larger than this are listed but not stored.</summary>
    public int MaxAttachmentMegabytes { get; set; } = 50;

    /// <summary>Links to local-network addresses (192.168.x.x, localhost, …) are refused unless this is on.</summary>
    public bool AllowPrivateNetworkLinks { get; set; }

    /// <summary>Open pages with almost no text in a headless browser, for sites built by JavaScript.</summary>
    public bool RenderJavaScriptPages { get; set; } = true;

    // AI summaries (NOMAD's Ollama).
    public bool SummariesEnabled { get; set; } = true;
    public string OllamaUrl { get; set; } = "http://host.docker.internal:11434";
    public string OllamaModel { get; set; } = "";
    /// <summary>Context window requested from Ollama; long pages are split to fit.</summary>
    public int OllamaContextTokens { get; set; } = 8192;
    /// <summary>Custom system prompt; empty uses the built-in one.</summary>
    public string? SummaryPrompt { get; set; }

    // YouTube.
    /// <summary>Preferred transcript languages, most preferred first (comma separated).</summary>
    public string TranscriptLanguages { get; set; } = "en";
    /// <summary>How often followed channels are checked for new videos.</summary>
    public int FollowCheckHours { get; set; } = 6;

    /// <summary>Whether YouTube videos themselves are saved for offline viewing.</summary>
    public VideoDownloadMode VideoDownloadMode { get; set; } = VideoDownloadMode.Tagged;
    /// <summary>Highest resolution to download (e.g. 720 for 720p).</summary>
    public int MaxVideoHeight { get; set; } = 720;
    /// <summary>Videos bigger than this are not downloaded.</summary>
    public int MaxVideoMegabytes { get; set; } = 2048;

    public IReadOnlyList<string> TranscriptLanguageList =>
        TranscriptLanguages.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.ToLowerInvariant()).DefaultIfEmpty("en").ToList();

    public bool IsAiConfigured =>
        SummariesEnabled && !string.IsNullOrWhiteSpace(OllamaUrl) && !string.IsNullOrWhiteSpace(OllamaModel);

    public bool IsMailConfigured =>
        !string.IsNullOrWhiteSpace(ImapHost) &&
        !string.IsNullOrWhiteSpace(Username) &&
        !string.IsNullOrEmpty(ProtectedPassword);

    public IReadOnlyList<string> AllowedSenderList =>
        AllowedSenders
            .Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .ToList();

    public bool IsSenderAllowed(string address)
    {
        var allowed = AllowedSenderList;
        if (allowed.Count == 0) return true;

        var normalized = address.Trim().ToLowerInvariant();
        return allowed.Any(entry => entry.StartsWith('@')
            ? normalized.EndsWith(entry, StringComparison.Ordinal)
            : normalized == entry);
    }
}

public enum VideoDownloadMode
{
    Never,
    /// <summary>Only for emails tagged #download (and channels followed from such an email).</summary>
    Tagged,
    Always,
}

/// <summary>Where the poller left off in a folder. Reset when the server's UIDVALIDITY changes.</summary>
public class MailboxCursor
{
    public required string Folder { get; set; }
    public uint UidValidity { get; set; }
    public uint LastSeenUid { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
