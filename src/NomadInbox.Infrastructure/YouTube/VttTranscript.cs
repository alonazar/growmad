using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace NomadInbox.Infrastructure.YouTube;

/// <summary>
/// Turns a WebVTT caption file into readable transcript text. YouTube auto-captions "roll": each cue repeats
/// the previous line and adds the next, with word-level timing tags. Those repeats and tags are removed, and a
/// [mm:ss] marker starts a new paragraph roughly every <see cref="ParagraphSeconds"/> seconds.
/// </summary>
public static partial class VttTranscript
{
    public const int ParagraphSeconds = 90;

    [GeneratedRegex(@"^(?<start>(\d+:)?\d{2}:\d{2}[.,]\d{3})\s+-->\s+")]
    private static partial Regex CueTiming();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static string ToText(string vtt)
    {
        var output = new StringBuilder();
        string? lastLine = null;
        var paragraphStart = double.NegativeInfinity;
        var cueStart = 0.0;
        var inCue = false;

        foreach (var raw in vtt.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            var timing = CueTiming().Match(line);
            if (timing.Success)
            {
                cueStart = ParseSeconds(timing.Groups["start"].Value);
                inCue = true;
                continue;
            }
            // Only a truly empty line ends a cue; YouTube starts cues with a line holding a single space.
            if (raw.Length == 0) { inCue = false; continue; }
            if (!inCue || line.Length == 0) continue; // header, NOTE and STYLE blocks; blank caption lines

            var text = Whitespace().Replace(WebUtility.HtmlDecode(Tags().Replace(line, "")), " ").Trim();
            if (text.Length == 0 || text == lastLine) continue;
            lastLine = text;

            if (cueStart - paragraphStart >= ParagraphSeconds)
            {
                if (output.Length > 0) output.Append("\n\n");
                output.Append('[').Append(Stamp(cueStart)).Append("] ");
                paragraphStart = cueStart;
            }
            else
            {
                output.Append(' ');
            }
            output.Append(text);
        }
        return output.ToString().Trim();
    }

    public static string Stamp(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private static double ParseSeconds(string stamp)
    {
        var parts = stamp.Replace(',', '.').Split(':');
        double seconds = 0;
        foreach (var part in parts) seconds = seconds * 60 + double.Parse(part, CultureInfo.InvariantCulture);
        return seconds;
    }
}
