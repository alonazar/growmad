using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NomadInbox.Core.Content;
using NomadInbox.Core.Domain;
using NomadInbox.Core.Mail;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.Ingest;

namespace NomadInbox.Tests;

public sealed class EmailProcessorTests : IAsyncLifetime
{
    private readonly FakeMailboxReader _reader = new();
    private readonly string _filesRoot = Path.Combine(Path.GetTempPath(), "nomad-inbox-tests", Guid.NewGuid().ToString("N"));
    private TestDatabase _db = default!;
    private AttachmentStore _store = default!;
    private EmailProcessor _processor = default!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _store = new AttachmentStore(_filesRoot);
        _processor = new EmailProcessor(_db.Factory, _db.Settings, _reader, _store, new TextExtractor(),
            NullLogger<EmailProcessor>.Instance);

        await _db.Settings.SaveAsync(new InboxSettings { Username = "nomad@gmail.com", MaxAttachmentMegabytes = 1 }, "pw");
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        if (Directory.Exists(_filesRoot)) Directory.Delete(_filesRoot, recursive: true);
    }

    [Fact]
    public async Task Processes_body_links_and_attachments_then_labels()
    {
        var id = await AddEmailAsync("m1@x", category: "Survival/Water");
        _reader.Session.Messages["m1@x"] = new FetchedMessage(10,
            "See https://youtu.be/dQw4w9WgXcQ and https://example.com/a?utm_source=x",
            null,
            [
                new FetchedAttachment("notes.txt", "text/plain", "boil it"u8.ToArray()),
                new FetchedAttachment("huge.bin", "application/octet-stream", new byte[2 * 1024 * 1024]),
            ]);

        var outcome = await _processor.ProcessPendingAsync(default);

        Assert.Equal(new ProcessingOutcome(1, 0), outcome);
        var email = await LoadAsync(id);
        Assert.Equal(EmailStatus.Done, email.Status);
        Assert.StartsWith("See https://youtu.be", email.BodyText);
        Assert.Equal(
            [(LinkKind.YouTubeVideo, "https://www.youtube.com/watch?v=dQw4w9WgXcQ"), (LinkKind.Web, "https://example.com/a")],
            email.Links.OrderBy(l => l.Position).Select(l => (l.Kind, l.Url)));
        Assert.Equal(2, email.LinkCount);
        Assert.Equal(2, email.AttachmentCount);

        var notes = email.Attachments.Single(a => a.FileName == "notes.txt");
        Assert.StartsWith("Survival/Water/", notes.StoredPath);
        Assert.True(_store.Exists(notes.StoredPath!));
        Assert.Equal("boil it", notes.ExtractedText);

        var huge = email.Attachments.Single(a => a.FileName == "huge.bin");
        Assert.Null(huge.StoredPath);
        Assert.Contains("larger than 1 MB", huge.Note);

        Assert.Equal([(10u, "NOMAD/Processed")], _reader.Session.Labels);
    }

    [Fact]
    public async Task Identical_attachment_in_another_email_is_marked_duplicate()
    {
        await AddEmailAsync("m1@x");
        await AddEmailAsync("m2@x");
        var file = new FetchedAttachment("map.pdf", "application/pdf", "same bytes"u8.ToArray());
        _reader.Session.Messages["m1@x"] = new FetchedMessage(1, "", null, [file]);
        _reader.Session.Messages["m2@x"] = new FetchedMessage(2, "", null, [file with { FileName = "renamed.pdf" }]);

        await _processor.ProcessPendingAsync(default);

        await using var db = await _db.Factory.CreateDbContextAsync();
        var attachments = await db.Attachments.OrderBy(a => a.Id).ToListAsync();
        Assert.False(attachments[0].IsDuplicate);
        Assert.True(attachments[1].IsDuplicate);
        Assert.Equal(attachments[0].StoredPath, attachments[1].StoredPath);
    }

    [Fact]
    public async Task Missing_message_fails_and_gets_failed_label()
    {
        var id = await AddEmailAsync("gone@x");

        var outcome = await _processor.ProcessPendingAsync(default);

        Assert.Equal(new ProcessingOutcome(0, 1), outcome);
        var email = await LoadAsync(id);
        Assert.Equal(EmailStatus.Failed, email.Status);
        Assert.Contains("no longer in the mailbox", email.StatusReason);
        Assert.Equal(1, email.Attempts);
        Assert.Equal("NOMAD/Failed", _reader.Session.Labels.Single().Label);
    }

    [Fact]
    public async Task Reprocessing_replaces_links_and_attachments()
    {
        var id = await AddEmailAsync("m1@x");
        _reader.Session.Messages["m1@x"] = new FetchedMessage(1, "https://a.example/", null,
            [new FetchedAttachment("a.txt", "text/plain", "a"u8.ToArray())]);
        await _processor.ProcessPendingAsync(default);

        await using (var db = await _db.Factory.CreateDbContextAsync())
            await db.Emails.ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, EmailStatus.Pending));
        _reader.Session.Messages["m1@x"] = new FetchedMessage(1, "https://b.example/", null,
            [new FetchedAttachment("a.txt", "text/plain", "a"u8.ToArray())]);
        await _processor.ProcessPendingAsync(default);

        var email = await LoadAsync(id);
        Assert.Equal(["https://b.example/"], email.Links.Select(l => l.Url));
        var attachment = Assert.Single(email.Attachments);
        Assert.False(attachment.IsDuplicate); // its own earlier copy doesn't count
        Assert.Equal(2, email.Attempts);
    }

    [Fact]
    public async Task Labels_are_skipped_when_turned_off()
    {
        var settings = await _db.Settings.GetAsync();
        settings.ApplyLabels = false;
        await _db.Settings.SaveAsync(settings, null);
        await AddEmailAsync("m1@x");
        _reader.Session.Messages["m1@x"] = new FetchedMessage(1, "hi", null, []);

        await _processor.ProcessPendingAsync(default);

        Assert.Empty(_reader.Session.Labels);
    }

    private async Task<int> AddEmailAsync(string messageId, string? category = null)
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        var email = new InboxEmail
        {
            MessageId = messageId,
            ImapUid = 1,
            UidValidity = 7,
            Folder = "INBOX",
            FromAddress = "me@gmail.com",
            Subject = "NOMAD: test",
            ReceivedAt = DateTimeOffset.UtcNow,
            Category = category,
        };
        db.Emails.Add(email);
        await db.SaveChangesAsync();
        return email.Id;
    }

    [Fact]
    public async Task Programs_are_listed_but_not_stored_by_default()
    {
        var id = await AddEmailAsync("m1@x");
        _reader.Session.Messages["m1@x"] = new FetchedMessage(1, "", null,
        [
            new FetchedAttachment("setup.exe", "application/octet-stream", AttachmentClassifierTests.WindowsProgram()),
            new FetchedAttachment("photo.jpg", "image/jpeg", "jpeg bytes"u8.ToArray()),
        ]);

        await _processor.ProcessPendingAsync(default);

        var email = await LoadAsync(id);
        var program = email.Attachments.Single(a => a.FileName == "setup.exe");
        Assert.Null(program.StoredPath);
        Assert.Contains("programs and scripts aren't allowed", program.Note);
        Assert.NotNull(email.Attachments.Single(a => a.FileName == "photo.jpg").StoredPath);
    }

    [Fact]
    public async Task Allowed_programs_are_stored_inside_a_zip()
    {
        await AllowAsync(InboxSettings.DefaultAttachmentKinds | AttachmentKinds.Executables);
        var id = await AddEmailAsync("m1@x", category: "Tools");
        var program = AttachmentClassifierTests.WindowsProgram();
        _reader.Session.Messages["m1@x"] = new FetchedMessage(1, "", null, [new FetchedAttachment("setup.exe", "application/octet-stream", program)]);

        await _processor.ProcessPendingAsync(default);

        var file = (await LoadAsync(id)).Attachments.Single();
        Assert.True(file.StoredAsZip);
        Assert.Equal("setup.exe", file.FileName);
        Assert.StartsWith("Tools/", file.StoredPath);
        Assert.EndsWith("/setup.exe.zip", file.StoredPath);
        Assert.Equal(AttachmentStore.ComputeSha256(program), file.Sha256);
        Assert.Null(file.ExtractedText);

        using var zip = ZipFile.OpenRead(_store.ResolveFullPath(file.StoredPath!)!);
        var entry = Assert.Single(zip.Entries);
        Assert.Equal("setup.exe", entry.FullName);
        using var content = new MemoryStream();
        entry.Open().CopyTo(content);
        Assert.Equal(program, content.ToArray());
    }

    [Fact]
    public async Task Same_program_in_another_email_reuses_the_zip()
    {
        await AllowAsync(InboxSettings.DefaultAttachmentKinds | AttachmentKinds.Executables);
        await AddEmailAsync("m1@x");
        var second = await AddEmailAsync("m2@x");
        var file = new FetchedAttachment("setup.exe", "application/octet-stream", AttachmentClassifierTests.WindowsProgram());
        _reader.Session.Messages["m1@x"] = new FetchedMessage(1, "", null, [file]);
        _reader.Session.Messages["m2@x"] = new FetchedMessage(2, "", null, [file with { FileName = "copy.exe" }]);

        await _processor.ProcessPendingAsync(default);

        var copy = (await LoadAsync(second)).Attachments.Single();
        Assert.True(copy.IsDuplicate);
        Assert.True(copy.StoredAsZip);
        Assert.EndsWith("setup.exe.zip", copy.StoredPath);
    }

    [Fact]
    public async Task Kinds_that_are_turned_off_are_not_stored()
    {
        await AllowAsync(InboxSettings.DefaultAttachmentKinds & ~AttachmentKinds.Video);
        var id = await AddEmailAsync("m1@x");
        _reader.Session.Messages["m1@x"] = new FetchedMessage(1, "", null,
        [
            new FetchedAttachment("clip.mp4", "video/mp4", "video"u8.ToArray()),
            new FetchedAttachment("notes.txt", "text/plain", "text"u8.ToArray()),
        ]);

        await _processor.ProcessPendingAsync(default);

        var email = await LoadAsync(id);
        var clip = email.Attachments.Single(a => a.FileName == "clip.mp4");
        Assert.Null(clip.StoredPath);
        Assert.Contains("videos aren't allowed", clip.Note);
        Assert.NotNull(email.Attachments.Single(a => a.FileName == "notes.txt").StoredPath);
    }

    private async Task AllowAsync(AttachmentKinds kinds)
    {
        var settings = await _db.Settings.GetAsync();
        settings.AllowedAttachmentKinds = kinds;
        await _db.Settings.SaveAsync(settings, null);
    }

    private async Task<InboxEmail> LoadAsync(int id)
    {
        await using var db = await _db.Factory.CreateDbContextAsync();
        return await db.Emails.Include(e => e.Links).Include(e => e.Attachments).SingleAsync(e => e.Id == id);
    }
}
