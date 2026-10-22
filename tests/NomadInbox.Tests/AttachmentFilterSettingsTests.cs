using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NomadInbox.Core.Content;
using NomadInbox.Core.Domain;

namespace NomadInbox.Tests;

public class AttachmentFilterSettingsTests
{
    [Fact]
    public void Everything_but_programs_is_allowed_by_default()
    {
        var kinds = new InboxSettings().AllowedAttachmentKinds;

        Assert.False(kinds.HasFlag(AttachmentKinds.Executables));
        Assert.All(
            [AttachmentKinds.Images, AttachmentKinds.Video, AttachmentKinds.Audio, AttachmentKinds.Documents, AttachmentKinds.Archives, AttachmentKinds.Other],
            kind => Assert.True(kinds.HasFlag(kind)));
    }

    [Fact]
    public async Task Choice_is_saved_and_loaded()
    {
        await using var test = await TestDatabase.CreateAsync();
        var kinds = AttachmentKinds.Images | AttachmentKinds.Executables;

        await test.Settings.SaveAsync(new InboxSettings { AllowedAttachmentKinds = kinds }, newPassword: null);

        Assert.Equal(kinds, (await test.Settings.GetAsync()).AllowedAttachmentKinds);
    }

    [Fact]
    public async Task Existing_settings_get_the_default_filter_when_upgrading()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.Settings.SaveAsync(new InboxSettings { Folder = "Archive" }, newPassword: null);

        await using (var db = await test.Factory.CreateDbContextAsync())
        {
            var migrator = db.GetService<IMigrator>();
            var migration = db.Database.GetMigrations().Single(m => m.EndsWith("_AttachmentFilter"));
            await migrator.MigrateAsync(db.Database.GetMigrations().TakeWhile(m => m != migration).Last());
            await migrator.MigrateAsync();
        }

        var settings = await test.Settings.GetAsync();
        Assert.Equal("Archive", settings.Folder);
        Assert.Equal(InboxSettings.DefaultAttachmentKinds, settings.AllowedAttachmentKinds);
    }
}
