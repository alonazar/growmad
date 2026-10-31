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

/// <summary>What the builder needs beyond each email: image descriptions by key, and saved pages by the link they came from.</summary>
public sealed record KnowledgeContext(
    IReadOnlyDictionary<string, ImageDescription> Images,
    IReadOnlyDictionary<int, ArchivedPage> PagesByLink)
{
    public static readonly KnowledgeContext Empty =
        new(new Dictionary<string, ImageDescription>(), new Dictionary<int, ArchivedPage>());
}

/// <summary>Links back into NOMAD Inbox from knowledge base documents; none when no address is set in Settings.</summary>
public sealed class InboxLinks(string? baseUrl)
{
    public static readonly InboxLinks None = new(null);

    private readonly string? _base = Uri.TryCreate(baseUrl?.Trim().TrimEnd('/'), UriKind.Absolute, out var uri) &&
                                     uri.Scheme is "http" or "https" ? uri.AbsoluteUri.TrimEnd('/') : null;

    public string? Email(int id) => _base is null ? null : $"{_base}/emails/{id}";
    public string? Attachment(int id) => _base is null ? null : $"{_base}/attachments/{id}";
    public string? Page(int id) => _base is null ? null : $"{_base}/pages/{id}";
    public string? PageAsset(string fileName) => _base is null ? null : $"{_base}/pages/assets/{Uri.EscapeDataString(fileName)}";
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

