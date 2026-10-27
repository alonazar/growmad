using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Bundles;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.Images;
using NomadInbox.Infrastructure.Ingest;
using NomadInbox.Infrastructure.Knowledge;
using NomadInbox.Infrastructure.Pages;
using NomadInbox.Infrastructure.YouTube;

namespace NomadInbox.Tests;

/// <summary>A collector and an air-gapped side, each with its own database and files, passing bundles through a folder.</summary>
public sealed class BundleTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nomad-inbox-tests", Guid.NewGuid().ToString("N"));
    private TestDatabase _collectorDb = default!, _airDb = default!;
    private AttachmentStore _collectorFiles = default!, _airFiles = default!;
    private PageStore _collectorPages = default!, _airPages = default!;
    private VideoDownloader _collectorVideos = default!, _airVideos = default!;
    private BundleExporter _exporter = default!;
    private BundleImporter _importer = default!;

    public async Task InitializeAsync()
    {
        _collectorDb = await TestDatabase.CreateAsync();
        _airDb = await TestDatabase.CreateAsync();
        _collectorFiles = new AttachmentStore(Path.Combine(_root, "collector-files"));
        _airFiles = new AttachmentStore(Path.Combine(_root, "air-files"));
        _collectorPages = new PageStore(Path.Combine(_root, "collector-data"), Path.Combine(_root, "collector-pages"));
        _airPages = new PageStore(Path.Combine(_root, "air-data"), Path.Combine(_root, "air-pages"));
        _collectorVideos = new VideoDownloader(new YtDlp(NullLogger<YtDlp>.Instance), Path.Combine(_root, "collector-videos"), NullLogger<VideoDownloader>.Instance);
        _airVideos = new VideoDownloader(new YtDlp(NullLogger<YtDlp>.Instance), Path.Combine(_root, "air-videos"), NullLogger<VideoDownloader>.Instance);
        _exporter = new BundleExporter(_collectorDb.Factory, _collectorDb.Settings, _collectorFiles, _collectorPages, _collectorVideos,
            Path.Combine(_root, "export"), NullLogger<BundleExporter>.Instance);
        var pageExporter = new PageExporter(_airDb.Factory, new PageRenderer(_airDb.Factory, _airPages), _airPages, _airDb.Settings, NullLogger<PageExporter>.Instance);
        _importer = new BundleImporter(_airDb.Factory, _airDb.Settings, _airFiles, _airPages, pageExporter, _airVideos,
            new KnowledgeTrigger(), new LinkTrigger(), new ImageTrigger(), Path.Combine(_root, "import"), NullLogger<BundleImporter>.Instance);
        await _collectorDb.Settings.SaveAsync(new InboxSettings { Mode = AppMode.Collector, CollectorName = "Home PC" }, null);
        await _airDb.Settings.SaveAsync(new InboxSettings { Mode = AppMode.AirGapped, OllamaModel = "gemma3:12b" }, null);
    }

    public async Task DisposeAsync()
    {
        await _collectorDb.DisposeAsync();
        await _airDb.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Round_trip_brings_everything_and_gives_the_same_knowledge_documents()
    {
        await AddEmailAsync("m1@x", "First summary");

        var export = await ExportOneAsync();
        Assert.NotNull(export);
        Assert.Equal(1, export.Number);
        Assert.Equal(1, export.Items);
        Assert.Matches(BundleFormat.FileName(), export.FileName);

        var result = await ImportAsync(export.FileName);
        Assert.Equal(ImportStatus.Imported, result.Status);
        Assert.Equal(1, result.Items);

        var email = await LoadAsync(_airDb, "m1@x");
        Assert.Equal(EmailStatus.Done, email.Status);
        Assert.Equal("Survival/Water", email.Category);
        Assert.Equal(["diy"], email.Tags);
        Assert.Equal("high", email.Fields["priority"]);
        Assert.Equal("A note about the filter, long enough to count as a note.", email.BodyText);
        Assert.Equal([LinkStatus.Done, LinkStatus.Fetched], email.Links.OrderBy(l => l.Position).Select(l => l.Status));
        Assert.Equal("First summary", email.Links.Single(l => l.Position == 0).Summary);
        Assert.Null(email.Links.Single(l => l.Position == 1).Summary); // summarized here, by the air-gapped side's AI

        var notes = email.Attachments.Single(a => a.FileName == "notes.txt");
        Assert.Equal("boil it", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(_airFiles.ResolveFullPath(notes.StoredPath!)!)));
        var program = email.Attachments.Single(a => a.FileName == "setup.exe");
        Assert.True(program.StoredAsZip);
        Assert.EndsWith("setup.exe.zip", program.StoredPath);

        await using (var db = await _airDb.Factory.CreateDbContextAsync())
        {
            var image = await db.ImageDescriptions.SingleAsync();
            Assert.Equal(ImageDescriptionStatus.Done, image.Status);
            Assert.Equal("A ceramic filter.", image.Caption);
        }

        // What each side would send to NOMAD's knowledge base is the same.
        Assert.Equal(await DocumentsAsync(_collectorDb, _collectorFiles), await DocumentsAsync(_airDb, _airFiles));
    }

    [Fact]
    public async Task Only_new_and_changed_items_go_into_the_next_bundle()
    {
        await AddEmailAsync("m1@x", "First summary");
        await AddEmailAsync("m2@x", "Other summary");
        Assert.Equal(2, (await ExportOneAsync()).Items);

        Assert.Empty(await _exporter.ExportAsync(default)); // nothing new

        await SetSummaryAsync(_collectorDb, "m1@x", "Better summary");
        var second = await ExportOneAsync();
        Assert.Equal(2, second.Number);
        Assert.Equal(1, second.Items);
    }

    [Fact]
    public async Task Importing_twice_changes_nothing()
    {
        await AddEmailAsync("m1@x", "First summary");
        var export = await ExportOneAsync();

        await ImportAsync(export.FileName);
        var again = await ImportAsync(export.FileName);

        Assert.Equal(ImportStatus.AlreadyImported, again.Status);
        await using var db = await _airDb.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.Emails.CountAsync());
        Assert.Equal(3, await db.Attachments.CountAsync());
    }

    [Fact]
    public async Task An_older_bundle_imported_later_does_not_overwrite_a_newer_one()
    {
        await AddEmailAsync("m1@x", "Old summary");
        var first = await ExportOneAsync();
        await SetSummaryAsync(_collectorDb, "m1@x", "New summary");
        var second = await ExportOneAsync();

        await ImportAsync(second.FileName);
        var late = await ImportAsync(first.FileName);

        Assert.Equal(ImportStatus.Imported, late.Status);
        Assert.Equal(0, late.Items);
        Assert.Equal(1, late.Skipped);
        Assert.Equal("New summary", (await LoadAsync(_airDb, "m1@x")).Links.Single(l => l.Position == 0).Summary);
    }

    [Fact]
    public async Task A_changed_item_replaces_the_earlier_copy()
    {
        await AddEmailAsync("m1@x", "Old summary");
        await ImportAsync((await ExportOneAsync()).FileName);
        await SetSummaryAsync(_collectorDb, "m1@x", "New summary");

        await ImportAsync((await ExportOneAsync()).FileName);

        var email = await LoadAsync(_airDb, "m1@x");
        Assert.Equal("New summary", email.Links.Single(l => l.Position == 0).Summary);
        Assert.Equal(2, email.Links.Count);
        Assert.Equal(3, email.Attachments.Count);
    }

    [Fact]
    public async Task Items_deleted_on_the_air_gapped_side_stay_deleted()
    {
        await AddEmailAsync("m1@x", "Summary");
        await using (var db = await _airDb.Factory.CreateDbContextAsync())
        {
            db.DeletedMessages.Add(new DeletedMessage { MessageId = "m1@x" });
            await db.SaveChangesAsync();
        }

        var result = await ImportAsync((await ExportOneAsync()).FileName);

        Assert.Equal(1, result.Skipped);
        await using var check = await _airDb.Factory.CreateDbContextAsync();
        Assert.False(await check.Emails.AnyAsync());
    }

    [Fact]
    public async Task Changed_file_is_rejected_and_nothing_is_imported()
    {
        await AddEmailAsync("m1@x", "Summary");
        var path = BundlePath((await ExportOneAsync()).FileName);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.Entries.First(e => e.FullName.StartsWith("items/"));
            var name = entry.FullName;
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write("""{"messageId":"evil@x"}""");
        }

        var result = await _importer.ImportAsync(path, Path.GetFileName(path), default);

        Assert.Equal(ImportStatus.Rejected, result.Status);
        Assert.Contains("checksum", result.Message);
        await using var db = await _airDb.Factory.CreateDbContextAsync();
        Assert.False(await db.Emails.AnyAsync());
    }

    [Theory]
    [InlineData("../evil.json")]
    [InlineData("files/not-a-checksum")]
    [InlineData("extra.txt")]
    public async Task Unexpected_files_are_rejected(string name)
    {
        await AddEmailAsync("m1@x", "Summary");
        var path = BundlePath((await ExportOneAsync()).FileName);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(zip.CreateEntry(name).Open()))
            writer.Write("x");

        var result = await _importer.ImportAsync(path, Path.GetFileName(path), default);

        Assert.Equal(ImportStatus.Rejected, result.Status);
        Assert.Contains("unexpected file", result.Message);
    }

    [Fact]
    public async Task Files_that_are_not_bundles_are_rejected()
    {
        var notZip = Path.Combine(_root, "notes.zip");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(notZip, "hello");
        var plainZip = Path.Combine(_root, "plain.zip");
        using (var zip = ZipFile.Open(plainZip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("photo.jpg").Open()))
            writer.Write("x");

        Assert.Contains("Not a ZIP", (await _importer.ImportAsync(notZip, "notes.zip", default)).Message);
        Assert.Contains("no manifest", (await _importer.ImportAsync(plainZip, "plain.zip", default)).Message);
    }

    [Fact]
    public async Task Bundles_are_only_imported_in_air_gapped_mode_and_exported_in_collector_mode()
    {
        await AddEmailAsync("m1@x", "Summary");
        var export = await ExportOneAsync();
        await _airDb.Settings.SaveAsync(new InboxSettings { Mode = AppMode.Connected }, null);
        await _collectorDb.Settings.SaveAsync(new InboxSettings { Mode = AppMode.Connected }, null);

        Assert.Equal(ImportStatus.Rejected, (await ImportAsync(export.FileName)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _exporter.ExportAsync(default));
    }

    [Fact]
    public async Task Watched_folder_imports_and_moves_bundles()
    {
        await AddEmailAsync("m1@x", "Summary");
        var export = await ExportOneAsync();
        Directory.CreateDirectory(_importer.ImportDirectory);
        var dropped = Path.Combine(_importer.ImportDirectory, export.FileName);
        File.Copy(BundlePath(export.FileName), dropped);
        File.SetLastWriteTimeUtc(dropped, DateTime.UtcNow.AddMinutes(-5)); // finished copying
        var broken = Path.Combine(_importer.ImportDirectory, "broken.zip");
        await File.WriteAllTextAsync(broken, "not a zip");
        File.SetLastWriteTimeUtc(broken, DateTime.UtcNow.AddMinutes(-5));
        var copying = Path.Combine(_importer.ImportDirectory, "copying.zip");
        await File.WriteAllTextAsync(copying, "still arriving");

        var worker = new BundleImportWorker(_importer, _airDb.Settings, NullLogger<BundleImportWorker>.Instance);
        Assert.Equal(2, await worker.ImportFolderAsync(default));

        Assert.True(File.Exists(Path.Combine(_importer.ImportDirectory, "imported", export.FileName)));
        Assert.True(File.Exists(Path.Combine(_importer.ImportDirectory, "rejected", "broken.zip")));
        Assert.True(File.Exists(copying)); // left alone until it's settled
        Assert.Equal(EmailStatus.Done, (await LoadAsync(_airDb, "m1@x")).Status);
    }

    [Fact]
    public async Task Collector_does_not_send_to_NOMAD()
    {
        await AddEmailAsync("m1@x", "Summary");
        var nomad = new FakeNomad();
        var sync = new KnowledgeSyncService(_collectorDb.Factory, _collectorDb.Settings, nomad, _collectorFiles, NullLogger<KnowledgeSyncService>.Instance);

        Assert.Equal(0, (await sync.RunOnceAsync(default)).Uploaded);
        Assert.Empty(nomad.Files);
    }

    [Fact]
    public async Task Saved_pages_and_videos_travel_with_their_item()
    {
        await AddEmailAsync("m1@x", "Summary");
        var (video, image) = await AddPagesAndVideoAsync("m1@x");

        var result = await ImportAsync((await ExportOneAsync()).FileName);
        Assert.Equal(ImportStatus.Imported, result.Status);

        // The video, its thumbnail and Jellyfin's metadata, under the same path in the videos folder.
        var email = await LoadAsync(_airDb, "m1@x");
        var link = email.Links.Single(l => l.Kind == LinkKind.YouTubeVideo);
        Assert.Equal(VideoStatus.Downloaded, link.VideoStatus);
        Assert.Equal(VideoPath, link.VideoPath);
        var videoFile = _airVideos.ResolveFullPath(VideoPath)!;
        Assert.Equal(video, await File.ReadAllBytesAsync(videoFile));
        Assert.Equal(video.Length, link.VideoBytes);
        Assert.True(File.Exists(videoFile.Replace(".mp4", "-thumb.jpg")));
        Assert.Contains("How to build it.", await File.ReadAllTextAsync(Path.ChangeExtension(videoFile, ".nfo")));

        // The pages: content, media, links, where they came from, the image description and the File Browser copy.
        await using var db = await _airDb.Factory.CreateDbContextAsync();
        var pages = await db.Pages.Include(p => p.Assets).Include(p => p.Links).AsSplitQuery().OrderBy(p => p.Id).ToListAsync();
        Assert.Equal(["https://example.com/a", "https://example.com/c"], pages.Select(p => p.Url));
        var guide = pages[0];
        Assert.All(pages, p => Assert.Equal((PageStatus.Done, email.Id), (p.Status, p.InboxEmailId)));
        Assert.Equal(email.Links.Single(l => l.Position == 0).Id, guide.SourceLinkId);
        Assert.Equal(guide.Id, pages[1].SavedFromPageId);
        Assert.Equal("Boil water first.", guide.TextContent);
        Assert.Contains($"nomad-asset:{image}", await _airPages.ReadContentAsync(guide.Id, default));
        Assert.Equal($"pages/{guide.Id}/content.html", guide.ContentPath);
        Assert.Equal(image, guide.Assets.Single().FileName);
        Assert.True(File.Exists(Path.Combine(_airPages.AssetRoot, image)));
        Assert.Equal("https://example.com/c", guide.Links.Single().Url);
        var description = await db.ImageDescriptions.SingleAsync(d => d.Source == ImageSource.PageAsset);
        Assert.Equal((Path.GetFileNameWithoutExtension(image), "A boiling pot.", ImageDescriptionStatus.Done),
            (description.ImageKey, description.Caption, description.Status));
        Assert.True(File.Exists(Path.Combine(_airPages.ExportRoot, "index.html")));
    }

    [Fact]
    public async Task A_changed_item_keeps_its_pages_pointing_at_its_links()
    {
        await AddEmailAsync("m1@x", "Old summary");
        await AddPagesAndVideoAsync("m1@x");
        await ImportAsync((await ExportOneAsync()).FileName);
        await SetSummaryAsync(_collectorDb, "m1@x", "New summary");

        await ImportAsync((await ExportOneAsync()).FileName);

        var email = await LoadAsync(_airDb, "m1@x");
        await using var db = await _airDb.Factory.CreateDbContextAsync();
        Assert.Equal(2, await db.Pages.CountAsync());
        Assert.Equal(email.Links.Single(l => l.Position == 0).Id, (await db.Pages.SingleAsync(p => p.Url == "https://example.com/a")).SourceLinkId);
    }

    [Fact]
    public async Task A_video_still_queued_on_the_collector_arrives_as_not_downloaded()
    {
        await AddEmailAsync("m1@x", "Summary");
        await using (var db = await _collectorDb.Factory.CreateDbContextAsync())
        {
            var email = await db.Emails.SingleAsync();
            email.ProcessedAt = DateTimeOffset.UtcNow.AddDays(-1); // past the wait
            email.Links.Add(new EmailLink { Url = "https://www.youtube.com/watch?v=q", Kind = LinkKind.YouTubeVideo, Position = 2, Status = LinkStatus.Done, VideoStatus = VideoStatus.Queued });
            await db.SaveChangesAsync();
        }

        await ImportAsync((await ExportOneAsync()).FileName);

        var link = (await LoadAsync(_airDb, "m1@x")).Links.Single(l => l.Position == 2);
        Assert.Equal(VideoStatus.Failed, link.VideoStatus);
        Assert.Null(link.VideoPath);
    }

    [Fact]
    public async Task A_big_export_is_split_into_self_contained_bundles()
    {
        var settings = await _collectorDb.Settings.GetAsync();
        settings.MaxBundleMegabytes = 1;
        await _collectorDb.Settings.SaveAsync(settings, null);
        foreach (var id in new[] { "m1@x", "m2@x", "m3@x" })
        {
            await AddEmailAsync(id, "Summary");
            await AddPagesAndVideoAsync(id, videoKilobytes: 600, videoPath: $"Chan/{id}.mp4", withPages: false);
        }

        var bundles = await _exporter.ExportAsync(default);

        Assert.Equal([1, 2, 3], bundles.Select(b => b.Number));
        Assert.All(bundles, b => Assert.Equal(1, b.Items));
        Assert.All(bundles, b => Assert.InRange(b.Bytes, 600 * 1024, 1024 * 1024));
        foreach (var bundle in bundles.Reverse()) Assert.Equal(ImportStatus.Imported, (await ImportAsync(bundle.FileName)).Status); // any order
        await using var db = await _airDb.Factory.CreateDbContextAsync();
        Assert.Equal(3, await db.Emails.CountAsync());
        Assert.Equal(3, await db.Links.CountAsync(l => l.VideoStatus == VideoStatus.Downloaded));
    }

    [Fact]
    public async Task A_page_asset_that_does_not_match_its_name_is_rejected()
    {
        await AddEmailAsync("m1@x", "Summary");
        await AddPagesAndVideoAsync("m1@x");
        var path = BundlePath((await ExportOneAsync()).FileName);
        // Swap an asset's content and fix the manifest's checksum to match, as someone tampering would.
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var asset = zip.Entries.First(e => e.FullName.StartsWith("assets/")).FullName;
            zip.GetEntry(asset)!.Delete();
            var evil = Encoding.UTF8.GetBytes("something else");
            await using (var stream = zip.CreateEntry(asset).Open()) await stream.WriteAsync(evil);
            var manifestEntry = zip.GetEntry(BundleFormat.ManifestName)!;
            BundleManifest manifest;
            await using (var stream = manifestEntry.Open())
                manifest = (await System.Text.Json.JsonSerializer.DeserializeAsync<BundleManifest>(stream, BundleFormat.Json))!;
            manifestEntry.Delete();
            var files = new Dictionary<string, string>(manifest.Files) { [asset] = BundleFormat.Sha256(evil) };
            await using (var stream = zip.CreateEntry(BundleFormat.ManifestName).Open())
                await System.Text.Json.JsonSerializer.SerializeAsync(stream, manifest with { Files = files }, BundleFormat.Json);
        }

        var result = await _importer.ImportAsync(path, Path.GetFileName(path), default);

        Assert.Equal(ImportStatus.Rejected, result.Status);
        Assert.Contains("checksum", result.Message);
    }

    [Theory]
    [InlineData("Chan/2026-10-01 Title [abc].mp4", "Chan/2026-10-01 Title [abc].mp4")]
    [InlineData("../../passwd", "attachment/attachment/passwd")]
    [InlineData("..\\..\\x.mp4", "attachment/attachment/x.mp4")]
    [InlineData("/etc/passwd", "attachment/etc/passwd")]
    [InlineData("../../../../etc/passwd", null)] // too deep to be a video path
    public void Video_paths_from_a_bundle_stay_in_the_videos_folder(string path, string? expected)
    {
        var safe = BundleImporter.SafeVideoPath(path);
        Assert.Equal(expected, safe);
        if (safe is not null) Assert.NotNull(_airVideos.ResolveFullPath(safe));
    }

    private const string VideoPath = "Chan/2026-10-01 Filter build [abc].mp4";

    /// <summary>A downloaded video on link 2, a saved page for link 0 (with an image and a link), and a page saved from it.</summary>
    private async Task<(byte[] Video, string Image)> AddPagesAndVideoAsync(string messageId, int videoKilobytes = 1, string videoPath = VideoPath,
        bool withPages = true)
    {
        var video = new byte[videoKilobytes * 1024];
        Random.Shared.NextBytes(video);
        var videoFile = _collectorVideos.ResolveFullPath(videoPath)!;
        Directory.CreateDirectory(Path.GetDirectoryName(videoFile)!);
        await File.WriteAllBytesAsync(videoFile, video);
        await File.WriteAllBytesAsync(videoFile.Replace(".mp4", "-thumb.jpg"), Encoding.UTF8.GetBytes("jpeg thumb"));
        var image = await _collectorPages.SaveAssetAsync(Encoding.UTF8.GetBytes("png bytes of a pot"), "image/png", "https://example.com/pot.png", default);

        await using var db = await _collectorDb.Factory.CreateDbContextAsync();
        var email = await db.Emails.Include(e => e.Links).SingleAsync(e => e.MessageId == messageId);
        email.Links.Add(new EmailLink
        {
            Url = "https://www.youtube.com/watch?v=abc", Kind = LinkKind.YouTubeVideo, Position = 2, Status = LinkStatus.Done, ExternalId = "abc",
            Title = "Filter build", Author = "Chan", Summary = "How to build it.", VideoStatus = VideoStatus.Downloaded, VideoPath = videoPath, VideoBytes = video.Length,
        });
        if (!withPages)
        {
            await db.SaveChangesAsync();
            return (video, image);
        }
        var guide = new ArchivedPage
        {
            Url = "https://example.com/a", Status = PageStatus.Done, Title = "Guide", TextContent = "Boil water first.", InboxEmailId = email.Id,
            SourceLinkId = email.Links.Single(l => l.Position == 0).Id, ArchivedAt = DateTimeOffset.UtcNow, Bytes = 100,
            Assets = [new PageAsset { OriginalUrl = "https://example.com/pot.png", Kind = AssetKind.Image, FileName = image, ContentType = "image/png", Bytes = 18 }],
            Links = [new PageLink { Url = "https://example.com/c", Text = "More", Position = 0 }],
        };
        db.Pages.Add(guide);
        if (!await db.ImageDescriptions.AnyAsync(d => d.Source == ImageSource.PageAsset))
            db.ImageDescriptions.Add(new ImageDescription
            {
                ImageKey = Path.GetFileNameWithoutExtension(image), Source = ImageSource.PageAsset, SourcePath = image,
                Status = ImageDescriptionStatus.Done, Caption = "A boiling pot.",
            });
        await db.SaveChangesAsync();
        var more = new ArchivedPage { Url = "https://example.com/c", Status = PageStatus.Done, Title = "More", InboxEmailId = email.Id, SavedFromPageId = guide.Id, ArchivedAt = DateTimeOffset.UtcNow };
        db.Pages.Add(more);
        await db.SaveChangesAsync();
        await _collectorPages.WriteContentAsync(guide.Id, $"<p>Boil water first.</p><img src=\"nomad-asset:{image}\"><a href=\"nomad-link:0\">More</a>", default);
        await _collectorPages.WriteContentAsync(more.Id, "<p>More.</p>", default);
        return (video, image);
    }

    private async Task<ExportResult> ExportOneAsync() => Assert.Single(await _exporter.ExportAsync(default));

    private Task<ImportResult> ImportAsync(string fileName) => _importer.ImportAsync(BundlePath(fileName), fileName, default);

    private string BundlePath(string fileName) => Path.Combine(_exporter.ExportDirectory, fileName);

    private async Task AddEmailAsync(string messageId, string summary)
    {
        var received = DateTimeOffset.UtcNow.AddMinutes(-10);
        await using var db = await _collectorDb.Factory.CreateDbContextAsync();
        var email = new InboxEmail
        {
            MessageId = messageId, Folder = "INBOX", FromAddress = "me@gmail.com", FromName = "Me", Subject = "NOMAD: Survival/Water #diy priority=high Filters",
            Category = "Survival/Water", Tags = ["diy"], Fields = new() { ["priority"] = "high" }, Title = "Filters", Status = EmailStatus.Done,
            ReceivedAt = received, ProcessedAt = received, BodyText = "A note about the filter, long enough to count as a note.",
        };
        email.Links.Add(new EmailLink { Url = "https://example.com/a", Kind = LinkKind.Web, Position = 0, Status = LinkStatus.Done, Title = "Guide", ContentText = "Article", Summary = summary });
        email.Links.Add(new EmailLink { Url = "https://example.com/b", Kind = LinkKind.Web, Position = 1, Status = LinkStatus.Fetched, Title = "Other", ContentText = "More text" });

        async Task<EmailAttachment> FileAsync(string name, string type, byte[] content, bool zipped = false)
        {
            var sha = AttachmentStore.ComputeSha256(content);
            var stored = zipped ? Encoding.UTF8.GetBytes("zip of " + name) : content;
            var path = await _collectorFiles.SaveAsync(email.Category, received, zipped ? name + ".zip" : name, stored, AttachmentStore.ComputeSha256(stored), default);
            return new EmailAttachment { FileName = name, ContentType = type, SizeBytes = content.Length, Sha256 = sha, StoredPath = path, StoredAsZip = zipped };
        }

        var notes = await FileAsync("notes.txt", "text/plain", Encoding.UTF8.GetBytes("boil it"));
        notes.ExtractedText = "boil it";
        email.Attachments.Add(notes);
        var photo = await FileAsync("filter.jpg", "image/jpeg", Encoding.UTF8.GetBytes("jpeg bytes " + messageId));
        email.Attachments.Add(photo);
        email.Attachments.Add(await FileAsync("setup.exe", "application/octet-stream", Encoding.UTF8.GetBytes("MZ program"), zipped: true));
        db.Emails.Add(email);
        db.ImageDescriptions.Add(new ImageDescription
        {
            ImageKey = ImageDescription.KeyFor(photo.Sha256!), Source = ImageSource.Attachment, SourcePath = photo.StoredPath!,
            Status = ImageDescriptionStatus.Done, Caption = "A ceramic filter.", ImageType = "photo", Categories = ["Survival/Water"], Tags = ["filter"],
        });
        await db.SaveChangesAsync();
    }

    private static async Task SetSummaryAsync(TestDatabase test, string messageId, string summary)
    {
        await using var db = await test.Factory.CreateDbContextAsync();
        var link = await db.Links.Where(l => l.Position == 0)
            .Join(db.Emails.Where(e => e.MessageId == messageId), l => l.InboxEmailId, e => e.Id, (l, _) => l).SingleAsync();
        link.Summary = summary;
        await db.SaveChangesAsync();
    }

    private static async Task<InboxEmail> LoadAsync(TestDatabase test, string messageId)
    {
        await using var db = await test.Factory.CreateDbContextAsync();
        return await db.Emails.AsNoTracking().Include(e => e.Links).Include(e => e.Attachments).AsSplitQuery().SingleAsync(e => e.MessageId == messageId);
    }

    private static async Task<List<string>> DocumentsAsync(TestDatabase test, AttachmentStore store)
    {
        await using var db = await test.Factory.CreateDbContextAsync();
        var images = await db.ImageDescriptions.AsNoTracking().Where(d => d.Status == ImageDescriptionStatus.Done).ToDictionaryAsync(d => d.ImageKey);
        var settings = new InboxSettings { OllamaModel = "m" };
        var emails = await db.Emails.AsNoTracking().Include(e => e.Links).Include(e => e.Attachments).AsSplitQuery().ToListAsync();
        return emails.SelectMany(e => KnowledgeDocumentBuilder.Build(e, settings, store, new KnowledgeContext(images, new Dictionary<int, ArchivedPage>())))
            .Select(d => $"{d.FileName}|{d.Collection}|{(d.Content is null ? Encoding.UTF8.GetString(File.ReadAllBytes(d.FilePath!)) : Encoding.UTF8.GetString(d.Content))}")
            .Order().ToList();
    }
}

