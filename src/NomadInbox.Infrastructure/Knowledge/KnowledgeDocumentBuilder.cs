using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.YouTube;

namespace NomadInbox.Infrastructure.Knowledge;

/// <summary>A document that should be in NOMAD's knowledge base: generated Markdown or an attachment file on disk.</summary>
public sealed record DesiredDocument(
    string ItemKey,
    int InboxEmailId,
    string FileName,
    string ContentType,
    string? Collection,
    string Hash,
    byte[]? Content,
    string? FilePath)
{
    public Stream Open() => Content is not null ? new MemoryStream(Content, writable: false) : File.OpenRead(FilePath!);
}

/// <summary>
/// Decides what each processed email contributes to NOMAD's knowledge base:
/// <list type="bullet">
/// <item>every summarized link (web page, video, channel): a Markdown document with its metadata, the AI summary and
///       (optionally) the full article or transcript, which is what NOMAD's chat retrieves from;</item>
/// <item>every stored attachment NOMAD can read (PDF, Word, EPUB, text, images) as the original file; other files
///       with extracted text as Markdown;</item>
/// <item>an "email note" when the email body has text of its own beyond links.</item>
/// </list>
/// </summary>
public static partial class KnowledgeDocumentBuilder
{
    /// <summary>Types NOMAD's knowledge base extracts itself (it handles PDF, DOCX, EPUB, images and text).</summary>
    private static readonly HashSet<string> NativeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".epub", ".txt", ".md", ".markdown", ".csv", ".json", ".xml", ".yaml", ".yml", ".html", ".htm",
        ".png", ".jpg", ".jpeg", ".webp",
    };

    private const int MinNoteChars = 40;

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex Urls();

    public static string? CollectionFor(InboxEmail email, InboxSettings settings)
    {
        var fallback = string.IsNullOrWhiteSpace(settings.KnowledgeDefaultCollection) ? null : settings.KnowledgeDefaultCollection.Trim();
        if (!settings.KnowledgeCollectionPerCategory || string.IsNullOrWhiteSpace(email.Category)) return fallback;
        return email.Category.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? fallback;
    }

    public static IEnumerable<DesiredDocument> Build(InboxEmail email, InboxSettings settings, AttachmentStore store)
    {
        if (email.Status != EmailStatus.Done) yield break;
        var collection = CollectionFor(email, settings);

        foreach (var link in email.Links.OrderBy(l => l.Position))
        {
            var ready = link.Status == LinkStatus.Done || (link.Status == LinkStatus.Fetched && !settings.IsAiConfigured);
            if (!ready) continue;
            var markdown = LinkMarkdown(link, email, settings.KnowledgeIncludeFullText);
            yield return Text($"link:{link.Id}", email.Id, $"{DatePrefix(link.PublishedAt ?? email.ReceivedAt)} {link.Title ?? link.Url}", markdown, collection);
        }

        foreach (var file in email.Attachments.OrderBy(a => a.Id))
        {
            if (file.StoredPath is null) continue;
            var extension = Path.GetExtension(file.FileName);
            if (NativeExtensions.Contains(extension) && store.ResolveFullPath(file.StoredPath) is { } path && File.Exists(path))
            {
                yield return new DesiredDocument($"file:{file.Id}", email.Id, file.FileName, file.ContentType, collection,
                    Hash(Encoding.UTF8.GetBytes(file.Sha256 ?? file.StoredPath), collection), null, path);
            }
            else if (!string.IsNullOrWhiteSpace(file.ExtractedText))
            {
                var markdown = $"# {file.FileName}\n\n{Header(email)}\n\n{file.ExtractedText}";
                yield return Text($"file:{file.Id}", email.Id, file.FileName, markdown, collection);
            }
        }

        if (email.Source == ItemSource.Email && NoteText(email.BodyText) is not null)
            yield return Text($"email:{email.Id}:note", email.Id,
                $"{DatePrefix(email.ReceivedAt)} {(string.IsNullOrWhiteSpace(email.Title) ? "Email" : email.Title)} (note)",
                EmailNoteMarkdown(email), collection);
    }

    public static string LinkMarkdown(EmailLink link, InboxEmail email, bool includeFullText)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {link.Title ?? link.Url}").AppendLine();

        var kind = link.Kind switch
        {
            LinkKind.YouTubeVideo => "YouTube video",
            LinkKind.YouTubeChannel => "YouTube channel",
            LinkKind.YouTubePlaylist => "YouTube playlist",
            _ => "Web page",
        };
        sb.AppendLine($"- Type: {kind}");
        sb.AppendLine($"- Source: {link.FinalUrl ?? link.Url}");
        if (link.Kind == LinkKind.YouTubeVideo && link.Author is not null) sb.AppendLine($"- Channel: {link.Author}");
        else if (link.SiteName is not null && link.Kind == LinkKind.Web) sb.AppendLine($"- Site: {link.SiteName}");
        if (link.PublishedAt is { } published) sb.AppendLine($"- Published: {published:yyyy-MM-dd}");
        if (link.DurationSeconds is { } seconds) sb.AppendLine($"- Length: {VttTranscript.Stamp(seconds)}");
        sb.AppendLine(Header(email));
        sb.AppendLine();

        sb.AppendLine("## Summary").AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(link.Summary) ? "(No AI summary yet.)" : link.Summary.Trim()).AppendLine();

        if (link.Chapters is not null)
            sb.AppendLine("## Chapters").AppendLine().AppendLine(link.Chapters).AppendLine();

        if (includeFullText && !string.IsNullOrWhiteSpace(link.ContentText))
        {
            var heading = link.Kind switch
            {
                LinkKind.YouTubeVideo when link.ContentSource?.StartsWith("Video description") == true => "Description",
                LinkKind.YouTubeVideo => $"Transcript ({link.ContentSource})",
                LinkKind.YouTubeChannel or LinkKind.YouTubePlaylist => "Recent videos",
                _ => "Full text",
            };
            sb.AppendLine($"## {heading}").AppendLine().AppendLine(link.ContentText.Trim());
        }
        return sb.ToString().Replace("\r\n", "\n"); // same document on Windows and Linux
    }

    public static string EmailNoteMarkdown(InboxEmail email)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {(string.IsNullOrWhiteSpace(email.Title) ? email.Subject : email.Title)}").AppendLine();
        sb.AppendLine($"- From: {email.FromName ?? email.FromAddress}");
        sb.AppendLine(Header(email));
        foreach (var (key, value) in email.Fields) sb.AppendLine($"- {key}: {value}");
        sb.AppendLine().AppendLine("## Note").AppendLine().AppendLine(email.BodyText!.Trim());

        var related = email.Links.Select(l => $"- {l.Title ?? l.Url} ({l.Url})")
            .Concat(email.Attachments.Select(a => $"- Attachment: {a.FileName}")).ToList();
        if (related.Count > 0) sb.AppendLine().AppendLine("## Sent with this note").AppendLine().AppendLine(string.Join('\n', related));
        return sb.ToString().Replace("\r\n", "\n");
    }

    /// <summary>The body, if it says something beyond pasting links.</summary>
    public static string? NoteText(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var words = Urls().Replace(body, " ").Trim();
        return words.Length >= MinNoteChars ? body : null;
    }

    private static string Header(InboxEmail email)
    {
        var filed = string.Join(" ", new[] { email.Category }.Concat(email.Tags.Select(t => "#" + t)).Where(s => !string.IsNullOrWhiteSpace(s)));
        var lines = new List<string>();
        if (filed.Length > 0) lines.Add($"- Filed under: {filed}");
        lines.Add($"- Received: {email.ReceivedAt:yyyy-MM-dd} ({(email.Source == ItemSource.FollowedChannel ? "followed channel" : "NOMAD Inbox email")})");
        return string.Join('\n', lines);
    }

    private static DesiredDocument Text(string key, int emailId, string name, string markdown, string? collection)
    {
        var bytes = Encoding.UTF8.GetBytes(markdown);
        var fileName = AttachmentStore.SanitizeFileName(name);
        if (fileName.Length > 90) fileName = fileName[..90].TrimEnd();
        return new DesiredDocument(key, emailId, fileName + ".md", "text/markdown", collection, Hash(bytes, collection), bytes, null);
    }

    private static string DatePrefix(DateTimeOffset date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Hash(byte[] content, string? collection)
    {
        // The collection is part of the hash: moving an item to another collection re-files it in NOMAD.
        var suffix = Encoding.UTF8.GetBytes("\n" + (collection ?? "").ToLowerInvariant());
        return Convert.ToHexStringLower(SHA256.HashData(content.Concat(suffix).ToArray()));
    }
}
