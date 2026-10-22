using System.Text;
using NomadInbox.Core.Domain;

namespace NomadInbox.Infrastructure.Ai;

public enum SummaryKind { Web, Video, Channel }

/// <summary>What the summary is about, for the prompt. <paramref name="Details"/> is extra metadata (channel, chapters, …).</summary>
public sealed record SummaryContext(
    string? Title, string Url, string? Category, IReadOnlyList<string> Tags, string? EmailTitle,
    SummaryKind Kind = SummaryKind.Web, string? Details = null);

public sealed record SummaryResult(string Markdown, int Chunks, bool Truncated);

/// <summary>
/// Summarizes extracted page text with a local model. Text that doesn't fit the context window is split into
/// chunks at paragraph boundaries; each chunk is condensed to notes, then the notes are summarized together.
/// </summary>
public sealed class Summarizer(IOllamaApi model)
{
    public const string DefaultPrompt = """
        You summarize web pages for an offline knowledge archive (Project NOMAD) that will be read without internet access.
        Write in English, in Markdown, with no preamble and no closing remarks.
        Start with one sentence in bold saying what the page is and why it is useful.
        Then give 3 to 8 bullet points with the key facts: steps, quantities, measurements, materials, names, warnings and recommendations.
        Keep concrete details exactly as written. Never add facts that are not in the text.
        If the text is mostly navigation, ads, a login wall or an error page, say so in one sentence instead.
        """;

    public const string VideoPrompt = """
        You summarize YouTube videos from their transcript for an offline knowledge archive (Project NOMAD) that will be read without internet access.
        Write in English, in Markdown, with no preamble and no closing remarks.
        Start with one sentence in bold saying what the video shows or teaches and who it is useful for.
        Then give 4 to 10 bullet points with the key information: steps, techniques, quantities, materials, names, warnings and recommendations.
        If chapters are listed, finish with a "Chapters" list giving each chapter's time and a few words on what it covers.
        Auto-generated transcripts contain recognition errors: interpret them sensibly and never quote obvious mistakes.
        Never add facts that are not in the transcript or description. If there is only a description, summarize that and say so.
        """;

    public const string ChannelPrompt = """
        You describe YouTube channels and playlists for an offline knowledge archive (Project NOMAD).
        Write in English, in Markdown, with no preamble and no closing remarks.
        Start with one sentence in bold saying what the channel or playlist covers and who it is for.
        Then give 3 to 6 bullet points: main topics, style or format, and which of the listed videos look most useful and why.
        Use only the description and video titles given. Never invent videos or facts.
        """;

    private const string ChunkPrompt = """
        You are condensing one part of a long text (a web page or a video transcript) so it can be summarized later.
        Write concise Markdown bullet points with every concrete fact in this part: steps, quantities, measurements,
        materials, names, warnings. Do not add anything that is not in the text. No preamble.
        """;

    private const int MaxChunks = 8;
    private const int ReservedTokens = 1500; // prompt, metadata and the answer itself

    public async Task<SummaryResult> SummarizeAsync(InboxSettings settings, SummaryContext context, string text, CancellationToken ct)
    {
        var system = context.Kind switch
        {
            SummaryKind.Video => VideoPrompt,
            SummaryKind.Channel => ChannelPrompt,
            _ => string.IsNullOrWhiteSpace(settings.SummaryPrompt) ? DefaultPrompt : settings.SummaryPrompt,
        };
        // Metadata (description, chapters, …) goes into every request, so it takes room from the text.
        var chunkChars = Math.Max(2000, CharsPerChunk(settings.OllamaContextTokens) - (context.Details?.Length ?? 0));
        var chunks = Split(text, chunkChars);
        var truncated = chunks.Count > MaxChunks;
        if (truncated) chunks = chunks.Take(MaxChunks).ToList();

        string body;
        if (chunks.Count <= 1)
        {
            body = text;
        }
        else
        {
            var notes = new StringBuilder();
            for (var i = 0; i < chunks.Count; i++)
            {
                var part = await model.ChatAsync(settings.OllamaUrl, settings.OllamaModel, ChunkPrompt,
                    $"{Header(context)}\nPart {i + 1} of {chunks.Count}:\n<<<\n{chunks[i]}\n>>>", settings.OllamaContextTokens, ct);
                notes.AppendLine($"Notes from part {i + 1}:").AppendLine(part).AppendLine();
            }
            body = notes.ToString();
        }

        var label = context.Kind switch
        {
            SummaryKind.Video => chunks.Count > 1 ? "Notes taken from the transcript" : "Transcript",
            SummaryKind.Channel => "Recent videos",
            _ => chunks.Count > 1 ? "Notes taken from the page" : "Page text",
        };
        var summary = await model.ChatAsync(settings.OllamaUrl, settings.OllamaModel, system,
            $"{Header(context)}\n{label}:\n<<<\n{body}\n>>>",
            settings.OllamaContextTokens, ct);

        if (string.IsNullOrWhiteSpace(summary))
            throw new OllamaException("The model returned an empty summary.");
        if (truncated)
            summary += $"\n\n*Summary covers the first {MaxChunks} parts of a very long page.*";
        return new SummaryResult(summary.Trim(), Math.Max(1, chunks.Count), truncated);
    }

    /// <summary>Roughly 3 characters per token, which is conservative for English prose.</summary>
    public static int CharsPerChunk(int contextTokens) => Math.Max(2000, (contextTokens - ReservedTokens) * 3);

    /// <summary>Splits on blank lines (then lines, then hard cuts) so chunks stay under <paramref name="maxChars"/>.</summary>
    public static List<string> Split(string text, int maxChars)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var paragraph in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var piece in Pieces(paragraph, maxChars))
            {
                if (current.Length > 0 && current.Length + piece.Length + 2 > maxChars)
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                }
                if (current.Length > 0) current.Append("\n\n");
                current.Append(piece);
            }
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;

        static IEnumerable<string> Pieces(string paragraph, int max)
        {
            if (paragraph.Length <= max) { yield return paragraph; yield break; }
            var line = new StringBuilder();
            foreach (var l in paragraph.Split('\n'))
            {
                for (var start = 0; start < l.Length; start += max)
                {
                    var part = l.Substring(start, Math.Min(max, l.Length - start));
                    if (line.Length > 0 && line.Length + part.Length + 1 > max) { yield return line.ToString(); line.Clear(); }
                    if (line.Length > 0) line.Append('\n');
                    line.Append(part);
                }
            }
            if (line.Length > 0) yield return line.ToString();
        }
    }

    private static string Header(SummaryContext c)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Title: {c.Title ?? "(unknown)"}");
        sb.AppendLine($"URL: {c.Url}");
        if (c.Category is not null || c.Tags.Count > 0 || !string.IsNullOrWhiteSpace(c.EmailTitle))
        {
            var filed = string.Join(", ", new[] { c.Category, c.Tags.Count > 0 ? string.Join(' ', c.Tags.Select(t => "#" + t)) : null, c.EmailTitle }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            sb.AppendLine($"Filed by the sender under: {filed}");
        }
        if (!string.IsNullOrWhiteSpace(c.Details)) sb.AppendLine(c.Details.Trim());
        return sb.ToString();
    }
}
