using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using NomadInbox.Core.Domain;

namespace NomadInbox.Core.Content;

public sealed record ExtractedLink(string Url, LinkKind Kind);

/// <summary>
/// Finds http(s) links in an email's text and HTML bodies, normalizes them and classifies YouTube links.
/// Normalizing means: tracking parameters removed, Google redirect links unwrapped, and every form of
/// YouTube URL (youtu.be, shorts, m.youtube.com, …) rewritten to one canonical URL, so duplicates collapse.
/// </summary>
public static partial class LinkExtractor
{
    [GeneratedRegex(@"href\s*=\s*(?:""(?<u>[^""]*)""|'(?<u>[^']*)'|(?<u>[^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex Href();

    [GeneratedRegex(@"\bhttps?://[^\s<>""'\[\]{}|\\^`]+", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrl();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoId();

    private static readonly HashSet<string> TrackingParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "dclid", "msclkid", "mc_cid", "mc_eid", "igshid", "si", "feature", "ref_src", "_hsenc", "_hsmi",
    };

    public static IReadOnlyList<ExtractedLink> Extract(string? text, string? html, int max = 100)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(text))
            candidates.AddRange(BareUrl().Matches(text).Select(m => m.Value));
        if (!string.IsNullOrEmpty(html))
        {
            candidates.AddRange(Href().Matches(html).Select(m => WebUtility.HtmlDecode(m.Groups["u"].Value)));
            candidates.AddRange(BareUrl().Matches(HtmlText.ToPlainText(html)).Select(m => m.Value));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<ExtractedLink>();
        foreach (var candidate in candidates)
        {
            if (Normalize(candidate) is not { } link || !seen.Add(link.Url)) continue;
            links.Add(link);
            if (links.Count >= max) break;
        }
        return links;
    }

    public static ExtractedLink? Normalize(string? raw)
    {
        var s = TrimTrailingPunctuation(raw?.Trim() ?? "");
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;

        var host = uri.Host.ToLowerInvariant();
        var query = HttpUtility.ParseQueryString(uri.Query);

        // Google's redirect wrapper: https://www.google.com/url?q=<real url>
        if (host is "google.com" or "www.google.com" && uri.AbsolutePath == "/url")
            return Normalize(query["q"] ?? query["url"]);

        return ClassifyYouTube(uri, host, query) ?? new ExtractedLink(CleanWebUrl(uri, host, query), LinkKind.Web);
    }

    private static ExtractedLink? ClassifyYouTube(Uri uri, string host, System.Collections.Specialized.NameValueCollection query)
    {
        const string Base = "https://www.youtube.com";
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (host == "youtu.be")
            return segments.Length > 0 && VideoId().IsMatch(segments[0]) ? Video(segments[0]) : null;

        if (host is not ("youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com" or "www.youtube-nocookie.com"))
            return null;
        if (segments.Length == 0) return null;

        var first = segments[0];
        switch (first.ToLowerInvariant())
        {
            case "watch" when query["v"] is { } v && VideoId().IsMatch(v):
                return Video(v);
            case "watch" or "playlist" when query["list"] is { Length: > 0 } list:
                return new ExtractedLink($"{Base}/playlist?list={Uri.EscapeDataString(list)}", LinkKind.YouTubePlaylist);
            case "shorts" or "live" or "embed" or "v" when segments.Length > 1 && VideoId().IsMatch(segments[1]):
                return Video(segments[1]);
            case "channel" when segments.Length > 1:
                return new ExtractedLink($"{Base}/channel/{segments[1]}", LinkKind.YouTubeChannel);
            case "c" or "user" when segments.Length > 1:
                return new ExtractedLink($"{Base}/{first.ToLowerInvariant()}/{segments[1]}", LinkKind.YouTubeChannel);
        }

        if (first.StartsWith('@') && first.Length > 1)
            return new ExtractedLink($"{Base}/{first}", LinkKind.YouTubeChannel);

        return null;

        static ExtractedLink Video(string id) => new($"{Base}/watch?v={id}", LinkKind.YouTubeVideo);
    }

    private static string CleanWebUrl(Uri uri, string host, System.Collections.Specialized.NameValueCollection query)
    {
        var kept = query.AllKeys
            .Where(k => k is not null && !k.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) && !TrackingParameters.Contains(k))
            .SelectMany(k => (query.GetValues(k) ?? []).Select(v => $"{Uri.EscapeDataString(k!)}={Uri.EscapeDataString(v)}"))
            .ToList();
        // Keys without "=" (e.g. "?print") come back under a null key.
        kept.AddRange(query.GetValues(null)?.Select(Uri.EscapeDataString) ?? []);

        var builder = new UriBuilder(uri)
        {
            Host = host,
            Query = kept.Count == 0 ? "" : string.Join('&', kept),
        };
        if (uri.IsDefaultPort) builder.Port = -1;
        return builder.Uri.AbsoluteUri;
    }

    private static string TrimTrailingPunctuation(string s)
    {
        while (s.Length > 0)
        {
            var last = s[^1];
            if (last is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"' or '>' or ']' or '}')
                s = s[..^1];
            else if (last == ')' && s.Count(c => c == '(') < s.Count(c => c == ')'))
                s = s[..^1]; // "(see https://x.com/a)" but keep "https://en.wikipedia.org/wiki/Foo_(bar)"
            else
                break;
        }
        return s;
    }
}
