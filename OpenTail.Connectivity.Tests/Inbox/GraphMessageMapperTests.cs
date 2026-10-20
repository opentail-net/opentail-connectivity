using GraphMessage = Microsoft.Graph.Models.Message;
using GraphRecipient = Microsoft.Graph.Models.Recipient;
using GraphEmailAddress = Microsoft.Graph.Models.EmailAddress;
using GraphItemBody = Microsoft.Graph.Models.ItemBody;
using GraphFileAttachment = Microsoft.Graph.Models.FileAttachment;

namespace OpenTail.Connectivity.Tests.Inbox;

public sealed class GraphMessageMapperTests
{
    private static GraphRecipient MakeRecipient(string name, string address) =>
        new GraphRecipient { EmailAddress = new GraphEmailAddress { Name = name, Address = address } };

    [Fact]
    public void Maps_PlainTextBody()
    {
        var msg = new GraphMessage
        {
            InternetMessageId = "<id1@example.com>",
            Subject = "Plain text email",
            From = MakeRecipient("Alice", "alice@example.com"),
            ToRecipients = [MakeRecipient("Bob", "bob@example.com")],
            ReceivedDateTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Body = new GraphItemBody { ContentType = Microsoft.Graph.Models.BodyType.Text, Content = "Hello plain text" }
        };

        var record = GraphMessageMapper.Map(msg, "tenant");

        Assert.Equal("Hello plain text", record.Body);
        Assert.Null(record.BodyHtml);
    }

    [Fact]
    public void Maps_HtmlBody_StrippedForBody()
    {
        var msg = new GraphMessage
        {
            InternetMessageId = "<id2@example.com>",
            Subject = "HTML email",
            From = MakeRecipient("Alice", "alice@example.com"),
            ReceivedDateTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Body = new GraphItemBody { ContentType = Microsoft.Graph.Models.BodyType.Html, Content = "<p>HTML <b>content</b></p>" }
        };

        var record = GraphMessageMapper.Map(msg, "tenant");

        Assert.NotNull(record.BodyHtml);
        Assert.Contains("<p>HTML", record.BodyHtml);
        Assert.DoesNotContain("<p>", record.Body);
        Assert.Contains("HTML", record.Body);
    }

    [Fact]
    public void Maps_Recipients_FromToCc()
    {
        var msg = new GraphMessage
        {
            InternetMessageId = "<id3@example.com>",
            Subject = "Recipients test",
            From = MakeRecipient("Alice", "alice@example.com"),
            ToRecipients = [MakeRecipient("Bob", "bob@example.com"), MakeRecipient("Carol", "carol@example.com")],
            CcRecipients = [MakeRecipient("Dave", "dave@example.com")],
            ReceivedDateTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Body = new GraphItemBody { ContentType = Microsoft.Graph.Models.BodyType.Text, Content = "body" }
        };

        var record = GraphMessageMapper.Map(msg, "tenant");

        Assert.Contains("alice@example.com", record.From);
        Assert.Contains("bob@example.com", record.To);
        Assert.Contains("carol@example.com", record.To);
        Assert.Contains("dave@example.com", record.Cc);
    }

    [Fact]
    public void Maps_NullSubject_BecomeEmptyString()
    {
        var msg = new GraphMessage
        {
            InternetMessageId = "<id4@example.com>",
            Subject = null,
            From = MakeRecipient("Alice", "alice@example.com"),
            ReceivedDateTime = DateTimeOffset.UtcNow,
            Body = new GraphItemBody { ContentType = Microsoft.Graph.Models.BodyType.Text, Content = "body" }
        };

        var ex = Record.Exception(() => GraphMessageMapper.Map(msg, "tenant"));
        Assert.Null(ex);

        var record = GraphMessageMapper.Map(msg, "tenant");
        Assert.Equal(string.Empty, record.Subject);
    }

    [Fact]
    public void Maps_Attachments_NamesCollected()
    {
        var msg = new GraphMessage
        {
            InternetMessageId = "<id5@example.com>",
            Subject = "With attachment",
            From = MakeRecipient("Alice", "alice@example.com"),
            ReceivedDateTime = DateTimeOffset.UtcNow,
            Body = new GraphItemBody { ContentType = Microsoft.Graph.Models.BodyType.Text, Content = "see attached" },
            Attachments = [new GraphFileAttachment { Name = "document.pdf" }]
        };

        var record = GraphMessageMapper.Map(msg, "tenant");

        Assert.Contains("document.pdf", record.AttachmentNames);
    }

    [Fact]
    public void Maps_NullBody_NoException()
    {
        var msg = new GraphMessage
        {
            InternetMessageId = "<id6@example.com>",
            Subject = "Null body",
            From = MakeRecipient("Alice", "alice@example.com"),
            ReceivedDateTime = DateTimeOffset.UtcNow,
            Body = null
        };

        var ex = Record.Exception(() => GraphMessageMapper.Map(msg, "tenant"));
        Assert.Null(ex);

        var record = GraphMessageMapper.Map(msg, "tenant");
        Assert.Equal(string.Empty, record.Body);
        Assert.Null(record.BodyHtml);
    }
}
