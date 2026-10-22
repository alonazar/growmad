using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Logging;
using MimeKit;
using NomadInbox.Core.Mail;

namespace NomadInbox.Infrastructure.Mail;

internal sealed class MailKitMailboxSession(ImapClient client, IMailFolder folder, ILogger logger) : IMailboxSession
{
    public uint UidValidity => folder.UidValidity;

    public bool SupportsLabels => client.Capabilities.HasFlag(ImapCapabilities.GMailExt1);

    public async Task<FetchedMessage?> FetchAsync(uint uid, uint uidValidity, string messageId, CancellationToken ct)
    {
        MimeMessage? message = null;
        var id = new UniqueId(folder.UidValidity, uid);

        if (uidValidity == folder.UidValidity)
        {
            try
            {
                message = await folder.GetMessageAsync(id, ct);
            }
            catch (MessageNotFoundException)
            {
            }
            // A recycled UID would point at a different message; only trust it if the Message-ID agrees.
            if (message is not null && !IsSynthetic(messageId) && !SameMessageId(message.MessageId, messageId))
                message = null;
        }

        if (message is null && !IsSynthetic(messageId))
        {
            var found = await folder.SearchAsync(SearchQuery.HeaderContains("Message-ID", messageId), ct);
            if (found.Count > 0)
            {
                id = found[0];
                message = await folder.GetMessageAsync(id, ct);
            }
        }

        if (message is null) return null;

        logger.LogInformation("Message {MessageId} parts: {Parts}", messageId, string.Join("; ", message.BodyParts.Select(Describe)));

        var attachments = new List<FetchedAttachment>();
        var index = 0;
        foreach (var entity in message.BodyParts.Where(IsFile))
        {
            index++;
            using var buffer = new MemoryStream();
            string fileName;
            string contentType;

            switch (entity)
            {
                case MessagePart { Message: { } forwarded }:
                    // An attached email: keep it as an .eml file.
                    await forwarded.WriteToAsync(buffer, ct);
                    fileName = $"{Sanitize(forwarded.Subject) ?? $"message-{index}"}.eml";
                    contentType = "message/rfc822";
                    break;
                case MimePart part:
                    if (part.Content is null) continue;
                    await part.Content.DecodeToAsync(buffer, ct);
                    contentType = part.ContentType.MimeType;
                    fileName = part.FileName
                               ?? $"attachment-{index}{(MimeTypes.TryGetExtension(contentType, out var ext) ? ext : "")}";
                    break;
                default:
                    continue;
            }

            attachments.Add(new FetchedAttachment(fileName, contentType, buffer.ToArray()));
        }

        return new FetchedMessage(id.Id, message.TextBody, message.HtmlBody, attachments);
    }

    public async Task AddLabelAsync(uint uid, string label, CancellationToken ct)
    {
        if (!SupportsLabels)
        {
            logger.LogDebug("Server has no Gmail label support; not labelling UID {Uid}", uid);
            return;
        }
        // Gmail creates the label if it doesn't exist yet.
        await folder.StoreAsync(new UniqueId(folder.UidValidity, uid),
            new StoreLabelsRequest(StoreAction.Add, [label]) { Silent = true }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (client.IsConnected) await client.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error while disconnecting from IMAP");
        }
        client.Dispose();
    }

    /// <summary>
    /// A part to file: anything marked as an attachment, plus inline parts that carry a file name, such as
    /// images pasted into a Gmail message ("inline", not "attachment"). The text and HTML bodies are not files.
    /// </summary>
    internal static bool IsFile(MimeEntity entity) => entity switch
    {
        MessagePart => true,
        TextPart text => text.IsAttachment,
        MimePart part => part.IsAttachment || !string.IsNullOrEmpty(part.FileName),
        _ => false,
    };

    private static string Describe(MimeEntity entity) =>
        $"{entity.ContentType.MimeType} {(entity.IsAttachment ? "attachment" : "inline")}" +
        (entity is MimePart { FileName: { } name } ? $" \"{name}\"" : "");

    private static bool IsSynthetic(string messageId) => messageId.StartsWith("nomad-inbox-", StringComparison.Ordinal);

    private static bool SameMessageId(string? a, string b) =>
        string.Equals(a?.Trim().Trim('<', '>'), b.Trim().Trim('<', '>'), StringComparison.Ordinal);

    private static string? Sanitize(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        var cleaned = new string(subject.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }
}
