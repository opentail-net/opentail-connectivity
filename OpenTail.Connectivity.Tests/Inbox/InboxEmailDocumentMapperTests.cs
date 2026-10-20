namespace OpenTail.Connectivity.Tests.Inbox;

public sealed class InboxEmailDocumentMapperTests
{
    private static InboxEmailRecord MakeRecord(
        string messageId = "<test@example.com>",
        string subject = "Hello World",
        string from = "alice@example.com",
        string body = "This is the body.",
        string folder = "INBOX",
        string tenant = "acme") =>
        new(
            MessageId: messageId,
            Subject: subject,
            From: from,
            To: "bob@example.com",
            Cc: "",
            Date: new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero),
            Body: body,
            BodyHtml: null,
            AttachmentNames: [],
            Folder: folder,
            TenantContext: tenant,
            Headers: new Dictionary<string, string>());

    [Fact]
    public void Map_ProducesNormalisedDocument_WithSourceTypeInboxEmail()
    {
        var record = MakeRecord();
        var doc = InboxEmailDocumentMapper.Map(record);
        Assert.Equal("inbox-email", doc.SourceType);
    }

    [Fact]
    public void Map_TitleEqualsSubject()
    {
        var doc = InboxEmailDocumentMapper.Map(MakeRecord(subject: "My Subject"));
        Assert.Equal("My Subject", doc.Title);
    }

    [Fact]
    public void Map_TitleFallsBackWhenSubjectEmpty()
    {
        var doc = InboxEmailDocumentMapper.Map(MakeRecord(subject: ""));
        Assert.Equal("(no subject)", doc.Title);
    }

    [Fact]
    public void Map_BodyContainsFromAndDate()
    {
        var doc = InboxEmailDocumentMapper.Map(MakeRecord(from: "alice@example.com"));
        Assert.Contains("alice@example.com", doc.Body);
        Assert.Contains("2026-03-01", doc.Body);
    }

    [Fact]
    public void Map_SourceRefEqualsMessageId()
    {
        var doc = InboxEmailDocumentMapper.Map(MakeRecord(messageId: "<abc@host.com>"));
        Assert.Equal("<abc@host.com>", doc.SourceRef);
    }

    [Fact]
    public void Map_TenantContextPreserved()
    {
        var doc = InboxEmailDocumentMapper.Map(MakeRecord(tenant: "contoso"));
        Assert.Equal("contoso", doc.TenantContext);
    }

    [Fact]
    public void Map_ContentHashIsSha256HexString()
    {
        var doc = InboxEmailDocumentMapper.Map(MakeRecord());
        Assert.Equal(64, doc.ContentHash.Length);
        Assert.True(doc.ContentHash.All(c => "0123456789abcdef".Contains(c)));
    }

    [Fact]
    public void Map_MetadataContainsEmailFields()
    {
        var doc = InboxEmailDocumentMapper.Map(MakeRecord(subject: "Test", from: "a@b.com", folder: "Sent"));
        Assert.Equal("Test", doc.Metadata["email.subject"]);
        Assert.Equal("a@b.com", doc.Metadata["email.from"]);
        Assert.Equal("Sent", doc.Metadata["email.folder"]);
    }

    [Fact]
    public void Map_AttachmentsInMetadataWhenPresent()
    {
        var record = MakeRecord() with { AttachmentNames = ["report.pdf", "photo.jpg"] };
        var doc = InboxEmailDocumentMapper.Map(record);
        Assert.Contains("report.pdf", doc.Metadata["email.attachments"]);
        Assert.Contains("photo.jpg", doc.Metadata["email.attachments"]);
    }

    [Fact]
    public void Map_CcOmittedFromMetadataWhenEmpty()
    {
        var doc = InboxEmailDocumentMapper.Map(MakeRecord());
        Assert.False(doc.Metadata.ContainsKey("email.cc"));
    }

    [Fact]
    public void Map_CcIncludedInMetadataWhenPresent()
    {
        var record = MakeRecord() with { Cc = "cc@example.com" };
        var doc = InboxEmailDocumentMapper.Map(record);
        Assert.Equal("cc@example.com", doc.Metadata["email.cc"]);
    }

    [Fact]
    public void Map_IdIsDeterministicForSameInput()
    {
        var record = MakeRecord();
        var doc1 = InboxEmailDocumentMapper.Map(record);
        var doc2 = InboxEmailDocumentMapper.Map(record);
        Assert.Equal(doc1.Id, doc2.Id);
    }

    [Fact]
    public void Map_IdDiffersForDifferentTenants()
    {
        var doc1 = InboxEmailDocumentMapper.Map(MakeRecord(tenant: "acme"));
        var doc2 = InboxEmailDocumentMapper.Map(MakeRecord(tenant: "contoso"));
        Assert.NotEqual(doc1.Id, doc2.Id);
    }
}
