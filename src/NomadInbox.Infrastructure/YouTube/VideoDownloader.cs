using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Files;

namespace NomadInbox.Infrastructure.YouTube;

public sealed record VideoDownloadResult(VideoStatus Status, string? RelativePath, long? Bytes, string? Error);

/// <summary>
/// Saves YouTube videos for offline viewing in a folder Jellyfin can index (NOMAD's shared media folder):
/// <c>{root}/{Channel}/{yyyy-MM-dd} {Title} [{id}].mp4</c>, with <c>-thumb.jpg</c> and a <c>.nfo</c> file
/// (title, date, channel, AI summary) that Jellyfin reads as local metadata.
/// </summary>
public sealed class VideoDownloader(YtDlp ytDlp, string root, ILogger<VideoDownloader> logger)
{
    public const string DownloadTag = "download";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(90);

    public string Root { get; } = Path.GetFullPath(root);

    public static bool ShouldDownload(VideoDownloadMode mode, IEnumerable<string> tags) => mode switch
    {
        VideoDownloadMode.Always => true,
        VideoDownloadMode.Tagged => tags.Contains(DownloadTag, StringComparer.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>Channel folder and file name without extension, e.g. ("MachSquawk", "2026-10-21 Title [id]").</summary>
    public static (string Folder, string BaseName) NameFor(EmailLink link)
    {
        var folder = AttachmentStore.SanitizeFileName(link.Author ?? "YouTube");
        var date = link.PublishedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "undated";
        var title = AttachmentStore.SanitizeFileName(link.Title ?? link.ExternalId ?? "video");
        if (title.Length > 100) title = title[..100].TrimEnd();
        return (folder, $"{date} {title} [{link.ExternalId}]");
    }

    public string? ResolveFullPath(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    public async Task<VideoDownloadResult> DownloadAsync(EmailLink link, InboxEmail? email, InboxSettings settings, CancellationToken ct)
    {
        var (folder, baseName) = NameFor(link);
        var directory = Path.Combine(Root, folder);
        var temp = Directory.CreateTempSubdirectory("nomad-video-");
        try
        {
            Directory.CreateDirectory(directory);
            var height = Math.Clamp(settings.MaxVideoHeight, 144, 2160);
            var result = await ytDlp.RunAsync(
            [
                // Best video up to the height cap plus best audio. H.264/AAC in MP4 is preferred over AV1/VP9: a bit
                // bigger, but it plays on every phone, TV and Jellyfin client without transcoding.
                "-f", $"bv*[height<={height}]+ba/b[height<={height}]/b",
                "-S", $"res:{height},vcodec:h264,acodec:m4a,ext:mp4:m4a",
                "--merge-output-format", "mp4",
                "--max-filesize", $"{Math.Max(1, settings.MaxVideoMegabytes)}M",
                "--no-playlist", "--no-mtime", "--force-overwrites",
                "--write-thumbnail", "--convert-thumbnails", "jpg",
                "-P", directory, "-P", $"temp:{temp.FullName}", // partial files stay out of Jellyfin's view
                "-o", $"{baseName}.%(ext)s", "-o", $"thumbnail:{baseName}-thumb.%(ext)s",
                "--print", "after_move:filepath",
                link.Url,
            ], directory, Timeout, ct);

            var printed = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(l => l.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || File.Exists(l));

            if (result.ExitCode != 0)
                return new VideoDownloadResult(VideoStatus.Failed, null, null, YtDlp.ErrorMessage(result));
            if (printed is null || !File.Exists(printed))
                return new VideoDownloadResult(VideoStatus.TooLarge, null, null,
                    $"Not downloaded: larger than the {settings.MaxVideoMegabytes:N0} MB limit in Settings.");

            var relative = Path.GetRelativePath(Root, printed).Replace(Path.DirectorySeparatorChar, '/');
            var bytes = new FileInfo(printed).Length;
            link.VideoPath = relative;
            await WriteNfoAsync(link, email, ct);
            logger.LogInformation("Downloaded {Url} to {Path} ({Megabytes:N0} MB)", link.Url, relative, bytes / 1024 / 1024);
            return new VideoDownloadResult(VideoStatus.Downloaded, relative, bytes, null);
        }
        finally
        {
            try { temp.Delete(recursive: true); } catch { }
        }
    }

    /// <summary>Writes (or refreshes, once the summary exists) the Jellyfin metadata file next to the video.</summary>
    public async Task WriteNfoAsync(EmailLink link, InboxEmail? email, CancellationToken ct)
    {
        if (link.VideoPath is null || ResolveFullPath(link.VideoPath) is not { } video) return;
        var nfo = Path.ChangeExtension(video, ".nfo");
        await File.WriteAllTextAsync(nfo, BuildNfo(link, email), ct);
    }

    /// <summary>Kodi/Jellyfin "movie" NFO. The plot is the AI summary when there is one, otherwise the description.</summary>
    public static string BuildNfo(EmailLink link, InboxEmail? email)
    {
        var plot = link.Summary is not null ? PlainText(link.Summary) : link.Description ?? "";
        if (link.Chapters is not null) plot += $"\n\nChapters:\n{link.Chapters}";
        plot += $"\n\nSource: {link.Url}";

        var movie = new XElement("movie",
            new XElement("title", link.Title ?? link.ExternalId),
            new XElement("plot", plot.Trim()),
            link.PublishedAt is { } p ? new XElement("premiered", p.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : null,
            link.PublishedAt is { } y ? new XElement("year", y.Year) : null,
            link.DurationSeconds is { } d ? new XElement("runtime", Math.Max(1, d / 60)) : null,
            link.Author is not null ? new XElement("studio", link.Author) : null,
            email?.Category is not null ? new XElement("genre", email.Category) : null,
            (email?.Tags ?? []).Where(t => t is not (DownloadTag or "follow")).Select(t => new XElement("tag", t)),
            new XElement("uniqueid", new XAttribute("type", "youtube"), new XAttribute("default", "true"), link.ExternalId));

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), movie);
        return doc.Declaration + "\n" + doc;
    }

    /// <summary>Jellyfin shows the plot as plain text, so drop Markdown emphasis and turn list markers into bullets.</summary>
    public static string PlainText(string markdown)
    {
        var lines = markdown.Replace("\r", "").Split('\n').Select(line =>
        {
            var l = System.Text.RegularExpressions.Regex.Replace(line, @"(\*\*|__)(.+?)\1", "$2");
            l = System.Text.RegularExpressions.Regex.Replace(l, @"^\s*[*\-+]\s+", "• ");
            l = System.Text.RegularExpressions.Regex.Replace(l, @"^#{1,6}\s+", "");
            return l.TrimEnd();
        });
        return System.Text.RegularExpressions.Regex.Replace(string.Join('\n', lines), @"\n{3,}", "\n\n").Trim();
    }
}
