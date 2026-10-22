using MimeKit;
using NomadInbox.Infrastructure.Mail;

namespace NomadInbox.Tests;

public class MimeFileTests
{
    [Fact]
    public void Files_attachments_and_named_inline_parts_but_not_bodies()
    {
        // Shaped like a Gmail message with a pasted image and an attached PDF.
        var builder = new BodyBuilder { TextBody = "See the photo", HtmlBody = "<p>See the photo</p><img src=\"cid:img1\">" };
        var inline = builder.LinkedResources.Add("IMG_0042.jpg", new byte[] { 1, 2, 3 }, new ContentType("image", "jpeg"));
        inline.ContentId = "img1";
        builder.Attachments.Add("guide.pdf", new byte[] { 4, 5 }, new ContentType("application", "pdf"));
        var message = new MimeMessage { Body = builder.ToMessageBody() };

        var files = message.BodyParts.Where(MailKitMailboxSession.IsFile).Cast<MimePart>().Select(p => p.FileName);

        Assert.Equal(["IMG_0042.jpg", "guide.pdf"], files.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Attached_text_file_is_a_file()
    {
        var builder = new BodyBuilder { TextBody = "body" };
        builder.Attachments.Add("notes.txt", "hello"u8.ToArray(), new ContentType("text", "plain"));
        var message = new MimeMessage { Body = builder.ToMessageBody() };

        Assert.Equal(["notes.txt"], message.BodyParts.Where(MailKitMailboxSession.IsFile).Cast<MimePart>().Select(p => p.FileName));
    }
}
