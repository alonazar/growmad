using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Ai;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.Ingest;
using NomadInbox.Infrastructure.Web;
using NomadInbox.Infrastructure.YouTube;

namespace NomadInbox.Tests;

public class VttTranscriptTests
{
    // Shape of a real YouTube auto-caption file: rolling cues that repeat the previous line, with word timing tags,
    // and lines holding a single space (built with string.Join because editors strip trailing whitespace).
    private static readonly string AutoCaptions = string.Join("\n",
        "WEBVTT",
        "Kind: captions",
        "Language: en",
        "",
        "00:00:00.000 --> 00:00:01.990 align:start position:0%",
        " ",
        "Single<00:00:00.680><c> circle,</c><00:00:01.120><c> you're</c><00:00:01.240><c> going</c><00:00:01.360><c> to</c><00:00:01.440><c> come</c><00:00:01.800><c> up</c>",
        "",
        "00:00:01.990 --> 00:00:02.000 align:start position:0%",
        "Single circle, you're going to come up",
        " ",
        "",
        "00:00:02.000 --> 00:00:04.550 align:start position:0%",
        "Single circle, you're going to come up",
        "and<00:00:02.160><c> get</c><00:00:02.320><c> inside,</c><00:00:03.360><c> and</c><00:00:03.520><c> now</c>",
        "",
        "00:01:35.000 --> 00:01:37.000 align:start position:0%",
        "and get inside, and now",
        "Tom &amp; Jerry<00:01:36.000><c> fly</c>");

    [Fact]
    public void Removes_rolling_duplicates_and_tags_and_adds_time_markers()
    {
        var text = VttTranscript.ToText(AutoCaptions);

        Assert.Equal("[0:00] Single circle, you're going to come up and get inside, and now\n\n[1:35] Tom & Jerry fly", text);
    }

    [Theory]
    [InlineData(5, "0:05")]
    [InlineData(754, "12:34")]
    [InlineData(3725, "1:02:05")]
    public void Stamps(double seconds, string expected) => Assert.Equal(expected, VttTranscript.Stamp(seconds));
}

public class YouTubeParsingTests
{
    [Fact]
    public void Prefers_uploaded_subtitles_then_original_auto_captions_then_translations()
    {
        string[] none = [];
        Assert.Equal(("en-GB", false), YtDlpYouTubeClient.ChooseTrack(["de", "en-GB"], ["en-orig", "en"], "en", ["en"]));
        Assert.Equal(("en-orig", true), YtDlpYouTubeClient.ChooseTrack(["live_chat"], ["ar-orig", "en", "en-orig"], "ar", ["en"]));
        Assert.Equal(("de-orig", true), YtDlpYouTubeClient.ChooseTrack(none, ["de-orig", "en"], "de", ["en"]));
        Assert.Equal(("fr", false), YtDlpYouTubeClient.ChooseTrack(["fr"], ["en"], "fr", ["en"]));
        Assert.Equal(("en", true), YtDlpYouTubeClient.ChooseTrack(none, ["en", "es"], null, ["en"]));
        Assert.Null(YtDlpYouTubeClient.ChooseTrack(none, none, "en", ["en"]));
    }

