using System.Buffers.Binary;
using System.IO.Compression;

namespace NomadInbox.Core.Content;

/// <summary>The kinds of attachment the filter in Settings → Attachments can let through.</summary>
[Flags]
public enum AttachmentKinds
{
    None = 0,
    Images = 1,
    Video = 2,
    Audio = 4,
    /// <summary>Text, PDF, Office and e-book files, web pages, data and map files.</summary>
    Documents = 8,
    /// <summary>zip, 7z, rar, tar, gz, … A zip that contains a program counts as <see cref="Executables"/>.</summary>
    Archives = 16,
    Other = 32,
    /// <summary>Programs, installers, scripts, disk images and macro-enabled Office files.</summary>
    Executables = 64,
}

/// <summary>
/// Sorts an attachment into one <see cref="AttachmentKinds"/> value. Executables are recognized by name, by the
/// declared content type and by the file's own bytes (Windows, Linux and macOS programs, #! scripts, zips that
/// contain programs), so renaming setup.exe to setup.jpg doesn't get it through.
/// </summary>
public static class AttachmentClassifier
{
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows programs, installers and scripts.
        ".exe", ".dll", ".com", ".scr", ".pif", ".cpl", ".sys", ".drv", ".ocx", ".msi", ".msp", ".mst", ".msix",
        ".msixbundle", ".appx", ".appxbundle", ".appinstaller", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs",
        ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".lnk", ".reg", ".scf", ".inf", ".gadget", ".application",
        // Cross-platform and Unix.
        ".jar", ".apk", ".aab", ".xapk", ".sh", ".bash", ".zsh", ".csh", ".ksh", ".fish", ".run", ".deb", ".rpm",
        ".appimage", ".snap", ".flatpak", ".py", ".pyw", ".pyz", ".pl", ".rb", ".php",
        // macOS.
        ".app", ".command", ".pkg", ".mpkg", ".dmg", ".scpt", ".applescript", ".workflow",
        // Disk images, which open as a drive and can carry programs.
        ".iso", ".img", ".vhd", ".vhdx",
        // Office files that can run macros.
        ".docm", ".dotm", ".xlsm", ".xltm", ".xlam", ".pptm", ".potm", ".ppam", ".ppsm", ".sldm",
    };

    private static readonly HashSet<string> ExecutableContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/x-msdownload", "application/x-msdos-program", "application/x-dosexec", "application/x-executable",
        "application/x-elf", "application/x-mach-binary", "application/vnd.microsoft.portable-executable",
        "application/x-msi", "application/x-ms-installer", "application/x-ms-shortcut", "application/x-sh",
        "application/x-shellscript", "application/x-bat", "application/x-csh", "application/hta",
        "application/java-archive", "application/x-java-archive", "application/vnd.android.package-archive",
        "application/x-apple-diskimage", "application/x-iso9660-image", "application/x-debian-package",
        "application/x-rpm", "text/x-shellscript", "text/x-python", "text/x-perl", "text/vbscript",
        "application/x-vbscript", "application/vnd.ms-word.document.macroenabled.12",
        "application/vnd.ms-excel.sheet.macroenabled.12", "application/vnd.ms-powerpoint.presentation.macroenabled.12",
    };

    private static readonly Dictionary<string, AttachmentKinds> KindByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = AttachmentKinds.Images, [".jpeg"] = AttachmentKinds.Images, [".png"] = AttachmentKinds.Images,
        [".gif"] = AttachmentKinds.Images, [".webp"] = AttachmentKinds.Images, [".bmp"] = AttachmentKinds.Images,
        [".tif"] = AttachmentKinds.Images, [".tiff"] = AttachmentKinds.Images, [".heic"] = AttachmentKinds.Images,
        [".heif"] = AttachmentKinds.Images, [".avif"] = AttachmentKinds.Images, [".svg"] = AttachmentKinds.Images,
        [".ico"] = AttachmentKinds.Images, [".raw"] = AttachmentKinds.Images, [".dng"] = AttachmentKinds.Images,

        [".mp4"] = AttachmentKinds.Video, [".m4v"] = AttachmentKinds.Video, [".mkv"] = AttachmentKinds.Video,
        [".mov"] = AttachmentKinds.Video, [".avi"] = AttachmentKinds.Video, [".webm"] = AttachmentKinds.Video,
        [".wmv"] = AttachmentKinds.Video, [".flv"] = AttachmentKinds.Video, [".mpg"] = AttachmentKinds.Video,
        [".mpeg"] = AttachmentKinds.Video, [".3gp"] = AttachmentKinds.Video,

        [".mp3"] = AttachmentKinds.Audio, [".wav"] = AttachmentKinds.Audio, [".ogg"] = AttachmentKinds.Audio,
        [".oga"] = AttachmentKinds.Audio, [".opus"] = AttachmentKinds.Audio, [".m4a"] = AttachmentKinds.Audio,
        [".aac"] = AttachmentKinds.Audio, [".flac"] = AttachmentKinds.Audio, [".wma"] = AttachmentKinds.Audio,
        [".mid"] = AttachmentKinds.Audio, [".midi"] = AttachmentKinds.Audio,

        [".txt"] = AttachmentKinds.Documents, [".md"] = AttachmentKinds.Documents, [".csv"] = AttachmentKinds.Documents,
        [".tsv"] = AttachmentKinds.Documents, [".log"] = AttachmentKinds.Documents, [".pdf"] = AttachmentKinds.Documents,
        [".doc"] = AttachmentKinds.Documents, [".docx"] = AttachmentKinds.Documents, [".xls"] = AttachmentKinds.Documents,
        [".xlsx"] = AttachmentKinds.Documents, [".ppt"] = AttachmentKinds.Documents, [".pptx"] = AttachmentKinds.Documents,
        [".odt"] = AttachmentKinds.Documents, [".ods"] = AttachmentKinds.Documents, [".odp"] = AttachmentKinds.Documents,
        [".rtf"] = AttachmentKinds.Documents, [".epub"] = AttachmentKinds.Documents, [".mobi"] = AttachmentKinds.Documents,
        [".azw3"] = AttachmentKinds.Documents, [".html"] = AttachmentKinds.Documents, [".htm"] = AttachmentKinds.Documents,
        [".json"] = AttachmentKinds.Documents, [".xml"] = AttachmentKinds.Documents, [".yaml"] = AttachmentKinds.Documents,
        [".yml"] = AttachmentKinds.Documents, [".ics"] = AttachmentKinds.Documents, [".vcf"] = AttachmentKinds.Documents,
        [".eml"] = AttachmentKinds.Documents, [".gpx"] = AttachmentKinds.Documents, [".kml"] = AttachmentKinds.Documents,
        [".kmz"] = AttachmentKinds.Documents,

        [".zip"] = AttachmentKinds.Archives, [".7z"] = AttachmentKinds.Archives, [".rar"] = AttachmentKinds.Archives,
        [".tar"] = AttachmentKinds.Archives, [".gz"] = AttachmentKinds.Archives, [".tgz"] = AttachmentKinds.Archives,
        [".bz2"] = AttachmentKinds.Archives, [".xz"] = AttachmentKinds.Archives, [".zst"] = AttachmentKinds.Archives,
    };

    /// <summary>Sorts a file by name, declared content type and content.</summary>
    public static AttachmentKinds Classify(string fileName, string? contentType, byte[] content)
    {
        var extension = Path.GetExtension(fileName);
        var type = (contentType ?? "").Split(';')[0].Trim();

        if (ExecutableExtensions.Contains(extension) || ExecutableContentTypes.Contains(type) || IsProgram(content))
            return AttachmentKinds.Executables;

        var kind = KindByExtension.GetValueOrDefault(extension);
        if (kind == AttachmentKinds.None)
            kind = type switch
            {
                _ when type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => AttachmentKinds.Images,
                _ when type.StartsWith("video/", StringComparison.OrdinalIgnoreCase) => AttachmentKinds.Video,
                _ when type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => AttachmentKinds.Audio,
                _ when type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) => AttachmentKinds.Documents,
                "application/pdf" => AttachmentKinds.Documents,
                "application/zip" or "application/x-zip-compressed" => AttachmentKinds.Archives,
                _ => AttachmentKinds.Other,
            };

        // A zip (by name or bytes, but not Office and e-book files, which are zips too) is only as safe as what's in it.
        var isZip = kind == AttachmentKinds.Archives || (kind == AttachmentKinds.Other && StartsWith(content, "PK\x03\x04"u8));
        return isZip && ZipContainsProgram(content) ? AttachmentKinds.Executables : kind;
    }

    /// <summary>Windows (PE), Linux (ELF) and macOS (Mach-O) programs, and scripts starting with #!.</summary>
    private static bool IsProgram(byte[] content)
    {
        if (StartsWith(content, [0x7F, (byte)'E', (byte)'L', (byte)'F']) || StartsWith(content, "#!"u8)) return true;

        if (content.Length >= 4)
        {
            var magic = BinaryPrimitives.ReadUInt32BigEndian(content);
            // Mach-O 32/64-bit in either byte order, and universal ("fat") binaries.
            if (magic is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xCAFEBABE) return true;
        }

        // "MZ", then the offset at 0x3C must point at a "PE\0\0" header (plain text can start with "MZ").
        if (content.Length >= 0x40 && StartsWith(content, "MZ"u8))
        {
            var offset = BinaryPrimitives.ReadInt32LittleEndian(content.AsSpan(0x3C));
            return offset > 0 && offset <= content.Length - 4 && content.AsSpan(offset, 4).SequenceEqual("PE\0\0"u8);
        }
        return false;
    }

    // Nested zips bigger than this aren't unpacked to look inside (keeps zip bombs cheap).
    private const long MaxNestedZipBytes = 50 * 1024 * 1024;

    private static bool ZipContainsProgram(byte[] content)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            return zip.Entries.Any(e =>
                ExecutableExtensions.Contains(Path.GetExtension(e.FullName)) ||
                (Path.GetExtension(e.FullName).Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
                 e.Length <= MaxNestedZipBytes && NestedZipContainsProgram(e)));
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            return false; // not a readable zip: judged by its name only
        }
    }

    // One level of zip-in-zip, the usual way to hide a program from a quick look.
    private static bool NestedZipContainsProgram(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(copy.ToArray()), ZipArchiveMode.Read);
            return zip.Entries.Any(e => ExecutableExtensions.Contains(Path.GetExtension(e.FullName)));
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool StartsWith(byte[] content, ReadOnlySpan<byte> prefix) => content.AsSpan().StartsWith(prefix);
}
