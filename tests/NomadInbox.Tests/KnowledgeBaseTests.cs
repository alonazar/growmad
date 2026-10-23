using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NomadInbox.Core.Domain;
using NomadInbox.Core.Mail;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.Ingest;
using NomadInbox.Infrastructure.Knowledge;
using NomadInbox.Infrastructure.YouTube;

namespace NomadInbox.Tests;

/// <summary>In-memory stand-in for NOMAD's knowledge base API, behaving like the real one (random suffix, /app prefix).</summary>
public sealed class FakeNomad : INomadKnowledgeBase
{
    public bool Available { get; set; } = true;
    public Dictionary<string, (NomadFile File, string Content)> Files { get; } = [];
    public List<string> Deleted { get; } = [];
    public string NextState { get; set; } = "indexed";
    private int _counter;

    public Task<bool> IsAvailableAsync(string baseUrl, CancellationToken ct) => Task.FromResult(Available);

    public async Task<NomadUpload> UploadAsync(string baseUrl, string fileName, string contentType, Stream content, string? collection, CancellationToken ct)
    {
        using var reader = new StreamReader(content, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(ct);
        var stored = $"{fileName}-{++_counter:x12}{Path.GetExtension(fileName)}";
        var source = $"/app/storage/kb_uploads/{stored}";
        Files[source] = (new NomadFile(source, stored, NextState, 1, collection?.ToLowerInvariant()), text);
        return new NomadUpload(stored, $"/storage/kb_uploads/{stored}");
    }

    public Task<IReadOnlyList<NomadFile>> ListFilesAsync(string baseUrl, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<NomadFile>>(Files.Values.Select(f => f.File).ToList());

    public Task DeleteAsync(string baseUrl, string source, CancellationToken ct)
    {
        Files.Remove(source);
        Deleted.Add(source);
        return Task.CompletedTask;
    }
}

public class KnowledgeDocumentBuilderTests
{
    private static readonly AttachmentStore Store = new(Path.GetTempPath());

    private static InboxEmail Email(string? category = "Survival/Water") => new()
    {
        Id = 1, MessageId = "m@x", Folder = "INBOX", FromAddress = "me@gmail.com", FromName = "Me", Subject = "NOMAD: x",
        Title = "Filters", Category = category, Tags = ["diy"], Status = EmailStatus.Done,
        ReceivedAt = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero),
    };

    private static EmailLink Link(LinkStatus status) => new()
    {
        Id = 7, Url = "https://example.com/guide", Kind = LinkKind.Web, Status = status, Title = "Ceramic filter guide",
        SiteName = "example.com", Summary = "**Clean filters weekly.**\n- Scrub gently", ContentText = "Full article text about scrubbing.",
    };

    [Theory]
    [InlineData("Survival/Water", true, "Survival")]
    [InlineData(null, true, "nomad inbox")]
    [InlineData("Survival/Water", false, "nomad inbox")]
    public void Collections_follow_the_top_level_category(string? category, bool perCategory, string expected)
    {
        var settings = new InboxSettings { KnowledgeCollectionPerCategory = perCategory };

        Assert.Equal(expected, KnowledgeDocumentBuilder.CollectionFor(Email(category), settings));
    }

    [Fact]
    public void Summarized_links_become_markdown_with_metadata_summary_and_full_text()
    {
        var email = Email();
        email.Links.Add(Link(LinkStatus.Done));
        var settings = new InboxSettings { OllamaModel = "gemma3:12b" };

        var doc = Assert.Single(KnowledgeDocumentBuilder.Build(email, settings, Store));
        var text = Encoding.UTF8.GetString(doc.Content!);

        Assert.Equal("link:7", doc.ItemKey);
        Assert.Equal("2026-10-21 Ceramic filter guide.md", doc.FileName);
        Assert.Equal("Survival", doc.Collection);
        Assert.StartsWith("# Ceramic filter guide", text);
        Assert.Contains("- Source: https://example.com/guide", text);
        Assert.Contains("- Filed under: Survival/Water #diy", text);
        Assert.Contains("## Summary\n\n**Clean filters weekly.**", text);
        Assert.Contains("## Full text\n\nFull article text", text);

        settings.KnowledgeIncludeFullText = false;
        var summaryOnly = Encoding.UTF8.GetString(KnowledgeDocumentBuilder.Build(email, settings, Store).Single().Content!);
        Assert.DoesNotContain("Full article text", summaryOnly);
    }

    [Fact]
    public void Links_wait_for_their_summary_unless_the_AI_is_off()
    {
        var email = Email();
        email.Links.Add(Link(LinkStatus.Fetched));

        Assert.Empty(KnowledgeDocumentBuilder.Build(email, new InboxSettings { OllamaModel = "m" }, Store));
        Assert.Single(KnowledgeDocumentBuilder.Build(email, new InboxSettings { SummariesEnabled = false }, Store));
    }

    [Fact]
    public void Ignored_or_unprocessed_emails_contribute_nothing()
    {
        var email = Email();
        email.Links.Add(Link(LinkStatus.Done));
        email.Status = EmailStatus.Ignored;

        Assert.Empty(KnowledgeDocumentBuilder.Build(email, new InboxSettings(), Store));
    }

    [Theory]
    [InlineData("https://a.example/x https://b.example/y", false)]
    [InlineData("Remember to check the seals on both buckets before the next trip.", true)]
    public void Email_note_only_when_the_body_says_something(string body, bool expected)
    {
        Assert.Equal(expected, KnowledgeDocumentBuilder.NoteText(body) is not null);
    }

    [Fact]
    public void Attachments_go_as_files_or_as_extracted_text()
    {
        var root = Directory.CreateTempSubdirectory("kb-files-");
        try
        {
            var store = new AttachmentStore(root.FullName);
            File.WriteAllText(Path.Combine(root.FullName, "guide.pdf"), "%PDF-fake");
            var email = Email();
            email.Attachments.Add(new EmailAttachment { Id = 3, FileName = "guide.pdf", ContentType = "application/pdf", StoredPath = "guide.pdf", Sha256 = "abc" });
            email.Attachments.Add(new EmailAttachment { Id = 4, FileName = "slides.pptx", ContentType = "application/x", StoredPath = "slides.pptx", ExtractedText = "Slide text" });
            email.Attachments.Add(new EmailAttachment { Id = 5, FileName = "tool.zip", ContentType = "application/zip", StoredPath = "tool.zip" });

            var docs = KnowledgeDocumentBuilder.Build(email, new InboxSettings(), store).ToList();

            Assert.Equal(["file:3", "file:4"], docs.Select(d => d.ItemKey));
            Assert.EndsWith("guide.pdf", docs[0].FilePath);
            Assert.Contains("Slide text", Encoding.UTF8.GetString(docs[1].Content!));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}

public sealed class KnowledgeSyncTests : IAsyncLifetime
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
    public async Task Uploads_once_then_tracks_indexing()
    {
        var emailId = await AddEmailWithLinkAsync("First summary");
        _nomad.NextState = "processing";

        var first = await _sync.RunOnceAsync(default);
        var second = await _sync.RunOnceAsync(default);

        Assert.Equal(1, first.Uploaded);
        Assert.Equal(0, second.Uploaded); // unchanged: not sent again
        var doc = await SingleDocAsync();
        Assert.Equal(KnowledgeState.Uploaded, doc.State);
        Assert.StartsWith("/app/storage/kb_uploads/", doc.NomadSource);

        _nomad.Files[doc.NomadSource!] = (_nomad.Files[doc.NomadSource!].File with { State = "indexed", Chunks = 3 }, _nomad.Files[doc.NomadSource!].Content);
        await _sync.RunOnceAsync(default);
        doc = await SingleDocAsync();
        Assert.Equal(KnowledgeState.Indexed, doc.State);
        Assert.Equal(3, doc.Chunks);
        Assert.Equal(emailId, doc.InboxEmailId);
        Assert.Equal("survival", _nomad.Files.Values.Single().File.Collection);
    }

    [Fact]
    public async Task Changed_content_replaces_the_old_document()
    {
        await AddEmailWithLinkAsync("First summary");
        await _sync.RunOnceAsync(default);
        var oldSource = _nomad.Files.Keys.Single(); // NOMAD's id for the first upload

        await using (var db = await _db.Factory.CreateDbContextAsync())
            await db.Links.ExecuteUpdateAsync(u => u.SetProperty(l => l.Summary, "Better summary"));
        await _sync.RunOnceAsync(default);

        Assert.Contains(oldSource!, _nomad.Deleted);
        var remaining = Assert.Single(_nomad.Files.Values);
        Assert.Contains("Better summary", remaining.Content);
    }

    [Fact]
    public async Task Deleting_the_item_removes_its_documents_from_NOMAD()
    {
        var emailId = await AddEmailWithLinkAsync("Summary");
        await _sync.RunOnceAsync(default);
        var deletion = new ItemDeletionService(_db.Factory, new AttachmentStore(Path.GetTempPath()),
            new VideoDownloader(new YtDlp(NullLogger<YtDlp>.Instance), Path.GetTempPath(), NullLogger<VideoDownloader>.Instance),
            NullLogger<ItemDeletionService>.Instance);

        await deletion.DeleteAsync(emailId, deleteFiles: true, default);
        var outcome = await _sync.RunOnceAsync(default);

        Assert.Equal(1, outcome.Removed);
        Assert.Empty(_nomad.Files);
        await using var db = await _db.Factory.CreateDbContextAsync();
        Assert.Empty(await db.KnowledgeDocuments.ToListAsync());
        Assert.True(await db.DeletedMessages.AnyAsync(d => d.MessageId == "m1@x"));
    }

    [Fact]
    public async Task Resend_uploads_again_and_unreachable_NOMAD_waits()
    {
        await AddEmailWithLinkAsync("Summary");
        _nomad.Available = false;
        Assert.Equal(0, (await _sync.RunOnceAsync(default)).Uploaded);

        _nomad.Available = true;
        await _sync.RunOnceAsync(default);
        await _sync.ResendAllAsync(default);
        var again = await _sync.RunOnceAsync(default);

        Assert.Equal(1, again.Uploaded);
        Assert.Single(_nomad.Files); // the previous copy was replaced, not duplicated
    }

    [Fact]
    public async Task Disabled_sends_nothing()
    {
        var settings = await _db.Settings.GetAsync();
        settings.KnowledgeBaseEnabled = false;
        await _db.Settings.SaveAsync(settings, null);
        await AddEmailWithLinkAsync("Summary");

        await _sync.RunOnceAsync(default);

        Assert.Empty(_nomad.Files);
    }

    [Fact]
    public async Task Link_with_a_long_title_is_uploaded()
    {
        await AddEmailWithLinkAsync("Summary", "GitHub - alonazar/growmad: Email-driven intake for a Project NOMAD server. Send mail to a " +
            "Gmail account with a subject and Growmad records it, indexes it by the subject line, files its attachments, collects " +
            "its links, and (in later phases) summarizes those links.");

        var outcome = await _sync.RunOnceAsync(default);

        Assert.Equal(1, outcome.Uploaded);
        var doc = await SingleDocAsync();
        Assert.EndsWith(".md", doc.FileName);
        Assert.True(doc.FileName.Length <= 93, doc.FileName);
    }

    private async Task<int> AddEmailWithLinkAsync(string summary, string title = "Guide")
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        var email = new InboxEmail
        {
            MessageId = "m1@x", Folder = "INBOX", FromAddress = "me@gmail.com", Subject = "NOMAD: x", Title = "Filters",
            Category = "Survival/Water", Status = EmailStatus.Done, ReceivedAt = DateTimeOffset.UtcNow,
        };
        email.Links.Add(new EmailLink
        {
            Url = "https://example.com/guide", Kind = LinkKind.Web, Status = LinkStatus.Done, Title = title,
            Summary = summary, ContentText = "Article",
        });
        db.Emails.Add(email);
        await db.SaveChangesAsync();
        return email.Id;
    }

    private async Task<KnowledgeDocument> SingleDocAsync()
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        return await db.KnowledgeDocuments.SingleAsync();
    }
}

