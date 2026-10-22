using System.Text;
using DocumentFormat.OpenXml.Packaging;
using MimeKit;
using NomadInbox.Core.Content;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Wordprocessing = DocumentFormat.OpenXml.Wordprocessing;
using Drawing = DocumentFormat.OpenXml.Drawing;

namespace NomadInbox.Infrastructure.Files;

public sealed record TextExtraction(string? Text, string? Note);

/// <summary>Pulls plain text out of common attachment types for search and (later) summaries.</summary>
public sealed class TextExtractor
{
    public const int MaxChars = 200_000;
    private const int MaxPdfPages = 500;

    private static readonly HashSet<string> PlainTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".log", ".yaml", ".yml", ".ini", ".srt", ".vtt",
    };

    public TextExtraction Extract(string fileName, string contentType, byte[] content)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            string? text = extension switch
            {
                ".pdf" => FromPdf(content),
                ".docx" => FromDocx(content),
                ".pptx" => FromPptx(content),
                ".html" or ".htm" => HtmlText.ToPlainText(Decode(content)),
                ".eml" => FromEml(content),
                _ when PlainTextExtensions.Contains(extension) => Decode(content),
                _ when contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) => HtmlText.ToPlainText(Decode(content)),
                _ when contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) => Decode(content),
                _ when contentType == "application/pdf" => FromPdf(content),
                _ => null,
            };

            if (text is null) return new TextExtraction(null, null); // unsupported type: nothing to say
            text = text.Trim();
            if (text.Length == 0)
                return new TextExtraction(null, extension == ".pdf" ? "No text layer (scanned PDF?)." : "No text found.");
            if (text.Length > MaxChars)
                return new TextExtraction(text[..MaxChars], $"Text truncated to {MaxChars:N0} characters.");
            return new TextExtraction(text, null);
        }
        catch (Exception ex)
        {
            return new TextExtraction(null, $"Text extraction failed: {ex.Message}");
        }
    }

    private static string FromPdf(byte[] content)
    {
        using var pdf = PdfDocument.Open(content);
        var sb = new StringBuilder();
        foreach (var page in pdf.GetPages().Take(MaxPdfPages))
        {
            sb.AppendLine(ContentOrderTextExtractor.GetText(page));
            if (sb.Length > MaxChars) break;
        }
        return sb.ToString();
    }

    private static string FromDocx(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var doc = WordprocessingDocument.Open(stream, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        return body is null ? "" : string.Join('\n', body.Descendants<Wordprocessing.Paragraph>().Select(p => p.InnerText));
    }

    private static string FromPptx(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var doc = PresentationDocument.Open(stream, false);
        var slides = doc.PresentationPart?.SlideParts ?? [];
        return string.Join("\n\n", slides.Select(s =>
            string.Join('\n', s.Slide?.Descendants<Drawing.Paragraph>().Select(p => p.InnerText) ?? [])));
    }

    private static string FromEml(byte[] content)
    {
        using var stream = new MemoryStream(content);
        var message = MimeMessage.Load(stream);
        var body = message.TextBody ?? HtmlText.ToPlainText(message.HtmlBody);
        return $"Subject: {message.Subject}\nFrom: {message.From}\nDate: {message.Date:u}\n\n{body}";
    }

    private static string Decode(byte[] content)
    {
        // UTF-8 (with or without BOM) covers almost everything sent by mail today; BOMs for UTF-16 are honoured.
        using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
