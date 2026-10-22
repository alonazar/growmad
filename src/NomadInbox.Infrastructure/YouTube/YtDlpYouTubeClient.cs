using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace NomadInbox.Infrastructure.YouTube;

/// <summary>YouTube via yt-dlp (videos, channels, playlists) and the public RSS feed (followed channels).</summary>
public sealed class YtDlpYouTubeClient(YtDlp ytDlp, HttpClient http, ILogger<YtDlpYouTubeClient> logger) : IYouTubeClient
{
    private static readonly TimeSpan InfoTimeout = TimeSpan.FromMinutes(2);

    public async Task<VideoDetails> GetVideoAsync(string url, IReadOnlyList<string> preferredLanguages, CancellationToken ct)
    {
        var temp = Directory.CreateTempSubdirectory("nomad-yt-");
        try
        {
            var info = await ytDlp.RunAsync(["--dump-single-json", "--skip-download", "--no-playlist", url], temp.FullName, InfoTimeout, ct);
            if (info.ExitCode != 0) throw new YouTubeException(YtDlp.ErrorMessage(info));

            using var json = JsonDocument.Parse(info.Output);
            var video = ParseVideo(json.RootElement);
            var track = ChooseTrack(Keys(json.RootElement, "subtitles"), Keys(json.RootElement, "automatic_captions"),
                Str(json.RootElement, "language"), preferredLanguages);

            if (track is null)
                return video with { Note = "This video has no captions, so it was summarized from its description." };

            string? lastError = null;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var result = await ytDlp.RunAsync(
                [
                    "--skip-download", "--no-playlist", track.Value.IsAuto ? "--write-auto-subs" : "--write-subs",
                    "--sub-langs", track.Value.Language, "--sub-format", "vtt", "-o", "%(id)s.%(ext)s", url,
                ], temp.FullName, InfoTimeout, ct);

                var file = temp.EnumerateFiles("*.vtt").FirstOrDefault();
                if (file is not null)
                {
                    var transcript = VttTranscript.ToText(await File.ReadAllTextAsync(file.FullName, ct));
                    var source = $"{(track.Value.IsAuto ? "Auto-generated captions" : "Subtitles")} ({track.Value.Language})";
                    return video with { Transcript = transcript, TranscriptSource = source };
                }

                lastError = YtDlp.ErrorMessage(result);
                logger.LogWarning("Caption download for {Url} failed (attempt {Attempt}): {Error}", url, attempt, lastError);
                if (!lastError.Contains("429")) break;
                await Task.Delay(TimeSpan.FromSeconds(8), ct); // YouTube rate limit; one polite retry
            }
            return video with { Note = $"Transcript unavailable ({lastError}); summarized from the description." };
        }
        finally
        {
            try { temp.Delete(recursive: true); } catch { }
        }
    }

    public async Task<ChannelDetails> GetChannelAsync(string url, int recentVideos, CancellationToken ct)
    {
        var isPlaylist = url.Contains("list=", StringComparison.Ordinal);
        // A channel URL lists its tabs; the uploads ("videos") tab is the useful one.
        var listUrl = isPlaylist || url.EndsWith("/videos", StringComparison.OrdinalIgnoreCase) ? url : url.TrimEnd('/') + "/videos";

        var result = await ytDlp.RunAsync(
            ["--flat-playlist", "--dump-single-json", "--playlist-end", recentVideos.ToString(CultureInfo.InvariantCulture), listUrl],
            null, InfoTimeout, ct);
        if (result.ExitCode != 0) throw new YouTubeException(YtDlp.ErrorMessage(result));

        using var json = JsonDocument.Parse(result.Output);
        return ParseChannel(json.RootElement, isPlaylist);
    }

    public async Task<IReadOnlyList<FeedEntry>> GetFeedAsync(string channelId, CancellationToken ct)
    {
        var url = $"https://www.youtube.com/feeds/videos.xml?channel_id={Uri.EscapeDataString(channelId)}";
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            throw new YouTubeException($"The channel feed answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        return ParseFeed(await response.Content.ReadAsStringAsync(ct));
    }

    // ---- parsing (internal for tests) ----

    internal static VideoDetails ParseVideo(JsonElement j)
    {
        DateTimeOffset? published = null;
        if (j.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number)
            published = DateTimeOffset.FromUnixTimeSeconds(ts.GetInt64());
        else if (Str(j, "upload_date") is { Length: 8 } d
                 && DateTime.TryParseExact(d, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            published = new DateTimeOffset(date, TimeSpan.Zero);

        var chapters = j.TryGetProperty("chapters", out var ch) && ch.ValueKind == JsonValueKind.Array
            ? ch.EnumerateArray().Select(c => new VideoChapter(Num(c, "start_time") ?? 0, Str(c, "title") ?? "")).ToList()
            : [];

        return new VideoDetails(
            Str(j, "id") ?? "",
            Str(j, "title") ?? "(untitled video)",
            Str(j, "channel") ?? Str(j, "uploader"),
            Str(j, "channel_id"),
            Str(j, "channel_url") ?? Str(j, "uploader_url"),
            published,
            Num(j, "duration") is { } dur ? (int)dur : null,
            Str(j, "description"),
            chapters,
            null, null, null);
    }

    internal static ChannelDetails ParseChannel(JsonElement j, bool isPlaylist)
    {
        var videos = j.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array
            ? entries.EnumerateArray()
                .Where(e => Str(e, "id") is not null)
                .Select(e => new RecentVideo(Str(e, "id")!, Str(e, "title") ?? "(untitled)",
                    Num(e, "duration") is { } d ? (int)d : null, Num(e, "view_count") is { } v ? (long)v : null))
                .ToList()
            : [];

        var title = isPlaylist ? Str(j, "title") : Str(j, "channel") ?? Str(j, "uploader") ?? Str(j, "title");
        return new ChannelDetails(
            Str(j, "id") ?? "",
            title ?? "(unknown)",
            Str(j, "channel_id"),
            Str(j, "channel_url") ?? Str(j, "webpage_url"),
            Str(j, "uploader") ?? Str(j, "channel"),
            Str(j, "description"),
            Num(j, "channel_follower_count") is { } f ? (long)f : null,
            isPlaylist,
            videos);
    }

    internal static IReadOnlyList<FeedEntry> ParseFeed(string xml)
    {
        XNamespace atom = "http://www.w3.org/2005/Atom", yt = "http://www.youtube.com/xml/schemas/2015";
        return XDocument.Parse(xml).Root!.Elements(atom + "entry")
            .Select(e => new FeedEntry(
                (string?)e.Element(yt + "videoId") ?? "",
                (string?)e.Element(atom + "title") ?? "(untitled)",
                DateTimeOffset.TryParse((string?)e.Element(atom + "published"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var p)
                    ? p : DateTimeOffset.MinValue))
            .Where(e => e.VideoId.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Picks the caption track to download, best first: uploaded subtitles in a preferred language; the original
    /// auto-generated track ("xx-orig", preferred languages first); then any uploaded subtitles; then a translated
    /// auto track in a preferred language. Translated auto tracks come last because YouTube rate-limits them (HTTP 429).
    /// </summary>
    internal static (string Language, bool IsAuto)? ChooseTrack(
        IReadOnlyCollection<string> manual, IReadOnlyCollection<string> auto, string? videoLanguage, IReadOnlyList<string> preferred)
    {
        static string? Match(IEnumerable<string> keys, string lang) =>
            keys.FirstOrDefault(k => k.Equals(lang, StringComparison.OrdinalIgnoreCase))
            ?? keys.FirstOrDefault(k => k.StartsWith(lang + "-", StringComparison.OrdinalIgnoreCase) && !k.EndsWith("-orig"));

        var realManual = manual.Where(k => k != "live_chat").ToList();
        foreach (var lang in preferred)
            if (Match(realManual, lang) is { } m) return (m, false);

        foreach (var lang in preferred.Append(videoLanguage).OfType<string>())
            if (auto.FirstOrDefault(k => k.Equals(lang + "-orig", StringComparison.OrdinalIgnoreCase)) is { } o) return (o, true);
        if (auto.FirstOrDefault(k => k.EndsWith("-orig", StringComparison.OrdinalIgnoreCase)) is { } anyOrig) return (anyOrig, true);

        if (realManual.Count > 0) return (realManual[0], false);
        foreach (var lang in preferred)
            if (auto.FirstOrDefault(k => k.Equals(lang, StringComparison.OrdinalIgnoreCase)) is { } t) return (t, true);
        return null;
    }

    private static IReadOnlyCollection<string> Keys(JsonElement j, string property) =>
        j.TryGetProperty(property, out var obj) && obj.ValueKind == JsonValueKind.Object
            ? obj.EnumerateObject().Select(p => p.Name).ToList()
            : [];

    private static string? Str(JsonElement j, string property) =>
        j.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static double? Num(JsonElement j, string property) =>
        j.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
