using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using NomadInbox.Infrastructure.Files;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace NomadInbox.Tests;

public sealed class AttachmentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nomad-inbox-tests", Guid.NewGuid().ToString("N"));
    private readonly AttachmentStore _store;
    private static readonly DateTimeOffset Received = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    public AttachmentStoreTests() => _store = new AttachmentStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Files_under_category_and_month()
    {
        var content = "hello"u8.ToArray();

        var path = await _store.SaveAsync("Survival/Water", Received, "guide.pdf", content, AttachmentStore.ComputeSha256(content), default);

        Assert.Equal("Survival/Water/2026-10/guide.pdf", path);
        Assert.Equal(content, await File.ReadAllBytesAsync(_store.ResolveFullPath(path)!));
    }

    [Fact]
    public async Task Same_name_same_content_is_reused_different_content_gets_suffix()
    {
        byte[] a = "a"u8.ToArray(), b = "b"u8.ToArray();

        var first = await _store.SaveAsync(null, Received, "notes.txt", a, AttachmentStore.ComputeSha256(a), default);
        var again = await _store.SaveAsync(null, Received, "notes.txt", a, AttachmentStore.ComputeSha256(a), default);
        var other = await _store.SaveAsync(null, Received, "notes.txt", b, AttachmentStore.ComputeSha256(b), default);

        Assert.Equal("Uncategorized/2026-10/notes.txt", first);
        Assert.Equal(first, again);
        Assert.Equal("Uncategorized/2026-10/notes (2).txt", other);
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\temp\\evil.exe", "evil.exe")]
    [InlineData("a:b*c?.txt", "a_b_c_.txt")]
    [InlineData("   ", "attachment")]
    public void Sanitizes_file_names(string input, string expected)
    {
        Assert.Equal(expected, AttachmentStore.SanitizeFileName(input));
    }

    [Fact]
    public async Task Category_cannot_escape_the_root()
    {
        var content = "x"u8.ToArray();

        var path = await _store.SaveAsync("../../outside", Received, "f.txt", content, AttachmentStore.ComputeSha256(content), default);

        Assert.Equal("outside/2026-10/f.txt", path);
        Assert.Null(_store.ResolveFullPath("../escape.txt"));
    }
}

public class TextExtractorTests
{
    private readonly TextExtractor _extractor = new();

    [Fact]
    public void Reads_plain_text_and_html()
    {
        Assert.Equal("line one\nline two", _extractor.Extract("a.md", "text/markdown", Encoding.UTF8.GetBytes("line one\nline two")).Text);
        Assert.Equal("Hi there", _extractor.Extract("a.html", "text/html", "<p>Hi <b>there</b></p>"u8.ToArray()).Text);
    }

    [Fact]
    public void Reads_pdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Boil water for one minute", 12, new PdfPoint(25, 700), font);

        var result = _extractor.Extract("water.pdf", "application/pdf", builder.Build());

        Assert.Contains("Boil water for one minute", result.Text);
    }

    [Fact]
    public void Reads_docx()
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            doc.AddMainDocumentPart().Document = new Document(new Body(
                new Paragraph(new Run(new Text("First paragraph"))),
                new Paragraph(new Run(new Text("Second paragraph")))));
        }

        var result = _extractor.Extract("notes.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", stream.ToArray());

        Assert.Equal("First paragraph\nSecond paragraph", result.Text);
    }

    [Fact]
    public void Unsupported_types_return_nothing_and_broken_files_return_a_note()
    {
        Assert.Equal(new TextExtraction(null, null), _extractor.Extract("photo.jpg", "image/jpeg", [1, 2, 3]));

        var broken = _extractor.Extract("broken.pdf", "application/pdf", "not a pdf"u8.ToArray());
        Assert.Null(broken.Text);
        Assert.StartsWith("Text extraction failed", broken.Note);
    }
}
