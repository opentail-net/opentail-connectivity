using GMessage = Google.Apis.Gmail.v1.Data.Message;
using GMessagePart = Google.Apis.Gmail.v1.Data.MessagePart;
using GMessagePartBody = Google.Apis.Gmail.v1.Data.MessagePartBody;
using GMessagePartHeader = Google.Apis.Gmail.v1.Data.MessagePartHeader;

namespace OpenTail.Connectivity.Tests.Inbox;

public sealed class GmailMessageMapperTests
{
    private static string B64Url(string text) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static GMessagePartBody MakeBody(string text) =>
        new GMessagePartBody { Data = B64Url(text) };

    private static GMessagePartHeader Header(string name, string value) =>
        new GMessagePartHeader { Name = name, Value = value };

    [Fact]
    public void Maps_PlainTextBody()
    {
        var msg = new GMessage
        {
            Id = "msg1",
            Payload = new GMessagePart
            {
                MimeType = "text/plain",
                Body = MakeBody("Hello, plain text!"),
                Headers = [Header("Subject", "Test"), Header("From", "a@b.com"),
                           Header("To", "c@d.com"), Header("Date", "Mon, 01 Jan 2024 00:00:00 +0000")]
            }
        };

        var record = GmailMessageMapper.Map(msg, "tenant");

        Assert.Equal("Hello, plain text!", record.Body);
        Assert.Null(record.BodyHtml);
    }

    [Fact]
    public void Maps_HtmlBody_FallsBackToStripped()
    {
        var msg = new GMessage
        {
            Id = "msg2",
            Payload = new GMessagePart
            {
                MimeType = "text/html",
                Body = MakeBody("<p>Hello <b>world</b></p>"),
                Headers = [Header("Subject", "HTML Test"), Header("From", "a@b.com"),
                           Header("To", "c@d.com"), Header("Date", "Mon, 01 Jan 2024 00:00:00 +0000")]
            }
        };

        var record = GmailMessageMapper.Map(msg, "tenant");

        Assert.NotNull(record.BodyHtml);
        Assert.Contains("<p>Hello", record.BodyHtml);
        Assert.DoesNotContain("<p>", record.Body);
        Assert.Contains("Hello", record.Body);
    }

    [Fact]
    public void Maps_MultipartAlternative_PrefersPlain()
    {
        var msg = new GMessage
        {
            Id = "msg3",
            Payload = new GMessagePart
            {
                MimeType = "multipart/alternative",
                Headers = [Header("Subject", "Multi"), Header("From", "a@b.com"),
                           Header("To", "c@d.com"), Header("Date", "Mon, 01 Jan 2024 00:00:00 +0000")],
                Parts =
                [
                    new GMessagePart { MimeType = "text/plain", Body = MakeBody("Plain body text") },
                    new GMessagePart { MimeType = "text/html", Body = MakeBody("<p>HTML body</p>") }
                ]
            }
        };

        var record = GmailMessageMapper.Map(msg, "tenant");

        Assert.Equal("Plain body text", record.Body);
        Assert.Contains("HTML body", record.BodyHtml ?? "");
    }

    [Fact]
    public void Maps_AttachmentNames()
    {
        var msg = new GMessage
        {
            Id = "msg4",
            Payload = new GMessagePart
            {
                MimeType = "multipart/mixed",
                Headers = [Header("Subject", "Attach"), Header("From", "a@b.com"),
                           Header("To", "c@d.com"), Header("Date", "Mon, 01 Jan 2024 00:00:00 +0000")],
                Parts =
                [
                    new GMessagePart { MimeType = "text/plain", Body = MakeBody("See attached") },
                    new GMessagePart { MimeType = "application/pdf", Filename = "report.pdf" }
                ]
            }
        };

        var record = GmailMessageMapper.Map(msg, "tenant");

        Assert.Contains("report.pdf", record.AttachmentNames);
    }

    [Fact]
    public void Maps_Headers_FromToSubjectDate()
    {
        var msg = new GMessage
        {
            Id = "msg5",
            Payload = new GMessagePart
            {
                MimeType = "text/plain",
                Body = MakeBody("body"),
                Headers =
                [
                    Header("From", "sender@example.com"),
                    Header("To", "recipient@example.com"),
                    Header("Cc", "cc@example.com"),
                    Header("Subject", "Important Email"),
                    Header("Date", "Mon, 01 Jan 2024 00:00:00 +0000"),
                    Header("Message-ID", "<unique-id@example.com>")
                ]
            }
        };

        var record = GmailMessageMapper.Map(msg, "tenant");

        Assert.Equal("sender@example.com", record.From);
        Assert.Equal("recipient@example.com", record.To);
        Assert.Equal("cc@example.com", record.Cc);
        Assert.Equal("Important Email", record.Subject);
        Assert.Equal("<unique-id@example.com>", record.MessageId);
    }

    [Fact]
    public void Maps_InReplyTo_Header_Preserved()
    {
        var msg = new GMessage
        {
            Id = "msg6",
            Payload = new GMessagePart
            {
                MimeType = "text/plain",
                Body = MakeBody("reply body"),
                Headers =
                [
                    Header("Subject", "Re: Test"),
                    Header("From", "a@b.com"),
                    Header("To", "c@d.com"),
                    Header("Date", "Mon, 01 Jan 2024 00:00:00 +0000"),
                    Header("In-Reply-To", "<original-id@example.com>")
                ]
            }
        };

        var record = GmailMessageMapper.Map(msg, "tenant");

        Assert.True(record.Headers.ContainsKey("In-Reply-To"));
        Assert.Equal("<original-id@example.com>", record.Headers["In-Reply-To"]);
    }

    [Fact]
    public void Maps_EmptyParts_NoException()
    {
        var msg = new GMessage
        {
            Id = "msg7",
            Payload = new GMessagePart
            {
                MimeType = "text/plain",
                Headers = [],
                Body = null,
                Parts = null
            }
        };

        var ex = Record.Exception(() => GmailMessageMapper.Map(msg, "tenant"));

        Assert.Null(ex);
    }

    [Fact]
    public void Maps_NullPayload_NoException()
    {
        var msg = new GMessage { Id = "msg8", Payload = null };

        var ex = Record.Exception(() => GmailMessageMapper.Map(msg, "tenant"));
        Assert.Null(ex);
    }
}
