using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NomadInbox.Core.Domain;
using NomadInbox.Core.Mail;
using NomadInbox.Infrastructure.Ingest;

namespace NomadInbox.Tests;

public sealed class MailIngestServiceTests : IAsyncLifetime
{
    private readonly FakeMailboxReader _reader = new();
    private TestDatabase _db = default!;
    private MailIngestService _ingest = default!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _ingest = new MailIngestService(_db.Factory, _db.Settings, _reader, NullLogger<MailIngestService>.Instance);

        await _db.Settings.SaveAsync(new InboxSettings
        {
            Username = "nomad@gmail.com",
            AllowedSenders = "me@gmail.com",
        }, "abcd efgh ijkl mnop");
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Records_matches_and_marks_unknown_senders_ignored()
    {
        _reader.NextScan = Scan(uidValidity: 7, highest: 20,
            Header(10, "a@x", "me@gmail.com", "NOMAD: Survival/Water #diy Filters"),
            Header(11, "b@x", "stranger@spam.com", "NOMAD: buy now"));

        var outcome = await _ingest.RunOnceAsync(default);

        Assert.Equal(new IngestOutcome(2, 1, 1, 0), outcome);
        await using var db = await _db.Factory.CreateDbContextAsync();
        var emails = await db.Emails.OrderBy(e => e.ImapUid).ToListAsync();
        Assert.Equal(EmailStatus.Pending, emails[0].Status);
        Assert.Equal(7u, emails[0].UidValidity);
        Assert.Equal("Survival/Water", emails[0].Category);
        Assert.Equal(["diy"], emails[0].Tags);
        Assert.Equal("Filters", emails[0].Title);
        Assert.Equal(EmailStatus.Ignored, emails[1].Status);
    }

    [Fact]
    public async Task Advances_cursor_and_passes_it_to_next_scan()
    {
        _reader.NextScan = Scan(uidValidity: 7, highest: 20, Header(10, "a@x", "me@gmail.com", "NOMAD: one"));
        await _ingest.RunOnceAsync(default);

        _reader.NextScan = Scan(uidValidity: 7, highest: 25);
        await _ingest.RunOnceAsync(default);

        Assert.Equal(7u, _reader.LastRequest!.KnownUidValidity);
        Assert.Equal(20u, _reader.LastRequest.LastSeenUid);
        await using var db = await _db.Factory.CreateDbContextAsync();
        Assert.Equal(25u, (await db.Cursors.SingleAsync()).LastSeenUid);
    }

    [Fact]
    public async Task Skips_messages_already_recorded()
    {
        _reader.NextScan = Scan(7, 20, Header(10, "a@x", "me@gmail.com", "NOMAD: one"));
        await _ingest.RunOnceAsync(default);

        // e.g. after a UIDVALIDITY reset the same message comes back with a new UID
        _reader.NextScan = Scan(8, 30, Header(3, "a@x", "me@gmail.com", "NOMAD: one"), Header(4, "c@x", "me@gmail.com", "NOMAD: two"));
        var outcome = await _ingest.RunOnceAsync(default);

        Assert.Equal(new IngestOutcome(2, 1, 0, 1), outcome);
        await using var db = await _db.Factory.CreateDbContextAsync();
        Assert.Equal(2, await db.Emails.CountAsync());
    }

    [Fact]
    public async Task Reset_cursor_starts_from_scratch()
    {
        _reader.NextScan = Scan(7, 20);
        await _ingest.RunOnceAsync(default);

        await _ingest.ResetCursorAsync("INBOX", default);
        _reader.NextScan = Scan(7, 20);
        await _ingest.RunOnceAsync(default);

        Assert.Equal(0u, _reader.LastRequest!.LastSeenUid);
    }

    [Fact]
    public async Task Stored_password_is_encrypted_and_gmail_spaces_removed()
    {
        var stored = await _db.Settings.GetAsync();

        Assert.DoesNotContain("abcd", stored.ProtectedPassword);
        Assert.Equal("abcdefghijklmnop", _db.Settings.CreateAccount(stored)!.Password);
    }

    [Fact]
    public async Task New_settings_columns_get_their_defaults()
    {
        var stored = await _db.Settings.GetAsync();

        Assert.True(stored.ApplyLabels);
        Assert.Equal("NOMAD/Processed", stored.ProcessedLabel);
        Assert.Equal(50, stored.MaxAttachmentMegabytes);
    }

    private static MailHeader Header(uint uid, string messageId, string from, string subject) =>
        new(uid, messageId, from, null, subject, DateTimeOffset.UtcNow);

    private static MailScanResult Scan(uint uidValidity, uint highest, params MailHeader[] matches) =>
        new(uidValidity, highest, false, matches);
}
