using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NomadInbox.Core.Mail;
using NomadInbox.Infrastructure.Data;
using NomadInbox.Infrastructure.Settings;

namespace NomadInbox.Tests;

/// <summary>Migrated in-memory SQLite database plus a SettingsService, shared by the service tests.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public IDbContextFactory<NomadInboxDbContext> Factory { get; private set; } = default!;
    public SettingsService Settings { get; private set; } = default!;

    public static async Task<TestDatabase> CreateAsync()
    {
        var db = new TestDatabase();
        await db._connection.OpenAsync();
        db.Factory = new ContextFactory(new DbContextOptionsBuilder<NomadInboxDbContext>().UseSqlite(db._connection).Options);
        await using (var ctx = await db.Factory.CreateDbContextAsync())
            await ctx.Database.MigrateAsync();
        db.Settings = new SettingsService(db.Factory, new EphemeralDataProtectionProvider());
        return db;
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private sealed class ContextFactory(DbContextOptions<NomadInboxDbContext> options) : IDbContextFactory<NomadInboxDbContext>
    {
        public NomadInboxDbContext CreateDbContext() => new(options);
    }
}

public sealed class FakeMailboxReader : IMailboxReader
{
    public MailScanResult NextScan { get; set; } = new(1, 0, false, []);
    public MailScanRequest? LastRequest { get; private set; }
    public FakeMailboxSession Session { get; } = new();

    public Task<ConnectionTestResult> TestConnectionAsync(MailAccount account, CancellationToken ct) =>
        Task.FromResult(new ConnectionTestResult(true, "ok"));

    public Task<MailScanResult> ScanAsync(MailAccount account, MailScanRequest request, CancellationToken ct)
    {
        LastRequest = request;
        return Task.FromResult(NextScan);
    }

    public Task<IMailboxSession> OpenSessionAsync(MailAccount account, CancellationToken ct) =>
        Task.FromResult<IMailboxSession>(Session);
}

public sealed class FakeMailboxSession : IMailboxSession
{
    public uint UidValidity { get; set; } = 7;
    public bool SupportsLabels => true;

    /// <summary>Messages by Message-ID.</summary>
    public Dictionary<string, FetchedMessage> Messages { get; } = [];
    public List<(uint Uid, string Label)> Labels { get; } = [];

    public Task<FetchedMessage?> FetchAsync(uint uid, uint uidValidity, string messageId, CancellationToken ct) =>
        Task.FromResult(Messages.GetValueOrDefault(messageId));

    public Task AddLabelAsync(uint uid, string label, CancellationToken ct)
    {
        Labels.Add((uid, label));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