    public static IEnumerable<DesiredDocument> Build(InboxEmail email, InboxSettings settings, AttachmentStore store, KnowledgeContext? context = null)
    {
        if (email.Status != EmailStatus.Done) yield break;
        context ??= KnowledgeContext.Empty;
        var collection = CollectionFor(email, settings);
        var links = new InboxLinks(settings.PublicBaseUrl);

        foreach (var link in email.Links.OrderBy(l => l.Position))
        {
            var ready = link.Status == LinkStatus.Done || (link.Status == LinkStatus.Fetched && !settings.IsAiConfigured);
            if (!ready) continue;
            context.PagesByLink.TryGetValue(link.Id, out var page);
            var markdown = LinkMarkdown(link, email, settings.KnowledgeIncludeFullText, links, page,
                settings.DescribeImages ? context.Images : null);
            yield return Text($"link:{link.Id}", email.Id, $"{DatePrefix(link.PublishedAt ?? email.ReceivedAt)} {link.Title ?? link.Url}", markdown, collection);
        }

        foreach (var file in email.Attachments.OrderBy(a => a.Id))
        {
            if (file.StoredPath is null) continue;
            var extension = Path.GetExtension(file.FileName);
            // A described image goes in as its image card (NOMAD's chat can't show the picture, but can search the card).
            if (settings.DescribeImages && file.Sha256 is not null &&
                context.Images.TryGetValue(ImageDescription.KeyFor(file.Sha256), out var image) && image.Status == ImageDescriptionStatus.Done)
            {
                yield return Text($"file:{file.Id}", email.Id, file.FileName, ImageCardMarkdown(file, image, email, links), collection);
            }
            else if (NativeExtensions.Contains(extension) && store.ResolveFullPath(file.StoredPath) is { } path && File.Exists(path))
            {
                yield return new DesiredDocument($"file:{file.Id}", email.Id, AsciiFileName(file.FileName), file.ContentType, collection,
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
                EmailNoteMarkdown(email, links), collection);
    }

    /// <summary>An image attachment as NOMAD's chat can use it: what the AI saw in it, and where the picture is.</summary>
    public static string ImageCardMarkdown(EmailAttachment file, ImageDescription image, InboxEmail email, InboxLinks? links = null)
    {
        links ??= InboxLinks.None;
        var sb = new StringBuilder();
        sb.AppendLine($"# Image: {file.FileName}").AppendLine();
        sb.AppendLine($"- Type: {image.ImageType ?? "image"}");
        if (image.Categories.Count > 0) sb.AppendLine($"- Categories: {string.Join(", ", image.Categories)}");
        if (image.Tags.Count > 0) sb.AppendLine($"- Tags: {string.Join(", ", image.Tags)}");
        sb.AppendLine($"- Sent with: {(string.IsNullOrWhiteSpace(email.Title) ? email.Subject : email.Title)}");
        sb.AppendLine(Header(email));
        if (links.Attachment(file.Id) is { } local) sb.AppendLine($"- Local copy: [{file.FileName}]({local})");
        if (links.Email(email.Id) is { } item) sb.AppendLine($"- In NOMAD Inbox: [the email]({item})");
        sb.AppendLine().AppendLine("## Description").AppendLine().AppendLine(image.Caption?.Trim());
        if (!string.IsNullOrWhiteSpace(image.ImageText))
            sb.AppendLine().AppendLine("## Text in the image").AppendLine().AppendLine(image.ImageText.Trim());
        return sb.ToString().Replace("\r\n", "\n");
    }

    public static string LinkMarkdown(EmailLink link, InboxEmail email, bool includeFullText, InboxLinks? links = null,
        ArchivedPage? page = null, IReadOnlyDictionary<string, ImageDescription>? images = null)
    {
        links ??= InboxLinks.None;
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
        var saved = page is { Status: PageStatus.Done } ? page : null;
        if (saved is not null && links.Page(saved.Id) is { } copy) sb.AppendLine($"- Saved copy: [{link.Title ?? "the page"}]({copy})");
        if (links.Email(email.Id) is { } item) sb.AppendLine($"- In NOMAD Inbox: [the email]({item})");
        sb.AppendLine();

        sb.AppendLine("## Summary").AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(link.Summary) ? "(No AI summary yet.)" : link.Summary.Trim()).AppendLine();

        if (link.Chapters is not null)
            sb.AppendLine("## Chapters").AppendLine().AppendLine(link.Chapters).AppendLine();

        // Images on the saved copy of the page, as the AI described them.
        var described = saved is null || images is null ? [] : saved.Assets
            .Where(a => a.Kind == AssetKind.Image && a.FileName is not null).OrderBy(a => a.Id)
            .Select(a => (Asset: a, Image: images.GetValueOrDefault(Path.GetFileNameWithoutExtension(a.FileName!))))
            .Where(x => x.Image is { Status: ImageDescriptionStatus.Done })
            .ToList();
        if (described.Count > 0)
        {
            sb.AppendLine("## Images").AppendLine();
            foreach (var (asset, image) in described)
            {
                var about = new[] { image!.ImageType }.Concat(image.Categories).Where(s => !string.IsNullOrWhiteSpace(s));
                var line = $"- {image.Caption?.Trim()} ({string.Join("; ", about)}";
                if (image.Tags.Count > 0) line += $"; tags: {string.Join(", ", image.Tags)}";
                line += ")";
                if (links.PageAsset(asset.FileName!) is { } local) line += $" [image]({local})";
                sb.AppendLine(line);
                if (!string.IsNullOrWhiteSpace(image.ImageText)) sb.AppendLine($"  Text in the image: {image.ImageText.Trim().ReplaceLineEndings(" ")}");
            }
            sb.AppendLine();
        }

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

    public static string EmailNoteMarkdown(InboxEmail email, InboxLinks? links = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {(string.IsNullOrWhiteSpace(email.Title) ? email.Subject : email.Title)}").AppendLine();
        sb.AppendLine($"- From: {email.FromName ?? email.FromAddress}");
        sb.AppendLine(Header(email));
        if ((links ?? InboxLinks.None).Email(email.Id) is { } item) sb.AppendLine($"- In NOMAD Inbox: [the email]({item})");
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
        lines.Add($"- Received: {email.ReceivedAt:yyyy-MM-dd} ({(email.Source switch { ItemSource.FollowedChannel => "followed channel", ItemSource.InternetArchive => "Internet Archive", _ => "NOMAD Inbox email" })})");
        return string.Join('\n', lines);
    }

    private static DesiredDocument Text(string key, int emailId, string name, string markdown, string? collection)
    {
        var bytes = Encoding.UTF8.GetBytes(markdown);
        var fileName = AsciiFileName(AttachmentStore.SanitizeFileName(name));
        if (fileName.Length > 90) fileName = fileName[..90].TrimEnd();
        return new DesiredDocument(key, emailId, fileName + ".md", "text/markdown", collection, Hash(bytes, collection), bytes, null);
    }

    /// <summary>
    /// The name to send NOMAD, in plain ASCII: for any other character .NET sends the upload's file name encoded
    /// (<c>=?utf-8?B?…?=</c>), which NOMAD stores as the name and then loses the extension. Accents are dropped
    /// (é → e), typographic quotes and dashes become plain ones, anything else becomes '_'.
    /// </summary>
    public static string AsciiFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (c < 128) { sb.Append(c); continue; }
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue; // the accent of é
            sb.Append(c switch
            {
                '‘' or '’' or '‚' or '′' => "'",
                '“' or '”' or '„' or '″' or '«' or '»' => "\"",
                '‐' or '‑' or '‒' or '–' or '—' or '―' or '−' => "-",
                ' ' or ' ' or ' ' => " ",
                '…' => "...",
                'ß' => "ss", 'æ' => "ae", 'Æ' => "AE", 'œ' => "oe", 'Œ' => "OE",
                'ø' => "o", 'Ø' => "O", 'ł' => "l", 'Ł' => "L",
                _ => "_",
            });
        }
        return sb.ToString();
    }

    private static string DatePrefix(DateTimeOffset date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Hash(byte[] content, string? collection)
    {
        // The collection is part of the hash: moving an item to another collection re-files it in NOMAD.
        var suffix = Encoding.UTF8.GetBytes("\n" + (collection ?? "").ToLowerInvariant());
        return Convert.ToHexStringLower(SHA256.HashData(content.Concat(suffix).ToArray()));
    }
}
