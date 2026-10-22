using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Ai;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.Ingest;
using NomadInbox.Infrastructure.Web;
using NomadInbox.Infrastructure.YouTube;

namespace NomadInbox.Tests;

public class VideoDownloaderTests
{
    private static EmailLink Video() => new()
    {
        Url = "https://www.youtube.com/watch?v=U0G9sD7N_MA",
        Kind = LinkKind.YouTubeVideo,
        ExternalId = "U0G9sD7N_MA",
        Title = "I showed an F-22 pilot: the best dogfights?",
        Author = "MachSquawk",
        PublishedAt = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero),
        DurationSeconds = 1794,
        Chapters = "0:00 Intro\n1:23 F-15C Startup",
        Description = "Original description",
    };

    [Theory]
    [InlineData(VideoDownloadMode.Never, true, false)]
    [InlineData(VideoDownloadMode.Tagged, false, false)]
    [InlineData(VideoDownloadMode.Tagged, true, true)]
    [InlineData(VideoDownloadMode.Always, false, true)]
    public void Download_policy(VideoDownloadMode mode, bool tagged, bool expected)
    {
        string[] tags = tagged ? ["water", "Download"] : ["water"];

        Assert.Equal(expected, VideoDownloader.ShouldDownload(mode, tags));
    }

    [Fact]
    public void Files_are_named_by_channel_date_title_and_id()
    {
        var (folder, name) = VideoDownloader.NameFor(Video());

        Assert.Equal("MachSquawk", folder);
        Assert.Equal("2026-10-21 I showed an F-22 pilot_ the best dogfights_ [U0G9sD7N_MA]", name);
    }

    [Fact]
    public void Nfo_carries_summary_chapters_channel_and_filing()
    {
        var link = Video();
        link.Summary = "**A pilot reacts.**\n- point";
        var email = new InboxEmail
        {
            MessageId = "m", Folder = "INBOX", FromAddress = "a", Subject = "s",
            Category = "Aviation/Fighters", Tags = ["dcs", "download", "follow"],
        };

        var nfo = VideoDownloader.BuildNfo(link, email);
        var movie = XDocument.Parse(nfo).Root!;

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>", nfo);
        Assert.Equal("I showed an F-22 pilot: the best dogfights?", (string?)movie.Element("title"));
        Assert.StartsWith("A pilot reacts.\n• point", (string?)movie.Element("plot"));
        Assert.Contains("1:23 F-15C Startup", (string?)movie.Element("plot"));
        Assert.Equal("2026-10-21", (string?)movie.Element("premiered"));
        Assert.Equal("29", (string?)movie.Element("runtime"));
        Assert.Equal("MachSquawk", (string?)movie.Element("studio"));
        Assert.Equal("Aviation/Fighters", (string?)movie.Element("genre"));
        Assert.Equal(["dcs"], movie.Elements("tag").Select(t => t.Value));
        Assert.Equal("U0G9sD7N_MA", (string?)movie.Element("uniqueid"));
    }
}

public sealed class VideoQueueTests : IAsyncLifetime
{
    private readonly FakeYouTube _youTube = new();
    private readonly FakeOllama _ollama = new();
    private readonly PageFetcher _fetcher = new();
    private readonly VideoTrigger _videoTrigger = new();
    private TestDatabase _db = default!;
    private LinkProcessor _processor = default!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        var downloader = new VideoDownloader(new YtDlp(NullLogger<YtDlp>.Instance), Path.GetTempPath(), NullLogger<VideoDownloader>.Instance);
        _processor = new LinkProcessor(_db.Factory, _db.Settings, _fetcher, new ReadableText(new TextExtractor()),
            new Summarizer(_ollama), _ollama, NullLogger<LinkProcessor>.Instance, null, _youTube, null, downloader, _videoTrigger);
        await _db.Settings.SaveAsync(new InboxSettings(), null); // default mode: only #download emails
    }

    public async Task DisposeAsync()
    {
        _fetcher.Dispose();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Only_download_tagged_videos_are_queued()
    {
        var tagged = await AddVideoAsync(["download"]);
        var plain = await AddVideoAsync([]);

        await _processor.RunOnceAsync(default);

        await using var db = await _db.Factory.CreateDbContextAsync();
        Assert.Equal(VideoStatus.Queued, (await db.Links.SingleAsync(l => l.Id == tagged)).VideoStatus);
        Assert.Equal(VideoStatus.None, (await db.Links.SingleAsync(l => l.Id == plain)).VideoStatus);
    }

    private async Task<int> AddVideoAsync(List<string> tags)
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        var email = new InboxEmail
        {
            MessageId = Guid.NewGuid().ToString(), Folder = "INBOX", FromAddress = "me@gmail.com", Subject = "NOMAD: x",
            Tags = tags, ReceivedAt = DateTimeOffset.UtcNow,
        };
        var link = new EmailLink { Url = "https://www.youtube.com/watch?v=vid", Kind = LinkKind.YouTubeVideo };
        email.Links.Add(link);
        db.Emails.Add(email);
        await db.SaveChangesAsync();
        return link.Id;
    }
}
