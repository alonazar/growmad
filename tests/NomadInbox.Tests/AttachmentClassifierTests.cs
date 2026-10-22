using System.IO.Compression;
using System.Text;
using NomadInbox.Core.Content;

namespace NomadInbox.Tests;

public class AttachmentClassifierTests
{
    [Theory]
    [InlineData("photo.JPG", "image/jpeg", AttachmentKinds.Images)]
    [InlineData("scan", "image/png", AttachmentKinds.Images)]
    [InlineData("clip.mp4", "video/mp4", AttachmentKinds.Video)]
    [InlineData("voice.m4a", "audio/mp4", AttachmentKinds.Audio)]
    [InlineData("notes.txt", "text/plain", AttachmentKinds.Documents)]
    [InlineData("manual.pdf", "application/pdf", AttachmentKinds.Documents)]
    [InlineData("route.gpx", "application/gpx+xml", AttachmentKinds.Documents)]
    [InlineData("backup.7z", "application/x-7z-compressed", AttachmentKinds.Archives)]
    [InlineData("data.bin", "application/octet-stream", AttachmentKinds.Other)]
    [InlineData("setup.exe", "application/octet-stream", AttachmentKinds.Executables)]
    [InlineData("SETUP.MSI", "application/octet-stream", AttachmentKinds.Executables)]
    [InlineData("install.sh", "text/plain", AttachmentKinds.Executables)]
    [InlineData("tool.ps1", "text/plain", AttachmentKinds.Executables)]
    [InlineData("app.apk", "application/octet-stream", AttachmentKinds.Executables)]
    [InlineData("budget.xlsm", "application/octet-stream", AttachmentKinds.Executables)]
    [InlineData("disk.iso", "application/octet-stream", AttachmentKinds.Executables)]
    [InlineData("download", "application/x-msdownload", AttachmentKinds.Executables)]
    [InlineData("download", "application/x-sh; charset=utf-8", AttachmentKinds.Executables)]
    public void Sorts_by_name_and_content_type(string name, string contentType, AttachmentKinds expected)
    {
        Assert.Equal(expected, AttachmentClassifier.Classify(name, contentType, "plain bytes"u8.ToArray()));
    }

    [Fact]
    public void Windows_program_renamed_to_an_image_is_still_a_program()
    {
        Assert.Equal(AttachmentKinds.Executables, AttachmentClassifier.Classify("holiday.jpg", "image/jpeg", WindowsProgram()));
    }

    [Theory]
    [InlineData(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1 })] // Linux
    [InlineData(new byte[] { 0xCF, 0xFA, 0xED, 0xFE, 7, 0, 0, 1 })] // macOS, 64-bit
    [InlineData(new byte[] { (byte)'#', (byte)'!', (byte)'/', (byte)'b', (byte)'i', (byte)'n', (byte)'/', (byte)'s', (byte)'h' })]
    public void Programs_are_recognized_by_their_bytes(byte[] content)
    {
        Assert.Equal(AttachmentKinds.Executables, AttachmentClassifier.Classify("readme.txt", "text/plain", content));
    }

    [Fact]
    public void Text_that_starts_with_MZ_is_not_a_program()
    {
        var text = Encoding.ASCII.GetBytes("MZ is the start of this note, which is long enough to hold a fake header offset field.");
        Assert.Equal(AttachmentKinds.Documents, AttachmentClassifier.Classify("note.txt", "text/plain", text));
    }

    [Fact]
    public void Zip_of_documents_is_an_archive()
    {
        var zip = Zip(("maps/area.pdf", "pdf"u8.ToArray()), ("readme.txt", "hi"u8.ToArray()));
        Assert.Equal(AttachmentKinds.Archives, AttachmentClassifier.Classify("maps.zip", "application/zip", zip));
    }

    [Theory]
    [InlineData("tools/setup.exe")]
    [InlineData("run.BAT")]
    public void Zip_with_a_program_inside_is_a_program(string entry)
    {
        var zip = Zip(("readme.txt", "hi"u8.ToArray()), (entry, WindowsProgram()));
        Assert.Equal(AttachmentKinds.Executables, AttachmentClassifier.Classify("photos.zip", "application/zip", zip));
    }

    [Fact]
    public void Program_inside_a_zip_inside_a_zip_is_found()
    {
        var inner = Zip(("payload.exe", WindowsProgram()));
        var outer = Zip(("inner.zip", inner));
        Assert.Equal(AttachmentKinds.Executables, AttachmentClassifier.Classify("files.zip", "application/zip", outer));
    }

    [Fact]
    public void Unnamed_zip_is_recognized_by_its_bytes()
    {
        var zip = Zip(("setup.exe", WindowsProgram()));
        Assert.Equal(AttachmentKinds.Executables, AttachmentClassifier.Classify("attachment", "application/octet-stream", zip));
    }

    [Fact]
    public void Office_files_are_documents_even_though_they_are_zips()
    {
        var docx = Zip(("word/document.xml", "<w:document/>"u8.ToArray()));
        Assert.Equal(AttachmentKinds.Documents, AttachmentClassifier.Classify("report.docx", "application/octet-stream", docx));
    }

    [Fact]
    public void Broken_zip_is_judged_by_its_name()
    {
        Assert.Equal(AttachmentKinds.Archives, AttachmentClassifier.Classify("broken.zip", "application/zip", "PK\x03\x04 not really"u8.ToArray()));
    }

    /// <summary>The smallest thing that looks like a Windows program: "MZ", and at 0x3C an offset to "PE\0\0".</summary>
    internal static byte[] WindowsProgram()
    {
        var bytes = new byte[0x80];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.TryWriteBytes(bytes.AsSpan(0x3C), 0x40);
        "PE\0\0"u8.CopyTo(bytes.AsSpan(0x40));
        return bytes;
    }

    internal static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(content);
            }
        return buffer.ToArray();
    }
}
