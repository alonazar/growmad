using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.Images;
using NomadInbox.Infrastructure.Knowledge;
using NomadInbox.Infrastructure.Pages;

namespace NomadInbox.Tests;

public class ImageAnswerTests
{
    private static readonly string[] Categories = ["IT/Security", "Survival/Water"];

    [Fact]
    public void Answer_is_read_and_kept_to_the_lists()
    {
        var facts = ImageDescriber.Parse("""
            {"caption":" A router with blinking lights. ","type":"Photo","categories":["it/security","Cooking","Survival/Water","IT/Security"],
             "tags":["Router","network","router"],"text":"ASUS RT-AX88U"}
            """, Categories);

        Assert.Equal("A router with blinking lights.", facts.Caption);
        Assert.Equal("photo", facts.ImageType);
        Assert.Equal(["IT/Security", "Survival/Water"], facts.Categories); // canonical names, unknown dropped, no repeats
        Assert.Equal(["router", "network"], facts.Tags);
        Assert.Equal("ASUS RT-AX88U", facts.Text);
    }

    [Fact]
    public void Unknown_type_becomes_other_and_empty_text_is_null()
    {
        var facts = ImageDescriber.Parse("""```json {"caption":"A drawing.","type":"meme","categories":[],"tags":[],"text":""} ```""", Categories);

        Assert.Equal("other", facts.ImageType);
        Assert.Null(facts.Text);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"caption":"","type":"photo","categories":[],"tags":[],"text":""}""")]
    public void Unusable_answers_are_errors(string answer)
    {
        Assert.Throws<FormatException>(() => ImageDescriber.Parse(answer, Categories));
    }

    [Fact]
    public void Prompt_offers_the_known_categories()
    {
        Assert.Contains("chosen only from this list: IT/Security; Survival/Water.", ImageDescriber.Prompt(Categories));
        Assert.Contains("always an empty list", ImageDescriber.Prompt([]));
    }

    [Theory]
    [InlineData("photo.JPG", null, true, true)]
    [InlineData("scan", "image/png", true, false)]
    [InlineData("logo.svg", "image/svg+xml", true, false)]
    [InlineData("IMG_1.heic", null, true, false)]
    [InlineData("map.pdf", "application/pdf", false, false)]
    public void Images_and_readable_formats(string name, string? contentType, bool image, bool readable)
    {
        Assert.Equal(image, ImageDescriber.IsImage(name, contentType));
        Assert.Equal(readable, ImageDescriber.CanRead(name));
    }

    [Fact]
    public void Links_back_need_a_valid_address()
    {
        Assert.Equal("http://localhost:8601/emails/3", new InboxLinks("http://localhost:8601/").Email(3));
        Assert.Null(new InboxLinks("localhost:8601").Email(3));
        Assert.Null(InboxLinks.None.Attachment(1));
        Assert.Equal("http://nomad.lan:8601/pages/assets/ab.png", new InboxLinks("http://nomad.lan:8601").PageAsset("ab.png"));
    }
}

public sealed class ImageDescriberTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nomad-inbox-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeOllama _ollama = new();
    private TestDatabase _db = default!;
    private AttachmentStore _store = default!;
    private PageStore _pages = default!;
    private ImageDescriber _describer = default!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _store = new AttachmentStore(Path.Combine(_root, "files"));
        _pages = new PageStore(Path.Combine(_root, "data"), Path.Combine(_root, "export"));
        _describer = new ImageDescriber(_db.Factory, _db.Settings, _ollama, _store, _pages, new KnowledgeTrigger(),
            NullLogger<ImageDescriber>.Instance);
        await _db.Settings.SaveAsync(new InboxSettings { OllamaModel = "gemma3:12b" }, null);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Image_attachments_are_queued_and_other_files_are_not()
    {
        await AddEmailAsync("m1@x", ("photo.jpg", "image/jpeg"), ("logo.svg", "image/svg+xml"), ("manual.pdf", "application/pdf"));

        Assert.Equal(2, await _describer.QueueNewAsync(default));
        Assert.Equal(0, await _describer.QueueNewAsync(default)); // nothing new the second time

        var rows = await DescriptionsAsync();
        Assert.Equal(ImageDescriptionStatus.Pending, rows.Single(r => r.SourcePath.EndsWith("photo.jpg")).Status);
        var svg = rows.Single(r => r.SourcePath.EndsWith("logo.svg"));
        Assert.Equal(ImageDescriptionStatus.Skipped, svg.Status);
        Assert.Contains("SVG", svg.Error);
    }

    [Fact]
    public async Task Only_images_after_the_start_date_are_queued_until_existing_ones_are_asked_for()
    {
        await AddEmailAsync("old@x", DateTimeOffset.UtcNow.AddDays(-2), [("old.png", "image/png")]);
        await SetAsync(s => s.DescribeImagesFrom = DateTimeOffset.UtcNow.AddDays(-1));
        await AddEmailAsync("new@x", DateTimeOffset.UtcNow, [("new.png", "image/png")]);

        Assert.Equal(1, await _describer.QueueNewAsync(default));
        await _describer.DescribeExistingAsync(default);
        Assert.Equal(1, await _describer.QueueNewAsync(default));
        Assert.Null((await _db.Settings.GetAsync()).DescribeImagesFrom);
    }

    [Fact]
    public async Task Saved_pages_queue_their_first_images_without_icons()
    {
        await SetAsync(s => s.MaxImagesPerPage = 2);
        await AddPageAsync(images: [6000, 100, 7000, 8000]); // the 100-byte one is an icon

        Assert.Equal(2, await _describer.QueueNewAsync(default));
        Assert.All(await DescriptionsAsync(), r => Assert.Equal(ImageSource.PageAsset, r.Source));
    }

    [Fact]
    public async Task Describes_an_image_with_the_summary_model()
    {
        await AddEmailAsync("m1@x", ("photo.jpg", "image/jpeg"));
        await AddEmailAsync("m2@x", category: "Survival/Water");
        await _describer.QueueNewAsync(default);

        Assert.True(await _describer.DescribeNextAsync(default));
        Assert.False(await _describer.DescribeNextAsync(default)); // nothing left

        var row = Assert.Single(await DescriptionsAsync());
        Assert.Equal(ImageDescriptionStatus.Done, row.Status);
        Assert.Equal("A water filter on a table.", row.Caption);
        Assert.Equal(["Survival/Water"], row.Categories);
        Assert.Equal(["filter", "ceramic"], row.Tags);
        Assert.Equal("gemma3:12b", row.Model);
        var call = Assert.Single(_ollama.ImageCalls);
        Assert.Equal("gemma3:12b", call.Model);
        Assert.Contains("Survival/Water", call.Prompt);
        Assert.Equal(Encoding.UTF8.GetBytes("bytes of photo.jpg"), call.Image);
    }

    [Fact]
    public async Task Vision_model_setting_is_used_when_set()
    {
        await SetAsync(s => s.VisionModel = "llava:7b");
        await AddEmailAsync("m1@x", ("photo.jpg", "image/jpeg"));
        await _describer.QueueNewAsync(default);

        await _describer.DescribeNextAsync(default);

        Assert.Equal("llava:7b", Assert.Single(_ollama.ImageCalls).Model);
    }

    [Fact]
    public async Task Waits_without_using_an_attempt_while_Ollama_is_down()
    {
        await AddEmailAsync("m1@x", ("photo.jpg", "image/jpeg"));
        await _describer.QueueNewAsync(default);
        _ollama.Up = false;

        Assert.False(await _describer.DescribeNextAsync(default));

        var row = Assert.Single(await DescriptionsAsync());
        Assert.Equal(ImageDescriptionStatus.Pending, row.Status);
        Assert.Equal(0, row.Attempts);
    }

    [Fact]
    public async Task Bad_answers_are_retried_then_marked_failed_and_can_be_redescribed()
    {
        await AddEmailAsync("m1@x", ("photo.jpg", "image/jpeg"));
        await _describer.QueueNewAsync(default);
        _ollama.ImageReply = (_, _) => "I can't do that.";

        for (var i = 0; i < ImageDescriber.MaxAttempts; i++) await _describer.DescribeNextAsync(default);

        var row = Assert.Single(await DescriptionsAsync());
        Assert.Equal(ImageDescriptionStatus.Failed, row.Status);
        Assert.Equal(ImageDescriber.MaxAttempts, row.Attempts);
        Assert.Contains("wasn't JSON", row.Error);

        await _describer.RedescribeAsync(row.ImageKey, default);
        row = Assert.Single(await DescriptionsAsync());
        Assert.Equal(ImageDescriptionStatus.Pending, row.Status);
        Assert.Equal(0, row.Attempts);
    }

    [Fact]
    public async Task Missing_file_fails_at_once()
    {
        var id = await AddEmailAsync("m1@x", ("photo.jpg", "image/jpeg"));
        await _describer.QueueNewAsync(default);
        File.Delete(_store.ResolveFullPath((await DescriptionsAsync()).Single().SourcePath)!);

        Assert.True(await _describer.DescribeNextAsync(default));

        Assert.Equal(ImageDescriptionStatus.Failed, (await DescriptionsAsync()).Single().Status);
        Assert.Empty(_ollama.ImageCalls);
    }

    [Fact]
    public async Task Nothing_happens_when_describing_is_off()
    {
        await SetAsync(s => s.DescribeImages = false);
        await AddEmailAsync("m1@x", ("photo.jpg", "image/jpeg"));

        Assert.Equal(0, await _describer.QueueNewAsync(default));
        Assert.False(await _describer.DescribeNextAsync(default));
    }

    private async Task SetAsync(Action<InboxSettings> change)
    {
        var settings = await _db.Settings.GetAsync();
        change(settings);
        await _db.Settings.SaveAsync(settings, null);
    }

    private Task<int> AddEmailAsync(string messageId, params (string Name, string Type)[] files) =>
        AddEmailAsync(messageId, DateTimeOffset.UtcNow, files);

    private Task<int> AddEmailAsync(string messageId, string category) =>
        AddEmailAsync(messageId, DateTimeOffset.UtcNow, [], category);

    private async Task<int> AddEmailAsync(string messageId, DateTimeOffset when, (string Name, string Type)[] files, string? category = null)
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        var email = new InboxEmail
        {
            MessageId = messageId, Folder = "INBOX", FromAddress = "me@gmail.com", Subject = "NOMAD: x", Category = category,
            Status = EmailStatus.Done, ReceivedAt = when, ProcessedAt = when,
        };
        foreach (var (name, type) in files)
        {
            var content = Encoding.UTF8.GetBytes($"bytes of {name}");
            var sha = AttachmentStore.ComputeSha256(content);
            var path = await _store.SaveAsync("Tools", when, name, content, sha, default);
            email.Attachments.Add(new EmailAttachment { FileName = name, ContentType = type, SizeBytes = content.Length, Sha256 = sha, StoredPath = path });
        }
        db.Emails.Add(email);
        await db.SaveChangesAsync();
        return email.Id;
    }

    private async Task AddPageAsync(int[] images)
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        var page = new ArchivedPage { Url = "https://example.com/guide", Status = PageStatus.Done, ArchivedAt = DateTimeOffset.UtcNow };
        foreach (var size in images)
        {
            var name = await _pages.SaveAssetAsync(RandomNumberGenerator.GetBytes(size), "image/png", "https://example.com/i.png", default);
            page.Assets.Add(new PageAsset { OriginalUrl = "https://example.com/i.png", Kind = AssetKind.Image, FileName = name, Bytes = size });
        }
        db.Pages.Add(page);
        await db.SaveChangesAsync();
    }

    private async Task<List<ImageDescription>> DescriptionsAsync()
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        return await db.ImageDescriptions.AsNoTracking().ToListAsync();
    }
}

public class ImageKnowledgeTests
{
    private static readonly AttachmentStore Store = new(Path.GetTempPath());

    private static InboxEmail Email() => new()
    {
        Id = 4, MessageId = "m@x", Folder = "INBOX", FromAddress = "me@gmail.com", Subject = "NOMAD: x", Title = "Router setup",
        Category = "IT/Security", Status = EmailStatus.Done, ReceivedAt = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero),
    };

    private static ImageDescription Described(string sha) => new()
    {
        ImageKey = ImageDescription.KeyFor(sha), Source = ImageSource.Attachment, SourcePath = "x", Status = ImageDescriptionStatus.Done,
        Caption = "A router on a shelf.", ImageType = "photo", Categories = ["IT/Security"], Tags = ["router", "wifi"], ImageText = "RT-AX88U",
    };

    [Fact]
    public void Described_image_attachment_becomes_an_image_card_with_links()
    {
        var sha = new string('a', 64);
        var email = Email();
        email.Attachments.Add(new EmailAttachment { Id = 9, FileName = "router.jpg", ContentType = "image/jpeg", Sha256 = sha, StoredPath = "IT/router.jpg" });
        var context = new KnowledgeContext(new Dictionary<string, ImageDescription> { [ImageDescription.KeyFor(sha)] = Described(sha) }, new Dictionary<int, ArchivedPage>());
        var settings = new InboxSettings { PublicBaseUrl = "http://localhost:8601" };

        var doc = Assert.Single(KnowledgeDocumentBuilder.Build(email, settings, Store, context));
        var text = Encoding.UTF8.GetString(doc.Content!);

        Assert.Equal("file:9", doc.ItemKey);
        Assert.Equal("router.jpg.md", doc.FileName);
        Assert.Contains("# Image: router.jpg", text);
        Assert.Contains("- Type: photo", text);
        Assert.Contains("- Categories: IT/Security", text);
        Assert.Contains("- Tags: router, wifi", text);
        Assert.Contains("- Local copy: [router.jpg](http://localhost:8601/attachments/9)", text);
        Assert.Contains("- In NOMAD Inbox: [the email](http://localhost:8601/emails/4)", text);
        Assert.Contains("A router on a shelf.", text);
        Assert.Contains("## Text in the image\n\nRT-AX88U", text);
    }

    [Fact]
    public void Image_without_a_description_is_sent_as_before()
    {
        var root = Path.Combine(Path.GetTempPath(), "nomad-inbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "router.jpg");
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            var email = Email();
            email.Attachments.Add(new EmailAttachment { Id = 9, FileName = "router.jpg", ContentType = "image/jpeg", Sha256 = new string('b', 64), StoredPath = "router.jpg" });

            var doc = Assert.Single(KnowledgeDocumentBuilder.Build(email, new InboxSettings(), new AttachmentStore(root)));

            Assert.Null(doc.Content);
            Assert.Equal(path, doc.FilePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Saved_page_lists_its_described_images_and_links_to_the_copy()
    {
        var email = Email();
        email.Links.Add(new EmailLink { Id = 7, Url = "https://example.com/guide", Kind = LinkKind.Web, Status = LinkStatus.Done, Title = "Guide", Summary = "S" });
        var key = new string('c', 32);
        var page = new ArchivedPage { Id = 12, Url = "https://example.com/guide", Status = PageStatus.Done, SourceLinkId = 7 };
        page.Assets.Add(new PageAsset { Id = 1, OriginalUrl = "u", Kind = AssetKind.Image, FileName = key + ".png" });
        var image = Described(key + new string('0', 32));
        var context = new KnowledgeContext(new Dictionary<string, ImageDescription> { [key] = image }, new Dictionary<int, ArchivedPage> { [7] = page });

        var doc = Assert.Single(KnowledgeDocumentBuilder.Build(email, new InboxSettings { OllamaModel = "m", PublicBaseUrl = "http://localhost:8601" }, Store, context));
        var text = Encoding.UTF8.GetString(doc.Content!);

        Assert.Contains("- Saved copy: [Guide](http://localhost:8601/pages/12)", text);
        Assert.Contains("## Images", text);
        Assert.Contains($"- A router on a shelf. (photo; IT/Security; tags: router, wifi) [image](http://localhost:8601/pages/assets/{key}.png)", text);
        Assert.Contains("  Text in the image: RT-AX88U", text);
    }

    [Fact]
    public void No_address_means_no_links()
    {
        var email = Email();
        email.Links.Add(new EmailLink { Id = 7, Url = "https://example.com/guide", Kind = LinkKind.Web, Status = LinkStatus.Done, Title = "Guide", Summary = "S" });

        var text = Encoding.UTF8.GetString(KnowledgeDocumentBuilder.Build(email, new InboxSettings { OllamaModel = "m" }, Store).Single().Content!);

        Assert.DoesNotContain("NOMAD Inbox:", text);
        Assert.DoesNotContain("http://localhost", text);
    }
}

public class ImageSettingsTests
{
    [Fact]
    public void Defaults_describe_images_with_the_summary_model()
    {
        var settings = new InboxSettings { OllamaModel = "gemma3:12b" };

        Assert.True(settings.DescribeImages);
        Assert.Equal(10, settings.MaxImagesPerPage);
        Assert.Equal("gemma3:12b", settings.ImageModel);
        Assert.True(settings.IsImageAiConfigured);
        Assert.Null(settings.DescribeImagesFrom);
        Assert.Null(settings.PublicBaseUrl);
    }

    [Fact]
    public async Task Existing_settings_start_describing_from_the_upgrade()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.Settings.SaveAsync(new InboxSettings { Folder = "Archive" }, null);
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        await using (var db = await test.Factory.CreateDbContextAsync())
        {
            var migrator = db.GetService<IMigrator>();
            var migration = db.Database.GetMigrations().Single(m => m.EndsWith("_ImageDescriptions"));
            await migrator.MigrateAsync(db.Database.GetMigrations().TakeWhile(m => m != migration).Last());
            await migrator.MigrateAsync();
        }

        var settings = await test.Settings.GetAsync();
        Assert.Equal("Archive", settings.Folder);
        Assert.True(settings.DescribeImages);
        Assert.Equal(10, settings.MaxImagesPerPage);
        Assert.NotNull(settings.DescribeImagesFrom);
        Assert.True(settings.DescribeImagesFrom >= before && settings.DescribeImagesFrom <= DateTimeOffset.UtcNow.AddSeconds(5));
    }
}
