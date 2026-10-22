using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using NomadInbox.Core.Mail;
using NomadInbox.Core.Subjects;

namespace NomadInbox.Infrastructure.Mail;

public sealed class MailKitMailboxReader(ILogger<MailKitMailboxReader> logger) : IMailboxReader
{
    public async Task<ConnectionTestResult> TestConnectionAsync(MailAccount account, CancellationToken ct)
    {
        try
        {
            using var client = await ConnectAsync(account, ct);
            var folder = await OpenFolderAsync(client, account.Folder, ct);
            var count = folder.Count;
            await client.DisconnectAsync(true, ct);
            return new ConnectionTestResult(true, $"Connected. \"{account.Folder}\" contains {count:N0} messages.", count);
        }
        catch (AuthenticationException)
        {
            return new ConnectionTestResult(false,
                "Login failed. For Gmail, use a 16-character app password (2-Step Verification must be on), not your normal password.");
        }
        catch (FolderNotFoundException)
        {
            return new ConnectionTestResult(false, $"Logged in, but the folder \"{account.Folder}\" does not exist.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "IMAP connection test failed for {Host}", account.Host);
            return new ConnectionTestResult(false, $"Could not connect: {ex.Message}");
        }
    }

    public async Task<MailScanResult> ScanAsync(MailAccount account, MailScanRequest request, CancellationToken ct)
    {
        using var client = await ConnectAsync(account, ct);
        var folder = await OpenFolderAsync(client, account.Folder, ct);

        var uidValidity = folder.UidValidity;
        var validityChanged = request.KnownUidValidity != 0 && request.KnownUidValidity != uidValidity;
        var lastSeen = validityChanged ? 0u : request.LastSeenUid;
        if (validityChanged)
            logger.LogWarning("UIDVALIDITY of {Folder} changed ({Old} -> {New}); rescanning", account.Folder, request.KnownUidValidity, uidValidity);

        // UIDNEXT is reported by Gmail; fall back to "unknown" (0) and derive it from the results below.
        var highest = folder.UidNext is { } next ? next.Id - 1 : 0u;

        // "n:*" always matches the newest message, so skip the work when nothing new has arrived.
        if (highest != 0 && highest <= lastSeen)
        {
            await client.DisconnectAsync(true, ct);
            return new MailScanResult(uidValidity, lastSeen, validityChanged, []);
        }

        const MessageSummaryItems Items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate;
        List<IMessageSummary> summaries;

        if (lastSeen > 0)
        {
            // New mail is fetched by UID, never found via SEARCH: Gmail's IMAP search runs on its search index,
            // which can lag behind delivery, and a message missed that way would be skipped for good.
            summaries = (await folder.FetchAsync(
                    new UniqueIdRange(new UniqueId(uidValidity, lastSeen + 1), UniqueId.MaxValue), Items, ct))
                .Where(s => s.UniqueId.Id > lastSeen)
                .ToList();
        }
        else
        {
            // First scan: SEARCH avoids fetching every envelope in a large folder. Gmail's search is word-based,
            // so search for the prefix's words and check the real prefix client-side.
            SearchQuery query = SearchQuery.SubjectContains(SearchTerm(request.SubjectPrefix));
            if (request.Since is { } since)
                query = query.And(SearchQuery.DeliveredAfter(since.UtcDateTime.Date));
            var found = await folder.SearchAsync(query, ct);
            summaries = found.Count == 0 ? [] : (await folder.FetchAsync(found, Items, ct)).ToList();

            // Plus the newest messages directly, in case the search index hasn't caught up with them yet.
            if (folder.Count > 0)
            {
                var recent = await folder.FetchAsync(Math.Max(0, folder.Count - RecentOnFirstScan), -1, Items, ct);
                summaries.AddRange(recent.Where(s => request.Since is not { } d || (s.InternalDate ?? DateTimeOffset.MaxValue) >= d));
            }
            summaries = summaries.DistinctBy(s => s.UniqueId.Id).ToList();
        }

        summaries = summaries.OrderBy(s => s.UniqueId.Id).ToList();
        if (summaries.Count > request.MaxResults)
        {
            summaries = summaries.Take(request.MaxResults).ToList();
            highest = summaries[^1].UniqueId.Id; // resume from here next time
        }
        else if (highest == 0 && summaries.Count > 0)
        {
            highest = summaries[^1].UniqueId.Id;
        }

        var matches = new List<MailHeader>();
        foreach (var s in summaries)
        {
            var subject = s.Envelope?.Subject ?? "";
            if (!SubjectParser.Matches(subject, request.SubjectPrefix)) continue;

            var from = s.Envelope?.From.Mailboxes.FirstOrDefault();
            matches.Add(new MailHeader(
                s.UniqueId.Id,
                MessageIdOrFallback(s.Envelope?.MessageId, account, uidValidity, s.UniqueId.Id),
                from?.Address ?? "",
                string.IsNullOrWhiteSpace(from?.Name) ? null : from.Name,
                subject,
                s.InternalDate ?? s.Envelope?.Date ?? DateTimeOffset.UtcNow));
        }

        logger.LogInformation("Scanned {Count} message(s) in {Folder} after UID {LastSeen}; {Matches} match the prefix",
            summaries.Count, account.Folder, lastSeen, matches.Count);
        await client.DisconnectAsync(true, ct);
        return new MailScanResult(uidValidity, Math.Max(highest, lastSeen), validityChanged, matches);
    }

    private const int RecentOnFirstScan = 50;

    public async Task<IMailboxSession> OpenSessionAsync(MailAccount account, CancellationToken ct)
    {
        var client = await ConnectAsync(account, ct);
        try
        {
            var folder = string.Equals(account.Folder, "INBOX", StringComparison.OrdinalIgnoreCase)
                ? client.Inbox
                : await client.GetFolderAsync(account.Folder, ct);
            // Read-write so labels can be added. Fetching uses BODY.PEEK, so messages stay unread.
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            return new MailKitMailboxSession(client, folder, logger);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal static async Task<ImapClient> ConnectAsync(MailAccount account, CancellationToken ct)
    {
        var client = new ImapClient { Timeout = 60_000 };
        try
        {
            var security = account.Port == 993 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
            await client.ConnectAsync(account.Host, account.Port, security, ct);
            await client.AuthenticateAsync(account.Username, account.Password, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<IMailFolder> OpenFolderAsync(ImapClient client, string name, CancellationToken ct)
    {
        var folder = string.Equals(name, "INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(name, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        return folder;
    }

    private static string SearchTerm(string prefix)
    {
        var words = new string(prefix.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray()).Trim();
        return words.Length > 0 ? words : prefix.Trim();
    }

    private static string MessageIdOrFallback(string? messageId, MailAccount account, uint uidValidity, uint uid) =>
        string.IsNullOrWhiteSpace(messageId)
            ? $"nomad-inbox-{uidValidity}-{uid}@{account.Host}"
            : messageId.Trim().Trim('<', '>');
}
