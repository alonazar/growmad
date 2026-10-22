using System.Text;
using System.Text.RegularExpressions;

namespace NomadInbox.Core.Subjects;

public sealed record ParsedSubject(
    string? Category,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> Fields,
    string Title);

/// <summary>
/// Parses subjects like <c>NOMAD: Survival/Water #filtration #diy priority=high Ceramic filter builds</c>.
/// <list type="bullet">
/// <item>Leading reply/forward markers (Re:, Fwd:, Fw:) are skipped before the prefix check.</item>
/// <item>The first token is the category if it contains '/' (use a trailing slash for one level: <c>Survival/</c>).
///       <c>category=...</c> or <c>cat=...</c> also sets it.</item>
/// <item><c>#tag</c> adds a lower-cased tag.</item>
/// <item><c>key=value</c> or <c>key="two words"</c> adds a field.</item>
/// <item>Everything else, in order, is the title.</item>
/// </list>
/// </summary>
public static partial class SubjectParser
{
    [GeneratedRegex(@"^\s*((re|fwd?|aw|wg)\s*:\s*)+", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyMarkers();

    [GeneratedRegex(@"^(?<key>[A-Za-z][\w-]*)=(?<value>.*)$")]
    private static partial Regex FieldToken();

    public static bool Matches(string? subject, string prefix) => TryStripPrefix(subject, prefix, out _);

    public static ParsedSubject? Parse(string? subject, string prefix)
    {
        if (!TryStripPrefix(subject, prefix, out var rest)) return null;

        string? category = null;
        var tags = new List<string>();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var title = new List<string>();

        var tokens = Tokenize(rest);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (i == 0 && token.Contains('/') && !token.Contains("://"))
            {
                category = NormalizeCategory(token);
                continue;
            }

            if (token.Length > 1 && token[0] == '#')
            {
                var tag = token[1..].ToLowerInvariant();
                if (!tags.Contains(tag)) tags.Add(tag);
                continue;
            }

            var field = FieldToken().Match(token);
            if (field.Success)
            {
                var key = field.Groups["key"].Value.ToLowerInvariant();
                var value = field.Groups["value"].Value;
                if (key is "category" or "cat")
                    category = NormalizeCategory(value);
                else
                    fields[key] = value;
                continue;
            }

            title.Add(token);
        }

        return new ParsedSubject(category, tags, fields, string.Join(' ', title));
    }

    private static bool TryStripPrefix(string? subject, string prefix, out string rest)
    {
        rest = "";
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(prefix)) return false;

        var s = ReplyMarkers().Replace(subject, "").TrimStart();
        var p = prefix.Trim();
        if (!s.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return false;

        rest = s[p.Length..].Trim();
        return true;
    }

    private static string? NormalizeCategory(string raw)
    {
        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : string.Join('/', parts);
    }

    /// <summary>Splits on whitespace, keeping double-quoted runs together and dropping the quotes.</summary>
    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in input)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
