using NomadInbox.Core.Appearance;
using NomadInbox.Core.Content;

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

    /// <summary>Attachments of other kinds are listed but not stored. Allowed executables are stored zipped.</summary>
    public AttachmentKinds AllowedAttachmentKinds { get; set; } = DefaultAttachmentKinds;

    public const AttachmentKinds DefaultAttachmentKinds =
        AttachmentKinds.Images | AttachmentKinds.Video | AttachmentKinds.Audio | AttachmentKinds.Documents |
        AttachmentKinds.Archives | AttachmentKinds.Other;

    /// <summary>Links to local-network addresses (192.168.x.x, localhost, …) are refused unless this is on.</summary>
    public bool AllowPrivateNetworkLinks { get; set; }

    /// <summary>Open pages with almost no text in a headless browser, for sites built by JavaScript.</summary>
    public bool RenderJavaScriptPages { get; set; } = true;

    // AI summaries (NOMAD's Ollama).
    public bool SummariesEnabled { get; set; } = true;
    public string OllamaUrl { get; set; } = "http://nomad_ollama:11434";
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

    // NOMAD knowledge base (Phase 5).
    public bool KnowledgeBaseEnabled { get; set; } = true;
    /// <summary>NOMAD's Command Center, as seen from the container.</summary>
    public string NomadUrl { get; set; } = "http://nomad_admin:8080";
    /// <summary>Send the full article/transcript with each summary (better answers) or only the summary.</summary>
    public bool KnowledgeIncludeFullText { get; set; } = true;
    /// <summary>File each item into a collection named after its top-level category.</summary>
    public bool KnowledgeCollectionPerCategory { get; set; } = true;
    /// <summary>Collection for uncategorized items, or for everything when per-category is off.</summary>
    public string KnowledgeDefaultCollection { get; set; } = "nomad inbox";

    // Saved pages (page archive).
    /// <summary>Which web links get a local copy: emails tagged #archive (default), all, or none.</summary>
    public PageArchiveMode PageArchiveMode { get; set; } = PageArchiveMode.Tagged;
    /// <summary>A page's content plus media stops downloading media beyond this.</summary>
    public int MaxPageMegabytes { get; set; } = 100;
    /// <summary>Single images/videos bigger than this are left out.</summary>
    public int MaxPageAssetMegabytes { get; set; } = 25;
    /// <summary>"Save all linked pages" queues at most this many.</summary>
    public int SaveLinkedPagesLimit { get; set; } = 25;
    /// <summary>Custom Scriban page template; empty uses the built-in one.</summary>
    public string? PageTemplate { get; set; }
    /// <summary>Custom page stylesheet; empty uses the built-in one.</summary>
    public string? PageStylesheet { get; set; }

    // Appearance of the web app (not of saved pages).
    /// <summary>A <see cref="SkinCatalog"/> id; unknown ids show the default skin.</summary>
    public string Skin { get; set; } = SkinCatalog.DefaultId;
    public ColorMode ColorMode { get; set; } = ColorMode.System;

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

public enum PageArchiveMode
{
    Never,
    /// <summary>Only web links in emails tagged #archive.</summary>
    Tagged,
    Always,
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
