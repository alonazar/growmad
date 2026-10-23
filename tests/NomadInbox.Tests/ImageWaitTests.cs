using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.Knowledge;

namespace NomadInbox.Tests;

/// <summary>A saved page's document waits for its image descriptions, so it's sent once instead of once per image.</summary>
public class ImageWaitRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly InboxSettings Settings = new() { OllamaModel = "gemma3:12b" };
    private static readonly Dictionary<string, ImageDescription> NoImages = [];

    private static Dictionary<int, ArchivedPage> Page(string fileName = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png", DateTimeOffset? archived = null)
    {
        var page = new ArchivedPage { Id = 1, Url = "https://example.com", Status = PageStatus.Done, SourceLinkId = 7, ArchivedAt = archived ?? Now.AddMinutes(-5) };
        page.Assets.Add(new PageAsset { Id = 1, OriginalUrl = "u", Kind = AssetKind.Image, FileName = fileName, Bytes = 50_000 });
        return new() { [7] = page };
    }

    private static Dictionary<string, ImageDescription> Description(ImageDescriptionStatus status, DateTimeOffset? created = null) => new()
    {
        [new string('a', 32)] = new ImageDescription
        {
            ImageKey = new string('a', 32), SourcePath = "x", Source = ImageSource.PageAsset, Status = status, CreatedAt = created ?? Now.AddMinutes(-2),
        },
    };

    [Fact]
    public void Waits_while_an_image_is_pending()
    {
        Assert.Contains(7, KnowledgeSyncService.LinksWaitingForImages(Page(), Description(ImageDescriptionStatus.Pending), Settings, Now));
    }

    [Theory]
    [InlineData(ImageDescriptionStatus.Done)]
    [InlineData(ImageDescriptionStatus.Failed)]
    [InlineData(ImageDescriptionStatus.Skipped)]
    public void Goes_once_the_images_are_settled(ImageDescriptionStatus status)
    {
        Assert.Empty(KnowledgeSyncService.LinksWaitingForImages(Page(), Description(status), Settings, Now));
    }

    [Fact]
    public void Stops_waiting_after_an_hour()
    {
        var old = Description(ImageDescriptionStatus.Pending, Now - KnowledgeSyncService.MaxImageWait - TimeSpan.FromMinutes(1));
        Assert.Empty(KnowledgeSyncService.LinksWaitingForImages(Page(), old, Settings, Now));
    }

    [Fact]
    public void Waits_for_new_images_that_are_not_queued_yet_but_not_for_unreadable_or_old_ones()
    {
        Assert.Contains(7, KnowledgeSyncService.LinksWaitingForImages(Page(), NoImages, Settings, Now));
        Assert.Empty(KnowledgeSyncService.LinksWaitingForImages(Page("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.svg"), NoImages, Settings, Now));
        Assert.Empty(KnowledgeSyncService.LinksWaitingForImages(Page(archived: Now.AddHours(-3)), NoImages, Settings, Now));

        var startedLater = new InboxSettings { OllamaModel = "gemma3:12b", DescribeImagesFrom = Now.AddMinutes(-1) };
        Assert.Empty(KnowledgeSyncService.LinksWaitingForImages(Page(), NoImages, startedLater, Now));
    }

    [Fact]
    public void No_waiting_when_images_are_not_described()
    {
        var off = new InboxSettings { OllamaModel = "gemma3:12b", DescribeImages = false };
        var noModel = new InboxSettings();

        Assert.Empty(KnowledgeSyncService.LinksWaitingForImages(Page(), Description(ImageDescriptionStatus.Pending), off, Now));
        Assert.Empty(KnowledgeSyncService.LinksWaitingForImages(Page(), Description(ImageDescriptionStatus.Pending), noModel, Now));
    }
}

public sealed class ImageWaitSyncTests : IAsyncLifetime
{
    private readonly FakeNomad _nomad = new();
    private TestDatabase _db = default!;
    private KnowledgeSyncService _sync = default!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _sync = new KnowledgeSyncService(_db.Factory, _db.Settings, _nomad, new AttachmentStore(Path.GetTempPath()),
            NullLogger<KnowledgeSyncService>.Instance);
        await _db.Settings.SaveAsync(new InboxSettings { OllamaModel = "gemma3:12b" }, null);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Saved_page_is_sent_once_after_its_last_image_is_described()
    {
        var keys = new[] { new string('a', 32), new string('b', 32) };
        await AddEmailWithSavedPageAsync(keys);

        Assert.Equal(0, (await _sync.RunOnceAsync(default)).Uploaded); // both images still waiting

        await DescribeAsync(keys[0]);
        Assert.Equal(0, (await _sync.RunOnceAsync(default)).Uploaded); // one still waiting

        await DescribeAsync(keys[1]);
        Assert.Equal(1, (await _sync.RunOnceAsync(default)).Uploaded);
        Assert.Equal(0, (await _sync.RunOnceAsync(default)).Uploaded);

        var text = _nomad.Files.Values.Single().Content;
        Assert.Contains("## Images", text);
        Assert.Contains($"Caption for {keys[0]}", text);
        Assert.Contains($"Caption for {keys[1]}", text);
    }

    private async Task AddEmailWithSavedPageAsync(string[] imageKeys)
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        var email = new InboxEmail
        {
            MessageId = "m1@x", Folder = "INBOX", FromAddress = "me@gmail.com", Subject = "NOMAD: x", Title = "Races",
            Category = "Projects/Running", Status = EmailStatus.Done, ReceivedAt = DateTimeOffset.UtcNow,
        };
        var link = new EmailLink { Url = "https://example.com/races", Kind = LinkKind.Web, Status = LinkStatus.Done, Title = "Races", Summary = "S" };
        email.Links.Add(link);
        db.Emails.Add(email);
        await db.SaveChangesAsync();

        var page = new ArchivedPage { Url = link.Url, Status = PageStatus.Done, SourceLinkId = link.Id, InboxEmailId = email.Id, ArchivedAt = DateTimeOffset.UtcNow };
        foreach (var key in imageKeys)
        {
            page.Assets.Add(new PageAsset { OriginalUrl = $"https://example.com/{key}.png", Kind = AssetKind.Image, FileName = key + ".png", Bytes = 50_000 });
            db.ImageDescriptions.Add(new ImageDescription { ImageKey = key, Source = ImageSource.PageAsset, SourcePath = key + ".png" });
        }
        db.Pages.Add(page);
        await db.SaveChangesAsync();
    }

    private async Task DescribeAsync(string key)
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        var description = await db.ImageDescriptions.SingleAsync(d => d.ImageKey == key);
        description.Status = ImageDescriptionStatus.Done;
        description.Caption = $"Caption for {key}.";
        description.ImageType = "photo";
        await db.SaveChangesAsync();
    }
}