public sealed class DeletionTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("deletion-").FullName;
    private TestDatabase _db = default!;
    private ItemDeletionService _deletion = default!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _deletion = new ItemDeletionService(_db.Factory, new AttachmentStore(Path.Combine(_root, "files")),
            new VideoDownloader(new YtDlp(NullLogger<YtDlp>.Instance), Path.Combine(_root, "videos"), NullLogger<VideoDownloader>.Instance),
            NullLogger<ItemDeletionService>.Instance);
        await _db.Settings.SaveAsync(new InboxSettings { Username = "nomad@gmail.com" }, "pw");
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Removes_unshared_files_and_keeps_shared_ones()
    {
        Directory.CreateDirectory(Path.Combine(_root, "files"));
        Directory.CreateDirectory(Path.Combine(_root, "videos", "Chan"));
        File.WriteAllText(Path.Combine(_root, "files", "own.pdf"), "x");
        File.WriteAllText(Path.Combine(_root, "files", "shared.pdf"), "x");
        foreach (var f in new[] { "v.mp4", "v.nfo", "v-thumb.jpg" }) File.WriteAllText(Path.Combine(_root, "videos", "Chan", f), "x");

        var target = await AddAsync("a@x", ["own.pdf", "shared.pdf"], "Chan/v.mp4");
        await AddAsync("b@x", ["shared.pdf"], null);

        var outcome = await _deletion.DeleteAsync(target, deleteFiles: true, default);

        Assert.Equal(4, outcome.FilesDeleted); // own.pdf + video, .nfo, thumbnail
        Assert.False(File.Exists(Path.Combine(_root, "files", "own.pdf")));
        Assert.True(File.Exists(Path.Combine(_root, "files", "shared.pdf")));
        Assert.False(File.Exists(Path.Combine(_root, "videos", "Chan", "v.nfo")));
    }

    [Fact]
    public async Task Deleted_messages_are_not_imported_again()
    {
        var id = await AddAsync("a@x", [], null);
        await _deletion.DeleteAsync(id, deleteFiles: false, default);
        var reader = new FakeMailboxReader
        {
            NextScan = new MailScanResult(7, 20, false, [new MailHeader(10, "a@x", "me@gmail.com", null, "NOMAD: again", DateTimeOffset.UtcNow)]),
        };
        var ingest = new MailIngestService(_db.Factory, _db.Settings, reader, NullLogger<MailIngestService>.Instance);

        var outcome = await ingest.RunOnceAsync(default);

        Assert.Equal(0, outcome.Added);
        await using var db = await _db.Factory.CreateDbContextAsync();
        Assert.Empty(await db.Emails.ToListAsync());
    }

    private async Task<int> AddAsync(string messageId, string[] files, string? videoPath)
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        var email = new InboxEmail { MessageId = messageId, Folder = "INBOX", FromAddress = "me@gmail.com", Subject = "NOMAD: x", Status = EmailStatus.Done };
        foreach (var f in files)
            email.Attachments.Add(new EmailAttachment { FileName = f, ContentType = "application/pdf", StoredPath = f });
        if (videoPath is not null)
            email.Links.Add(new EmailLink { Url = "https://youtu.be/x", Kind = LinkKind.YouTubeVideo, VideoPath = videoPath });
        db.Emails.Add(email);
        await db.SaveChangesAsync();
        return email.Id;
    }
}