    [Fact]
    public void Parses_video_info()
    {
        using var json = JsonDocument.Parse("""
            {"id":"U0G9sD7N_MA","title":"F-22 pilot reacts","channel":"MachSquawk","channel_id":"UCuhfk79",
             "channel_url":"https://www.youtube.com/channel/UCuhfk79","timestamp":1792592886,"duration":1794,
             "description":"What does a real pilot think?","chapters":[{"start_time":0.0,"title":"Intro"},{"start_time":95.0,"title":"Startup"}]}
            """);

        var video = YtDlpYouTubeClient.ParseVideo(json.RootElement);

        Assert.Equal("MachSquawk", video.Channel);
        Assert.Equal("UCuhfk79", video.ChannelId);
        Assert.Equal(1794, video.DurationSeconds);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792592886), video.PublishedAt);
        Assert.Equal([new VideoChapter(0, "Intro"), new VideoChapter(95, "Startup")], video.Chapters);
    }

    [Fact]
    public void Parses_channel_listing()
    {
        using var json = JsonDocument.Parse("""
            {"id":"UCabc","title":"Prep Channel - Videos","channel":"Prep Channel","channel_id":"UCabc",
             "channel_url":"https://www.youtube.com/channel/UCabc","description":"Water and food storage.",
             "channel_follower_count":120000,
             "entries":[{"id":"v1","title":"Ceramic filters","duration":615,"view_count":5000},{"id":"v2","title":"Rain barrels"}]}
            """);

        var channel = YtDlpYouTubeClient.ParseChannel(json.RootElement, isPlaylist: false);

        Assert.Equal("Prep Channel", channel.Title);
        Assert.Equal(120000, channel.Followers);
        Assert.Equal(["Ceramic filters", "Rain barrels"], channel.Videos.Select(v => v.Title));
        Assert.Equal(615, channel.Videos[0].DurationSeconds);
    }

    [Fact]
    public void Parses_the_rss_feed()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns:yt="http://www.youtube.com/xml/schemas/2015" xmlns="http://www.w3.org/2005/Atom">
              <title>Prep Channel</title>
              <entry><yt:videoId>v2</yt:videoId><title>Rain barrels</title><published>2026-10-20T15:00:00+00:00</published></entry>
              <entry><yt:videoId>v1</yt:videoId><title>Ceramic filters</title><published>2026-10-01T15:00:00+00:00</published></entry>
            </feed>
            """;

        var entries = YtDlpYouTubeClient.ParseFeed(xml);

        Assert.Equal(["v2", "v1"], entries.Select(e => e.VideoId));
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.Zero), entries[0].PublishedAt);
    }
}

public sealed class FakeYouTube : IYouTubeClient
{
    public VideoDetails Video { get; set; } = new("vid", "Ceramic filter build", "Prep Channel", "UCprep",
        "https://www.youtube.com/channel/UCprep", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), 615,
        "How to build a ceramic filter.", [new VideoChapter(0, "Intro"), new VideoChapter(95, "Drilling")],
        "[0:00] Today we build a ceramic water filter from two buckets.", "Auto-generated captions (en-orig)", null);

    public ChannelDetails Channel { get; set; } = new("UCprep", "Prep Channel", "UCprep", "https://www.youtube.com/channel/UCprep",
        "Prep Channel", "Water and food storage.", 120000, false,
        [new RecentVideo("v1", "Ceramic filters", 615, 5000), new RecentVideo("v2", "Rain barrels", null, null)]);

    public List<FeedEntry> Feed { get; set; } = [];
    public Exception? FeedError { get; set; }

    public Task<VideoDetails> GetVideoAsync(string url, IReadOnlyList<string> preferredLanguages, CancellationToken ct) => Task.FromResult(Video);
    public Task<ChannelDetails> GetChannelAsync(string url, int recentVideos, CancellationToken ct) => Task.FromResult(Channel);
    public Task<IReadOnlyList<FeedEntry>> GetFeedAsync(string channelId, CancellationToken ct) =>
        FeedError is not null ? Task.FromException<IReadOnlyList<FeedEntry>>(FeedError) : Task.FromResult<IReadOnlyList<FeedEntry>>(Feed);
}

public sealed class YouTubePipelineTests : IAsyncLifetime
{
    private readonly FakeYouTube _youTube = new();
    private readonly FakeOllama _ollama = new();
    private readonly PageFetcher _fetcher = new();
    private readonly LinkTrigger _trigger = new();
    private TestDatabase _db = default!;
    private FollowService _follows = default!;
    private LinkProcessor _processor = default!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _follows = new FollowService(_db.Factory, _youTube, _trigger, NullLogger<FollowService>.Instance);
        _processor = new LinkProcessor(_db.Factory, _db.Settings, _fetcher, new ReadableText(new TextExtractor()),
            new Summarizer(_ollama), _ollama, NullLogger<LinkProcessor>.Instance, null, _youTube, _follows);
        await _db.Settings.SaveAsync(new InboxSettings { OllamaModel = "gemma3:12b" }, null);
    }

    public async Task DisposeAsync()
    {
        _fetcher.Dispose();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Video_gets_transcript_metadata_and_a_video_summary()
    {
        var id = await AddLinkAsync("https://www.youtube.com/watch?v=vid", LinkKind.YouTubeVideo);

        var outcome = await _processor.RunOnceAsync(default);

        var link = await LoadLinkAsync(id);
        Assert.Equal(new LinkOutcome(1, 1, 0), outcome);
        Assert.Equal(LinkStatus.Done, link.Status);
        Assert.Equal("Prep Channel", link.Author);
        Assert.Equal("UCprep", link.AuthorId);
        Assert.Equal(615, link.DurationSeconds);
        Assert.Equal("0:00 Intro\n1:35 Drilling", link.Chapters);
        Assert.Equal("Auto-generated captions (en-orig)", link.ContentSource);
        Assert.Contains("ceramic water filter", link.ContentText);

        var call = _ollama.Calls.Single();
        Assert.Equal(Summarizer.VideoPrompt, call.System);
        Assert.Contains("Channel: Prep Channel", call.User);
        Assert.Contains("1:35 Drilling", call.User);
        Assert.Contains("Transcript:", call.User);
    }

    [Fact]
    public async Task Video_without_captions_is_summarized_from_its_description()
    {
        _youTube.Video = _youTube.Video with { Transcript = null, TranscriptSource = null, Note = "This video has no captions." };
        var id = await AddLinkAsync("https://www.youtube.com/watch?v=vid", LinkKind.YouTubeVideo);

        await _processor.RunOnceAsync(default);

        var link = await LoadLinkAsync(id);
        Assert.Equal("How to build a ceramic filter.", link.ContentText);
        Assert.StartsWith("Video description", link.ContentSource);
        Assert.Equal(LinkStatus.Done, link.Status);
    }

    [Fact]
    public async Task Channel_lists_recent_videos_and_gets_a_channel_summary()
    {
        var id = await AddLinkAsync("https://www.youtube.com/@PrepChannel", LinkKind.YouTubeChannel);

        await _processor.RunOnceAsync(default);

        var link = await LoadLinkAsync(id);
        Assert.Equal("Prep Channel", link.Title);
        Assert.Equal("UCprep", link.AuthorId);
        Assert.Equal("- Ceramic filters (10:15), 5,000 views\n- Rain barrels", link.ContentText);
        Assert.Equal(Summarizer.ChannelPrompt, _ollama.Calls.Single().System);
        Assert.Empty(await FollowedAsync()); // no #follow tag
    }

    [Fact]
    public async Task Follow_tag_follows_the_channel_with_the_emails_filing()
    {
        await AddLinkAsync("https://www.youtube.com/@PrepChannel", LinkKind.YouTubeChannel, tags: ["water", "follow"]);

        await _processor.RunOnceAsync(default);

        var channel = Assert.Single(await FollowedAsync());
        Assert.Equal("UCprep", channel.ChannelId);
        Assert.Equal("Survival/Water", channel.Category);
        Assert.Equal(["water"], channel.Tags);
    }

    [Fact]
    public async Task New_uploads_become_items_once_and_old_ones_are_ignored()
    {
        var followed = await _follows.FollowAsync("UCprep", "Prep Channel", "https://www.youtube.com/channel/UCprep",
            "Survival/Water", ["water"], default);
        var now = DateTimeOffset.UtcNow;
        _youTube.Feed =
        [
            new FeedEntry("new2", "Rain barrels", now.AddMinutes(10)),
            new FeedEntry("new1", "Ceramic filters", now.AddMinutes(5)),
            new FeedEntry("old", "Before following", now.AddDays(-3)),
        ];

        var first = await _follows.CheckAsync(followed.Id, default);
        var second = await _follows.CheckAsync(followed.Id, default);

        Assert.Equal(2, first);
        Assert.Equal(0, second);
        await using var db = await _db.Factory.CreateDbContextAsync();
        var items = await db.Emails.Include(e => e.Links).OrderBy(e => e.ReceivedAt).ToListAsync();
        Assert.Equal(["Ceramic filters", "Rain barrels"], items.Select(e => e.Title));
        Assert.All(items, e =>
        {
            Assert.Equal(ItemSource.FollowedChannel, e.Source);
            Assert.Equal("Survival/Water", e.Category);
            Assert.Equal(LinkKind.YouTubeVideo, Assert.Single(e.Links).Kind);
        });
        Assert.Equal(2, (await db.FollowedChannels.SingleAsync()).VideosFound);
    }

    [Fact]
    public async Task Due_checks_respect_the_interval_and_record_errors()
    {
        await _follows.FollowAsync("UCprep", "Prep Channel", "https://www.youtube.com/channel/UCprep", null, [], default);
        _youTube.FeedError = new YouTubeException("The channel feed answered 404 Not Found.");

        await _follows.CheckDueAsync(6, default);
        _youTube.FeedError = null;
        _youTube.Feed = [new FeedEntry("x", "New", DateTimeOffset.UtcNow.AddMinutes(1))];
        var notDue = await _follows.CheckDueAsync(6, default); // checked moments ago

        Assert.Equal(0, notDue);
        Assert.Contains("404", (await FollowedAsync()).Single().LastError);
    }

    private async Task<List<FollowedChannel>> FollowedAsync()
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        return await db.FollowedChannels.ToListAsync();
    }

    private async Task<int> AddLinkAsync(string url, LinkKind kind, List<string>? tags = null)
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        var email = new InboxEmail
        {
            MessageId = Guid.NewGuid().ToString(), Folder = "INBOX", FromAddress = "me@gmail.com", Subject = "NOMAD: x",
            Category = "Survival/Water", Tags = tags ?? [], ReceivedAt = DateTimeOffset.UtcNow,
        };
        var link = new EmailLink { Url = url, Kind = kind };
        email.Links.Add(link);
        db.Emails.Add(email);
        await db.SaveChangesAsync();
        return link.Id;
    }

    private async Task<EmailLink> LoadLinkAsync(int id)
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        return await db.Links.SingleAsync(l => l.Id == id);
    }
}
