using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NomadInbox.Core.Domain;

namespace NomadInbox.Infrastructure.Data;

public class NomadInboxDbContext(DbContextOptions<NomadInboxDbContext> options) : DbContext(options)
{
    public DbSet<InboxEmail> Emails => Set<InboxEmail>();
    public DbSet<EmailLink> Links => Set<EmailLink>();
    public DbSet<EmailAttachment> Attachments => Set<EmailAttachment>();
    public DbSet<FollowedChannel> FollowedChannels => Set<FollowedChannel>();
    public DbSet<KnowledgeDocument> KnowledgeDocuments => Set<KnowledgeDocument>();
    public DbSet<ImageDescription> ImageDescriptions => Set<ImageDescription>();
    public DbSet<ExportedBundle> ExportedBundles => Set<ExportedBundle>();
    public DbSet<ExportedItem> ExportedItems => Set<ExportedItem>();
    public DbSet<ImportedBundle> ImportedBundles => Set<ImportedBundle>();
    public DbSet<ImportedItem> ImportedItems => Set<ImportedItem>();
    public DbSet<DeletedMessage> DeletedMessages => Set<DeletedMessage>();
    public DbSet<ArchivedPage> Pages => Set<ArchivedPage>();
    public DbSet<PageAsset> PageAssets => Set<PageAsset>();
    public DbSet<PageLink> PageLinks => Set<PageLink>();
    public DbSet<ArchiveFile> ArchiveFiles => Set<ArchiveFile>();
    public DbSet<InboxSettings> Settings => Set<InboxSettings>();
    public DbSet<MailboxCursor> Cursors => Set<MailboxCursor>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite can't compare or ORDER BY DateTimeOffset natively; the binary form sorts correctly.
        builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<InboxEmail>(e =>
        {
            e.HasIndex(x => x.MessageId).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.ReceivedAt);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Fields).HasConversion(
                v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, JsonSerializerOptions.Default) ?? new(),
                new ValueComparer<Dictionary<string, string>>(
                    (a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
                    v => v.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value)),
                    v => new Dictionary<string, string>(v)));
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            e.HasMany(x => x.Links).WithOne().HasForeignKey(l => l.InboxEmailId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Attachments).WithOne().HasForeignKey(a => a.InboxEmailId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<EmailLink>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.VideoStatus).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.Kind);
            e.HasIndex(x => x.VideoStatus);
            e.HasIndex(x => x.Status);
        });

        model.Entity<EmailAttachment>(e => e.HasIndex(x => x.Sha256));

        model.Entity<FollowedChannel>(e => e.HasIndex(x => x.ChannelId).IsUnique());

        model.Entity<ExportedBundle>(e =>
        {
            e.HasKey(x => x.Number);
            e.Property(x => x.Number).ValueGeneratedNever(); // bundle numbers are counted by the exporter
        });
        model.Entity<ExportedItem>(e => e.HasKey(x => x.MessageId));
        model.Entity<ImportedBundle>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.CollectorId, x.Number });
        });
        model.Entity<ImportedItem>(e => e.HasKey(x => x.MessageId));

        model.Entity<ImageDescription>(e =>
        {
            e.HasKey(x => x.ImageKey);
            e.Property(x => x.ImageKey).HasMaxLength(32);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.Status);
        });

        model.Entity<KnowledgeDocument>(e =>
        {
            e.HasIndex(x => x.ItemKey).IsUnique();
            e.HasIndex(x => x.InboxEmailId);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
        });

        model.Entity<DeletedMessage>(e => e.HasKey(x => x.MessageId));

        model.Entity<ArchivedPage>(e =>
        {
            e.HasIndex(x => x.Url).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.InboxEmailId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasMany(x => x.Assets).WithOne().HasForeignKey(a => a.ArchivedPageId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Links).WithOne().HasForeignKey(l => l.ArchivedPageId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<PageAsset>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.FileName);
        });
        model.Entity<PageLink>(e => e.HasIndex(x => x.Url));

        model.Entity<ArchiveFile>(e =>
        {
            e.HasIndex(x => new { x.Identifier, x.FileName }).IsUnique();
            e.HasIndex(x => x.Status);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        });

        model.Entity<InboxSettings>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Ignore(x => x.IsMailConfigured);
            e.Ignore(x => x.AllowedSenderList);
            e.Ignore(x => x.IsAiConfigured);
            e.Ignore(x => x.TranscriptLanguageList);
            e.Ignore(x => x.ImageModel);
            e.Ignore(x => x.IsImageAiConfigured);
            e.Ignore(x => x.UsesInternet);
            e.Ignore(x => x.SendsToNomad);
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.VideoDownloadMode).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.PageArchiveMode).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Skin).HasMaxLength(40);
            e.Property(x => x.ColorMode).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.AllowedAttachmentKinds).HasConversion<string>().HasMaxLength(200);
        });

        model.Entity<MailboxCursor>(e => e.HasKey(x => x.Folder));
    }
}