public class BundleReadinessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<string, ImageDescription> NoImages = [];

    private static InboxEmail Email(LinkStatus status, int minutesAgo = 10) => new()
    {
        MessageId = "m@x", Folder = "INBOX", FromAddress = "me@gmail.com", Subject = "s", Status = EmailStatus.Done,
        ReceivedAt = Now.AddMinutes(-minutesAgo), ProcessedAt = Now.AddMinutes(-minutesAgo),
        Links = [new EmailLink { Url = "https://example.com", Status = status }],
    };

    [Theory]
    [InlineData(LinkStatus.Pending, false)]
    [InlineData(LinkStatus.Fetched, true)]
    [InlineData(LinkStatus.Done, true)]
    [InlineData(LinkStatus.Failed, true)]
    public void Without_an_AI_items_go_once_their_links_are_fetched(LinkStatus status, bool ready)
    {
        Assert.Equal(ready, BundleExporter.IsReady(Email(status), new InboxSettings(), NoImages, Now));
    }

    [Fact]
    public void With_an_AI_items_wait_for_summaries_but_not_forever()
    {
        var withAi = new InboxSettings { OllamaModel = "gemma3:12b", ExportAiWaitHours = 6 };

        Assert.False(BundleExporter.IsReady(Email(LinkStatus.Fetched), withAi, NoImages, Now));
        Assert.True(BundleExporter.IsReady(Email(LinkStatus.Done), withAi, NoImages, Now));
        Assert.True(BundleExporter.IsReady(Email(LinkStatus.Fetched, minutesAgo: 7 * 60), withAi, NoImages, Now));
        Assert.True(BundleExporter.IsReady(Email(LinkStatus.Pending, minutesAgo: 7 * 60), withAi, NoImages, Now));
    }

    [Fact]
    public void With_an_AI_items_wait_for_their_image_descriptions()
    {
        var withAi = new InboxSettings { OllamaModel = "gemma3:12b" };
        var email = Email(LinkStatus.Done);
        var sha = new string('a', 64);
        email.Attachments.Add(new EmailAttachment { FileName = "photo.jpg", ContentType = "image/jpeg", Sha256 = sha, StoredPath = "x/photo.jpg" });
        var pending = new Dictionary<string, ImageDescription> { [ImageDescription.KeyFor(sha)] = new() { ImageKey = ImageDescription.KeyFor(sha), SourcePath = "x" } };
        var done = new Dictionary<string, ImageDescription> { [ImageDescription.KeyFor(sha)] = new() { ImageKey = ImageDescription.KeyFor(sha), SourcePath = "x", Status = ImageDescriptionStatus.Done } };

        Assert.False(BundleExporter.IsReady(email, withAi, NoImages, Now)); // about to be queued
        Assert.False(BundleExporter.IsReady(email, withAi, pending, Now));
        Assert.True(BundleExporter.IsReady(email, withAi, done, Now));
        Assert.True(BundleExporter.IsReady(email, new InboxSettings(), NoImages, Now)); // no AI here: described on the other side
    }

    [Fact]
    public void Items_wait_for_their_pages_and_videos_but_not_forever()
    {
        var settings = new InboxSettings { ExportAiWaitHours = 6 };
        var downloading = Email(LinkStatus.Done);
        downloading.Links[0].VideoStatus = VideoStatus.Queued;
        var saving = new ArchivedPage { Url = "https://example.com", Status = PageStatus.Saving };

        Assert.False(BundleExporter.IsReady(downloading, settings, NoImages, Now));
        Assert.False(BundleExporter.IsReady(Email(LinkStatus.Done), settings, NoImages, Now, [saving]));
        Assert.True(BundleExporter.IsReady(Email(LinkStatus.Done), settings, NoImages, Now, [new ArchivedPage { Url = "https://example.com", Status = PageStatus.Done }]));
        Assert.True(BundleExporter.IsReady(Email(LinkStatus.Done, minutesAgo: 7 * 60), settings, NoImages, Now, [saving]));
    }

    [Fact]
    public void With_an_AI_items_wait_for_their_page_image_descriptions()
    {
        var withAi = new InboxSettings { OllamaModel = "gemma3:12b" };
        var key = new string('b', 32);
        var page = new ArchivedPage
        {
            Url = "https://example.com", Status = PageStatus.Done, ArchivedAt = Now,
            Assets = [new PageAsset { OriginalUrl = "https://example.com/p.jpg", Kind = AssetKind.Image, FileName = key + ".jpg", Bytes = 50_000 }],
        };
        var done = new Dictionary<string, ImageDescription> { [key] = new() { ImageKey = key, SourcePath = key + ".jpg", Status = ImageDescriptionStatus.Done } };

        Assert.False(BundleExporter.IsReady(Email(LinkStatus.Done), withAi, NoImages, Now, [page])); // about to be queued
        Assert.True(BundleExporter.IsReady(Email(LinkStatus.Done), withAi, done, Now, [page]));
        Assert.True(BundleExporter.IsReady(Email(LinkStatus.Done), new InboxSettings(), NoImages, Now, [page]));
    }

    [Fact]
    public void Unprocessed_items_never_go()
    {
        var email = Email(LinkStatus.Done, minutesAgo: 24 * 60);
        email.Status = EmailStatus.Pending;
        Assert.False(BundleExporter.IsReady(email, new InboxSettings(), NoImages, Now));
    }
}

