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

namespace NomadInbox.Tests;

/// <summary>A collector and an air-gapped side, each with its own database and files, passing bundles through a folder.</summary>
public sealed class BundleTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nomad-inbox-tests", Guid.NewGuid().ToString("N"));
    private TestDatabase _collectorDb = default!, _airDb = default!;
    private AttachmentStore _collectorFiles = default!, _airFiles = default!;
    private BundleExporter _exporter = default!;
    private BundleImporter _importer = default!;

    public async Task InitializeAsync()
    {
        _collectorDb = await TestDatabase.CreateAsync();
        _airDb = await TestDatabase.CreateAsync();
        _collectorFiles = new AttachmentStore(Path.Combine(_root, "collector-files"));
        _airFiles = new AttachmentStore(Path.Combine(_root, "air-files"));
        _exporter = new BundleExporter(_collectorDb.Factory, _collectorDb.Settings, _collectorFiles, Path.Combine(_root, "export"),
            NullLogger<BundleExporter>.Instance);
        _importer = new BundleImporter(_airDb.Factory, _airDb.Settings, _airFiles, new KnowledgeTrigger(), new LinkTrigger(), new ImageTrigger(),
            Path.Combine(_root, "import"), NullLogger<BundleImporter>.Instance);
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

        var export = await _exporter.ExportAsync(default);
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
        Assert.Equal(2, (await _exporter.ExportAsync(default))!.Items);

        Assert.Null(await _exporter.ExportAsync(default)); // nothing new

        await SetSummaryAsync(_collectorDb, "m1@x", "Better summary");
        var second = await _exporter.ExportAsync(default);
        Assert.Equal(2, second!.Number);
        Assert.Equal(1, second.Items);
    }

    [Fact]
    public async Task Importing_twice_changes_nothing()
    {
        await AddEmailAsync("m1@x", "First summary");
        var export = await _exporter.ExportAsync(default);

        await ImportAsync(export!.FileName);
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
        var first = await _exporter.ExportAsync(default);
        await SetSummaryAsync(_collectorDb, "m1@x", "New summary");
        var second = await _exporter.ExportAsync(default);

        await ImportAsync(second!.FileName);
        var late = await ImportAsync(first!.FileName);

        Assert.Equal(ImportStatus.Imported, late.Status);
        Assert.Equal(0, late.Items);
        Assert.Equal(1, late.Skipped);
        Assert.Equal("New summary", (await LoadAsync(_airDb, "m1@x")).Links.Single(l => l.Position == 0).Summary);
    }

    [Fact]
    public async Task A_changed_item_replaces_the_earlier_copy()
    {
        await AddEmailAsync("m1@x", "Old summary");
        await ImportAsync((await _exporter.ExportAsync(default))!.FileName);
        await SetSummaryAsync(_collectorDb, "m1@x", "New summary");

        await ImportAsync((await _exporter.ExportAsync(default))!.FileName);

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

        var result = await ImportAsync((await _exporter.ExportAsync(default))!.FileName);

        Assert.Equal(1, result.Skipped);
        await using var check = await _airDb.Factory.CreateDbContextAsync();
        Assert.False(await check.Emails.AnyAsync());
    }

    [Fact]
    public async Task Changed_file_is_rejected_and_nothing_is_imported()
    {
        await AddEmailAsync("m1@x", "Summary");
        var path = BundlePath((await _exporter.ExportAsync(default))!.FileName);
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
        var path = BundlePath((await _exporter.ExportAsync(default))!.FileName);
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
        var export = await _exporter.ExportAsync(default);
        await _airDb.Settings.SaveAsync(new InboxSettings { Mode = AppMode.Connected }, null);
        await _collectorDb.Settings.SaveAsync(new InboxSettings { Mode = AppMode.Connected }, null);

        Assert.Equal(ImportStatus.Rejected, (await ImportAsync(export!.FileName)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _exporter.ExportAsync(default));
    }

    [Fact]
    public async Task Watched_folder_imports_and_moves_bundles()
    {
        await AddEmailAsync("m1@x", "Summary");
        var export = await _exporter.ExportAsync(default);
        Directory.CreateDirectory(_importer.ImportDirectory);
        var dropped = Path.Combine(_importer.ImportDirectory, export!.FileName);
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
        Assert.False(settings.WatchImportFolder);
    }
}
