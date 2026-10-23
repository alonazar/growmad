using System.Security.Cryptography;

namespace NomadInbox.Infrastructure.Files;

/// <summary>
/// Files attachments on disk as <c>{root}/{Category}/{yyyy-MM}/{file name}</c>, so the folder is browsable by hand.
/// Paths returned and stored in the database are relative to the root and use '/'.
/// </summary>
public sealed class AttachmentStore(string root)
{
    public const string UncategorizedFolder = "Uncategorized";

    public string Root { get; } = Path.GetFullPath(root);

    public static string ComputeSha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public bool Exists(string relativePath) => ResolveFullPath(relativePath) is { } p && File.Exists(p);

    /// <summary>Full path for a stored relative path, or null if it would escape the root.</summary>
    public string? ResolveFullPath(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>
    /// Writes the file and returns its relative path. If a file with the same name exists, it is reused when the
    /// content is identical, otherwise the new file gets a " (2)", " (3)", … suffix.
    /// </summary>
    public async Task<string> SaveAsync(string? category, DateTimeOffset received, string fileName, byte[] content, string sha256, CancellationToken ct)
    {
        var folder = Path.Combine([.. CategorySegments(category), received.ToString("yyyy-MM")]);
        var directory = Path.Combine(Root, folder);
        Directory.CreateDirectory(directory);

        var name = SanitizeFileName(fileName);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? name : $"{stem} ({n}){extension}";
            var fullPath = Path.Combine(directory, candidate);

            if (File.Exists(fullPath))
            {
                if (ComputeSha256(await File.ReadAllBytesAsync(fullPath, ct)) == sha256)
                    return ToRelative(fullPath);
                continue;
            }

            // Write to a temp file first so a crash never leaves a half-written attachment under the real name.
            var temp = fullPath + ".partial";
            await File.WriteAllBytesAsync(temp, content, ct);
            File.Move(temp, fullPath, overwrite: false);
            return ToRelative(fullPath);
        }
    }

    public static string SanitizeFileName(string? fileName)
    {
        // Split by hand: Path.GetFileName on Windows would treat "a:b.txt" as drive "a:".
        var name = (fileName ?? "").Replace('\\', '/').Split('/')[^1].Trim();
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '|', '?', '*']).ToHashSet();
        name = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (name.Length == 0) name = "attachment";

        if (name.Length > 120)
        {
            // Keep a real extension. A long "extension" is just text after a dot ("…server. Send mail…"), so then the
            // whole name is cut, never only the part before that dot (which can be shorter than the cut).
            var ext = Path.GetExtension(name);
            if (ext.Length > 15) ext = "";
            name = name[..(120 - ext.Length)].TrimEnd(' ', '.') + ext;
        }
        return name;
    }

    private static IEnumerable<string> CategorySegments(string? category)
    {
        var segments = (category ?? "")
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s is not ("." or ".."))
            .Select(SanitizeFileName)
            .ToList();
        return segments.Count == 0 ? [UncategorizedFolder] : segments;
    }

    private string ToRelative(string fullPath) =>
        Path.GetRelativePath(Root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
}
