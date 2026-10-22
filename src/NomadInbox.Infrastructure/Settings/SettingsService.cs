using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NomadInbox.Core.Domain;
using NomadInbox.Core.Mail;
using NomadInbox.Infrastructure.Data;

namespace NomadInbox.Infrastructure.Settings;

public sealed class SettingsService(
    IDbContextFactory<NomadInboxDbContext> dbFactory,
    IDataProtectionProvider dataProtection,
    ILogger<SettingsService>? logger = null)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("NomadInbox.MailPassword.v1");

    /// <summary>Raised after settings are saved, so the poller can pick up changes immediately.</summary>
    public event Action? Changed;

    public async Task<InboxSettings> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == InboxSettings.SingletonId, ct)
               ?? new InboxSettings();
    }

    /// <param name="newPassword">Plain-text password to store; null or empty keeps the existing one.</param>
    public async Task SaveAsync(InboxSettings settings, string? newPassword, CancellationToken ct = default)
    {
        settings.Id = InboxSettings.SingletonId;
        if (!string.IsNullOrEmpty(newPassword))
            settings.ProtectedPassword = _protector.Protect(NormalizePassword(settings.ImapHost, newPassword));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Settings.AnyAsync(s => s.Id == InboxSettings.SingletonId, ct))
            db.Settings.Update(settings);
        else
            db.Settings.Add(settings);
        await db.SaveChangesAsync(ct);

        logger?.LogInformation(
            "Settings saved: account configured {Configured}, new password {NewPassword}, automatic checking {Polling} every {Minutes} min, {Senders} allowed sender(s), labels {Labels}",
            settings.IsMailConfigured, !string.IsNullOrEmpty(newPassword), settings.PollingEnabled,
            settings.PollIntervalMinutes, settings.AllowedSenderList.Count, settings.ApplyLabels);
        Changed?.Invoke();
    }

    /// <summary>Builds connection details, preferring <paramref name="passwordOverride"/> (e.g. typed but not yet saved).</summary>
    public MailAccount? CreateAccount(InboxSettings settings, string? passwordOverride = null)
    {
        string? password = null;
        if (!string.IsNullOrEmpty(passwordOverride))
            password = NormalizePassword(settings.ImapHost, passwordOverride);
        else if (!string.IsNullOrEmpty(settings.ProtectedPassword))
            password = _protector.Unprotect(settings.ProtectedPassword);

        if (string.IsNullOrWhiteSpace(settings.ImapHost) || string.IsNullOrWhiteSpace(settings.Username) || password is null)
            return null;

        return new MailAccount(settings.ImapHost.Trim(), settings.ImapPort, settings.Username.Trim(), password, settings.Folder.Trim());
    }

    // Google shows app passwords as "abcd efgh ijkl mnop"; the spaces are not part of the password.
    private static string NormalizePassword(string host, string password) =>
        host.Contains("gmail", StringComparison.OrdinalIgnoreCase) || host.Contains("google", StringComparison.OrdinalIgnoreCase)
            ? string.Concat(password.Where(c => !char.IsWhiteSpace(c)))
            : password;
}
