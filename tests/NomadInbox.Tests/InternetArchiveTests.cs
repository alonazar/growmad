using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Archive;
using NomadInbox.Infrastructure.Bundles;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.Knowledge;
using NomadInbox.Infrastructure.Web;

namespace NomadInbox.Tests;

/// <summary>A fake archive.org on the test web server, with the real fetcher and a real attachment store.</summary>
public sealed class InternetArchiveTests : IAsyncLifetime
{
    private const string Id = "manuals";
    private readonly TestWebServer _server = new();
    private readonly PageFetcher _fetcher = new();
    private readonly string _root = Directory.CreateTempSubdirectory("archive-").FullName;
    private TestDatabase _db = default!;
    private AttachmentStore _store = default!;
    private InternetArchiveImporter _importer = default!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _store = new AttachmentStore(_root);
        _importer = new InternetArchiveImporter(_db.Factory, _db.Settings, _fetcher, _store, new TextExtractor(), new KnowledgeTrigger(), new ArchiveTrigger(),
            NullLogger<InternetArchiveImporter>.Instance, _server.Url("").TrimEnd('/'));
        await _db.Settings.SaveAsync(new InboxSettings { AllowPrivateNetworkLinks = true }, null); // the test server is on 127.0.0.1
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _server.DisposeAsync();
        _fetcher.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("https://archive.org/details/homesteading-survival-manuals", "homesteading-survival-manuals", null)]
    [InlineData("https://archive.org/details/homesteading-survival-manuals/4-h%20Beekeeping/", "homesteading-survival-manuals", "4-h Beekeeping")]
    [InlineData("https://www.archive.org/download/some.item/a%20b.pdf", "some.item", "a b.pdf")]
    [InlineData("  http://archive.org/details/x_1  ", "x_1", null)]
    public void Archive_links_are_understood(string link, string identifier, string? path)
    {
        Assert.Equal((identifier, path), InternetArchiveImporter.ParseLink(link));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a link")]
    [InlineData("https://example.com/details/x")]
    [InlineData("https://archive.org.evil.example/details/x")]
    [InlineData("https://archive.org/search?query=x")]
    [InlineData("https://archive.org/details/")]
    [InlineData("https://archive.org/details/..%2Fx")]
    [InlineData("ftp://archive.org/details/x")]
    public void Other_links_are_refused(string link)
    {
        Assert.Null(InternetArchiveImporter.ParseLink(link));
    }

    [Theory]
    [InlineData("Alpine-Living-2008.pdf", "Alpine Living 2008")]
    [InlineData("4-h Beekeeping.pdf", "4-h Beekeeping")]
    [InlineData("Army FM 21-150 - Hand To Hand Combat.pdf", "Army FM 21-150 - Hand To Hand Combat")]
    [InlineData("folder/some_guide.pdf", "some guide")]
    public void Titles_come_from_file_names(string fileName, string title)
    {
        Assert.Equal(title, InternetArchiveImporter.TitleFor(fileName));
    }

    [Fact]
    public async Task The_preview_lists_only_the_original_PDFs_and_prefers_text_versions_of_scans()
    {
        Publish(("Bee keeping.pdf", "Text PDF", "original", "bees"), ("Scan.pdf", "Image Container PDF", "original", "scan"),
            ("Scan_text.pdf", "Additional Text PDF", "derivative", "scan with text"), ("Bee keeping_djvu.txt", "DjVuTXT", "derivative", "text"),
            ("cover.jpg", "JPEG", "original", "jpeg"));

        var preview = await PreviewAsync();

        Assert.Equal("My manuals", preview.Title);
        Assert.Equal(["Bee keeping.pdf", "Scan.pdf"], preview.Files.Select(f => f.Name));
        Assert.Equal(["Bee keeping.pdf", "Scan_text.pdf"], preview.Files.Select(f => f.DownloadName));
        Assert.Equal([false, true], preview.Files.Select(f => f.Scanned));
        Assert.All(preview.Files, f => Assert.True(f.Suggested));
    }

    [Theory]
    [InlineData("/Bee%20keeping/", "Bee keeping.pdf")]
    [InlineData("/Bee%20keeping.pdf", "Bee keeping.pdf")]
    [InlineData("/Bee%20keeping/page/5/mode/2up", "Bee keeping.pdf")]
    [InlineData("/Other.pdf", "Other.pdf")]
    public async Task A_link_to_one_file_suggests_only_that_file(string path, string expected)
    {
        Publish(("Bee keeping.pdf", "Text PDF", "original", "bees"), ("Other.pdf", "Text PDF", "original", "other"));

        var preview = await PreviewAsync(path);

        Assert.Equal(expected, preview.Files.Single(f => f.Suggested).Name);
        Assert.Equal(2, preview.Files.Count);
    }

    [Fact]
    public async Task Each_PDF_becomes_an_item_with_it_attached()
    {
        Publish(("Bee-keeping_guide.pdf", "Text PDF", "original", "bees"), ("Scan.pdf", "Image Container PDF", "original", "scan"),
            ("Scan_text.pdf", "Additional Text PDF", "derivative", "scan with text"));
        var preview = await PreviewAsync();

        Assert.Equal(2, await _importer.QueueAsync(preview, preview.Files, "Homestead/Manuals", default));
        Assert.Equal(ArchiveStep.Done, await _importer.ImportNextAsync(default));
        Assert.Equal(ArchiveStep.Done, await _importer.ImportNextAsync(default));
        Assert.Equal(ArchiveStep.Idle, await _importer.ImportNextAsync(default));

        await using var db = await _db.Factory.CreateDbContextAsync();
        var items = await db.Emails.Include(e => e.Attachments).OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(["Bee-keeping guide", "Scan"], items.Select(e => e.Title));
        Assert.All(items, e =>
        {
            Assert.Equal((ItemSource.InternetArchive, EmailStatus.Done, "Homestead/Manuals", 1), (e.Source, e.Status, e.Category, e.AttachmentCount));
            Assert.StartsWith("From the Internet Archive: My manuals", e.BodyText);
        });
        Assert.Equal("archive:manuals/Scan.pdf", items[1].MessageId);

        // A scan is stored under its own name, with the searchable version's content.
        var scan = items[1].Attachments.Single();
        Assert.Equal("Scan.pdf", scan.FileName);
        Assert.Equal("scan with text", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(_store.ResolveFullPath(scan.StoredPath!)!)));
        Assert.StartsWith("Homestead/Manuals/", scan.StoredPath);

        Assert.All(await db.ArchiveFiles.ToListAsync(), f => Assert.Equal(ArchiveFileStatus.Done, f.Status));
    }

    [Fact]
    public async Task The_whole_PDF_goes_to_the_knowledge_base_and_the_item_into_bundles()
    {
        Publish(("Guide.pdf", "Text PDF", "original", "bees"));
        await ImportAllAsync();

        await using var db = await _db.Factory.CreateDbContextAsync();
        var email = await db.Emails.Include(e => e.Links).Include(e => e.Attachments).SingleAsync();
        var document = KnowledgeDocumentBuilder.Build(email, new InboxSettings(), _store).Single(d => d.FilePath is not null);
        Assert.Equal("Guide.pdf", document.FileName);
        Assert.Equal("bees", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(document.FilePath!)));
        Assert.True(BundleExporter.IsReady(email, new InboxSettings(), new Dictionary<string, ImageDescription>(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Importing_again_only_brings_what_is_new()
    {
        Publish(("A.pdf", "Text PDF", "original", "a"));
        await ImportAllAsync();

        Publish(("A.pdf", "Text PDF", "original", "a"), ("B.pdf", "Text PDF", "original", "b"));
        var preview = await PreviewAsync();

        Assert.Equal("Imported", preview.Files.Single(f => f.Name == "A.pdf").State);
        Assert.Null(preview.Files.Single(f => f.Name == "B.pdf").State);
        Assert.Equal(1, await _importer.QueueAsync(preview, preview.Files, null, default));
    }

    [Fact]
    public async Task Items_deleted_here_are_not_imported_again()
    {
        Publish(("A.pdf", "Text PDF", "original", "a"));
        await using (var db = await _db.Factory.CreateDbContextAsync())
        {
            db.DeletedMessages.Add(new DeletedMessage { MessageId = InternetArchiveImporter.MessageIdFor(Id, "A.pdf") });
            await db.SaveChangesAsync();
        }

        var preview = await PreviewAsync();

        Assert.Equal("Deleted", preview.Files.Single().State);
        Assert.Equal(0, await _importer.QueueAsync(preview, preview.Files, null, default));
    }

    [Fact]
    public async Task A_failed_download_is_tried_again_then_reported_and_can_be_retried()
    {
        Publish(("A.pdf", "Text PDF", "original", "a"));
        _server.Add($"/download/{Id}/A.pdf", "application/pdf", "gone", status: 503);
        var preview = await PreviewAsync();
        await _importer.QueueAsync(preview, preview.Files, null, default);

        for (var i = 0; i < InternetArchiveImporter.MaxAttempts; i++)
            Assert.Equal(ArchiveStep.Retry, await _importer.ImportNextAsync(default));

        Assert.Equal(ArchiveStep.Idle, await _importer.ImportNextAsync(default));
        await using (var db = await _db.Factory.CreateDbContextAsync())
        {
            var row = await db.ArchiveFiles.SingleAsync();
            Assert.Equal(ArchiveFileStatus.Failed, row.Status);
            Assert.Contains("503", row.Error);
            Assert.False(await db.Emails.AnyAsync());
        }

        Publish(("A.pdf", "Text PDF", "original", "a"));
        Assert.Equal(1, await _importer.RetryFailedAsync(Id, default));
        Assert.Equal(ArchiveStep.Done, await _importer.ImportNextAsync(default));
    }

    [Fact]
    public async Task A_download_that_does_not_match_its_checksum_is_not_kept()
    {
        Publish(("A.pdf", "Text PDF", "original", "a"));
        var preview = await PreviewAsync();
        await _importer.QueueAsync(preview, preview.Files, null, default);
        _server.Add($"/download/{Id}/A.pdf", "application/pdf", "something else");

        Assert.Equal(ArchiveStep.Retry, await _importer.ImportNextAsync(default));

        await using var db = await _db.Factory.CreateDbContextAsync();
        Assert.Contains("checksum", (await db.ArchiveFiles.SingleAsync()).Error);
        Assert.False(await db.Emails.AnyAsync());
    }

    [Fact]
    public async Task A_file_over_the_attachment_limit_fails_without_downloading()
    {
        Publish(("A.pdf", "Text PDF", "original", "a"));
        var settings = await _db.Settings.GetAsync();
        settings.MaxAttachmentMegabytes = 1;
        await _db.Settings.SaveAsync(settings, null);
        var preview = await PreviewAsync();
        await _importer.QueueAsync(preview, [preview.Files[0] with { Bytes = 2 * 1024 * 1024 }], null, default);

        Assert.Equal(ArchiveStep.Done, await _importer.ImportNextAsync(default));

        await using var db = await _db.Factory.CreateDbContextAsync();
        var row = await db.ArchiveFiles.SingleAsync();
        Assert.Equal((ArchiveFileStatus.Failed, 1), (row.Status, row.Attempts));
        Assert.Contains("larger than 1 MB", row.Error);
    }

    [Theory]
    [InlineData("{}", "no collection")]
    [InlineData("""{"is_dark":true,"files":[]}""", "restricted")]
    [InlineData("""{"metadata":{"access-restricted-item":"true"},"files":[]}""", "restricted")]
    [InlineData("""{"metadata":{"title":"x"},"files":[{"name":"a.mp4","source":"original"}]}""", "no PDF")]
    public async Task Collections_that_can_not_be_imported_say_why(string metadata, string expected)
    {
        _server.Add($"/metadata/{Id}", "application/json", metadata);

        var ex = await Assert.ThrowsAsync<ArchiveImportException>(() => PreviewAsync());

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task Nothing_is_fetched_on_the_air_gapped_side()
    {
        Publish(("A.pdf", "Text PDF", "original", "a"));
        var preview = await PreviewAsync();
        await _importer.QueueAsync(preview, preview.Files, null, default);
        await _db.Settings.SaveAsync(new InboxSettings { Mode = AppMode.AirGapped, AllowPrivateNetworkLinks = true }, null);

        Assert.Equal(ArchiveStep.Idle, await _importer.ImportNextAsync(default));
        await Assert.ThrowsAsync<ArchiveImportException>(() => PreviewAsync());
    }

    private Task<ArchivePreview> PreviewAsync(string path = "") => _importer.PreviewAsync($"https://archive.org/details/{Id}{path}", default);

    private async Task ImportAllAsync()
    {
        var preview = await PreviewAsync();
        await _importer.QueueAsync(preview, preview.Files, null, default);
        while (await _importer.ImportNextAsync(default) != ArchiveStep.Idle) { }
    }

    private void Publish(params (string Name, string Format, string Source, string Content)[] files)
    {
        var list = files.Select(f => new Dictionary<string, string>
        {
            ["name"] = f.Name, ["format"] = f.Format, ["source"] = f.Source, ["size"] = f.Content.Length.ToString(),
            ["md5"] = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(f.Content))),
        });
        _server.Add($"/metadata/{Id}", "application/json", JsonSerializer.Serialize(new { metadata = new { title = "My manuals" }, files = list }));
        foreach (var f in files)
            _server.Add($"/download/{Id}/{Uri.EscapeDataString(f.Name)}", "application/pdf", f.Content);
    }
}