public class ModeSettingsTests
{
    [Theory]
    [InlineData(AppMode.Connected, true, true)]
    [InlineData(AppMode.Collector, true, false)]
    [InlineData(AppMode.AirGapped, false, true)]
    public void What_each_mode_does(AppMode mode, bool usesInternet, bool sendsToNomad)
    {
        var settings = new InboxSettings { Mode = mode };

        Assert.Equal(usesInternet, settings.UsesInternet);
        Assert.Equal(sendsToNomad, settings.SendsToNomad);
    }

    [Fact]
    public async Task Existing_settings_stay_connected_after_the_upgrade()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.Settings.SaveAsync(new InboxSettings { Folder = "Archive" }, null);

        await using (var db = await test.Factory.CreateDbContextAsync())
        {
            var migrator = db.GetService<IMigrator>();
            var migration = db.Database.GetMigrations().Single(m => m.EndsWith("_AirGapped"));
            await migrator.MigrateAsync(db.Database.GetMigrations().TakeWhile(m => m != migration).Last());
            await migrator.MigrateAsync();
        }

        var settings = await test.Settings.GetAsync();
        Assert.Equal("Archive", settings.Folder);
        Assert.Equal(AppMode.Connected, settings.Mode);
        Assert.Equal("collector", settings.CollectorName);
        Assert.Equal(24, settings.AutoExportHours);
        Assert.Equal(6, settings.ExportAiWaitHours);
        Assert.Equal(3500, settings.MaxBundleMegabytes);
        Assert.False(settings.WatchImportFolder);
    }
}
