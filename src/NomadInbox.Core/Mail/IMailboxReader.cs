namespace NomadInbox.Core.Mail;

public sealed record MailAccount(string Host, int Port, string Username, string Password, string Folder);

public sealed record MailHeader(
    uint Uid,
    string MessageId,
    string FromAddress,
    string? FromName,
    string Subject,
    DateTimeOffset ReceivedAt);

public sealed record MailScanRequest(
    string SubjectPrefix,
    uint KnownUidValidity,
    uint LastSeenUid,
    DateTimeOffset? Since,
    int MaxResults = 500); // messages examined per poll; any beyond that are picked up by the next poll

public sealed record MailScanResult(
    uint UidValidity,
    uint HighestUid, // the next scan resumes after this UID

    bool UidValidityChanged,
    IReadOnlyList<MailHeader> Matches);

public sealed record ConnectionTestResult(bool Success, string Message, int? MessageCount = null);

public sealed record FetchedAttachment(string FileName, string ContentType, byte[] Content);

public sealed record FetchedMessage(
    uint Uid, // may differ from the recorded UID if the message had to be found by Message-ID
    string? TextBody,
    string? HtmlBody,
    IReadOnlyList<FetchedAttachment> Attachments);

public interface IMailboxReader
{
    Task<ConnectionTestResult> TestConnectionAsync(MailAccount account, CancellationToken ct);

    /// <summary>Read-only search for new matching messages.</summary>
    Task<MailScanResult> ScanAsync(MailAccount account, MailScanRequest request, CancellationToken ct);

    /// <summary>One connection for fetching and labelling a batch of messages.</summary>
    Task<IMailboxSession> OpenSessionAsync(MailAccount account, CancellationToken ct);
}

public interface IMailboxSession : IAsyncDisposable
{
    uint UidValidity { get; }

    /// <summary>True when the server supports Gmail labels (X-GM-EXT-1).</summary>
    bool SupportsLabels { get; }

    /// <summary>Fetches by UID when <paramref name="uidValidity"/> still matches, otherwise by Message-ID. Null if it's gone.
    /// Does not mark the message as read.</summary>
    Task<FetchedMessage?> FetchAsync(uint uid, uint uidValidity, string messageId, CancellationToken ct);

    Task AddLabelAsync(uint uid, string label, CancellationToken ct);
}
