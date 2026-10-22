using System.Net;
using System.Text.RegularExpressions;

namespace NomadInbox.Core.Content;

/// <summary>Good-enough HTML to plain text for email bodies (not a general HTML parser).</summary>
public static partial class HtmlText
{
    [GeneratedRegex(@"<(script|style|head|title)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Invisible();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    // No "/li": each <li> already starts a new line.
    [GeneratedRegex(@"<\s*(br|/p|/div|/ul|/ol|/tr|/h[1-6]|/blockquote|/table)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"<\s*li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItems();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n\s*\n\s*\n+")]
    private static partial Regex BlankLines();

    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var s = Invisible().Replace(html, "");
        s = Comments().Replace(s, "");
        s = LineBreaks().Replace(s, "\n");
        s = ListItems().Replace(s, "\n• ");
        s = Tags().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        s = Spaces().Replace(s, " ");
        s = string.Join('\n', s.Split('\n').Select(l => l.Trim()));
        s = BlankLines().Replace(s, "\n\n");
        return s.Trim();
    }
}
