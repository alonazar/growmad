using System.Security.Cryptography;

namespace NomadInbox.Infrastructure.Pages;

/// <summary>
/// Where saved pages live on disk.
/// <list type="bullet">
/// <item><c>{data}/pages/{id}/content.html</c>: each page's sanitized HTML (with markers), private to the app.</item>
/// <item><c>{export}/assets/{sha256}.{ext}</c>: images and videos, stored once even when several pages use them.</item>
/// <item><c>{export}/pages/{id}-{slug}/index.html</c>, <c>{export}/index.html</c>, <c>{export}/style.css</c>: the plain-HTML
///       copy for browsing outside the app (NOMAD's File Browser).</item>
/// </list>
/// The export folder is shared with NOMAD, so media is never stored twice.
/// </summary>
public sealed class PageStore(string dataDirectory, string exportDirectory)
{
    public string ContentRoot { get; } = Path.GetFullPath(Path.Combine(dataDirectory, "pages"));
    public string ExportRoot { get; } = Path.GetFullPath(exportDirectory);
    public string AssetRoot => Path.Combine(ExportRoot, "assets");

    public string ContentPathFor(int pageId) => Path.Combine(ContentRoot, pageId.ToString(), "content.html");

    public async Task WriteContentAsync(int pageId, string html, CancellationToken ct)
    {
        var path = ContentPathFor(pageId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, html, ct);
    }

    public async Task<string?> ReadContentAsync(int pageId, CancellationToken ct)
    {
        var path = ContentPathFor(pageId);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
    }

    public void DeleteContent(int pageId)
    {
        var dir = Path.GetDirectoryName(ContentPathFor(pageId))!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>Stores media by content hash and returns its file name; identical files are stored once.</summary>
    public async Task<string> SaveAssetAsync(byte[] content, string contentType, string url, CancellationToken ct)
    {
        Directory.CreateDirectory(AssetRoot);
        var name = Convert.ToHexStringLower(SHA256.HashData(content))[..32] + ExtensionFor(contentType, url);
        var path = Path.Combine(AssetRoot, name);
        if (!File.Exists(path))
        {
            var temp = path + ".partial";
            await File.WriteAllBytesAsync(temp, content, ct);
            File.Move(temp, path, overwrite: true);
        }
        return name;
    }

    /// <summary>Full path of a stored asset, or null if the name is not a plain file name in the assets folder.</summary>
    public string? ResolveAsset(string fileName)
    {
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..")) return null;
        var path = Path.Combine(AssetRoot, fileName);
        return File.Exists(path) ? path : null;
    }

    public void DeleteAsset(string fileName)
    {
        if (ResolveAsset(fileName) is { } path) File.Delete(path);
    }

    public static string ExtensionFor(string contentType, string url)
    {
        var ext = contentType.Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" or "image/pjpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/avif" => ".avif",
            "image/svg+xml" => ".svg",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/ogg" => ".ogv",
            "application/pdf" => ".pdf",
            _ => null,
        };
        if (ext is not null) return ext;
        var fromUrl = Uri.TryCreate(url, UriKind.Absolute, out var u) ? Path.GetExtension(u.AbsolutePath).ToLowerInvariant() : "";
        return fromUrl.Length is > 1 and <= 6 && fromUrl.All(c => char.IsLetterOrDigit(c) || c == '.') ? fromUrl : ".bin";
    }
}
