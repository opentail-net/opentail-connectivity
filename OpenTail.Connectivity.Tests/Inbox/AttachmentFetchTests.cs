using System.Net;
using Google.Apis.Gmail.v1;
using Google.Apis.Http;
using Google.Apis.Services;
using MailKit;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using GMessage = Google.Apis.Gmail.v1.Data.Message;
using GMessagePart = Google.Apis.Gmail.v1.Data.MessagePart;
using GMessagePartBody = Google.Apis.Gmail.v1.Data.MessagePartBody;
using GraphMessage = Microsoft.Graph.Models.Message;
using GraphFileAttachment = Microsoft.Graph.Models.FileAttachment;

namespace OpenTail.Connectivity.Tests.Inbox;

/// <summary>Attachment references from the mappers, and the three fetchers against stubbed providers.</summary>
public sealed class AttachmentReferenceTests
{
    [Fact]
    public void Gmail_references_are_part_ids_and_include_parts_whose_data_is_inline()
    {
        var msg = new GMessage
        {
            Id = "api-id-1",
            Payload = new GMessagePart
            {
                PartId = "",
                MimeType = "multipart/mixed",
                Headers = [new() { Name = "Message-ID", Value = "<rfc@example.com>" }],
                Parts =
                [
                    new GMessagePart { PartId = "0", MimeType = "text/plain", Body = new GMessagePartBody { Data = "aGk", Size = 2 } },
                    new GMessagePart { PartId = "1", MimeType = "application/pdf", Filename = "a.pdf",
                        Body = new GMessagePartBody { AttachmentId = "ANGjdJ-long", Size = 5000 } },
                    new GMessagePart { PartId = "2", MimeType = "text/csv", Filename = "small.csv",
                        Body = new GMessagePartBody { Data = "YSxi", Size = 3 } },
                ],
            },
        };

        var record = GmailMessageMapper.Map(msg, "tenant");

        Assert.Equal("<rfc@example.com>", record.MessageId);
        Assert.Equal("api-id-1", record.ProviderMessageId);
        Assert.Equal(["1", "2"], record.Attachments!.Select(a => a.AttachmentId));
        Assert.Equal(["a.pdf", "small.csv"], record.Attachments!.Select(a => a.Name));
        Assert.Equal(5000, record.Attachments![0].SizeBytes);
    }

    [Fact]
    public void Graph_records_carry_the_graph_id_and_the_attachment_content_type()
    {
        var msg = new GraphMessage
        {
            Id = "graph-id-1",
            InternetMessageId = "<rfc@example.com>",
            Attachments = [new GraphFileAttachment { Id = "att-1", Name = "a.pdf", ContentType = "application/pdf", Size = 10 }],
        };

        var record = GraphMessageMapper.Map(msg, "tenant");

        Assert.Equal("<rfc@example.com>", record.MessageId);
        Assert.Equal("graph-id-1", record.ProviderMessageId);
        var attachment = Assert.Single(record.Attachments!);
        Assert.Equal(("att-1", "application/pdf", 10L), (attachment.AttachmentId, attachment.ContentType, attachment.SizeBytes));
    }

    [Fact]
    public async Task Graph_listing_expands_attachments_without_their_content()
    {
        var graph = new GraphInboxSyncTests.StubGraph();
        var client = new GraphServiceClient(new HttpClient(graph), new AnonymousAuthenticationProvider());
        var adapter = new GraphInboxAdapter(GraphSettings.Create("t", "c", "s", "me@example.com"), "tenant", null,
            InboxSyncCommit.AfterEnumeration, client);

        await foreach (var _ in adapter.FetchRecentAsync(new InboxFetchOptions("INBOX", 5, null))) { }

        var url = Uri.UnescapeDataString(Assert.Single(graph.Requests).Url);
        Assert.Contains("$expand=attachments($select=id,name,contentType,size,isInline)", url);
        Assert.DoesNotContain("contentBytes", url);
        Assert.DoesNotContain(",attachments,", url);
    }

    private static BodyPartBasic Part(string specifier, string mime, string? fileName, uint octets, string encoding = "base64")
    {
        var type = ContentType.Parse(mime);
        var part = new BodyPartBasic(type, specifier) { Octets = octets, ContentTransferEncoding = encoding };
        if (fileName is not null)
            part.ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = fileName };
        return part;
    }

    [Fact]
    public void Imap_references_are_part_specifiers_so_files_with_the_same_name_differ()
    {
        var structure = new[]
        {
            Part("2", "application/pdf", "report.pdf", 4000),
            Part("3.1", "application/pdf", "report.pdf", 400, "7bit"),
            Part("4", "text/plain", null, 10),
        };

        var meta = ImapInboxAdapter.AttachmentsFromStructure(structure);

        Assert.Equal(["2", "3.1"], meta.Select(m => m.AttachmentId));
        Assert.Equal([3000L, 400L], meta.Select(m => m.SizeBytes)); // base64 decodes to three quarters; 7bit as is
    }

    [Fact]
    public void Imap_forwarded_messages_without_a_file_name_are_named_after_their_subject()
    {
        var forwarded = new BodyPartMessage(ContentType.Parse("message/rfc822"), "2")
        {
            Envelope = new Envelope { Subject = "Quarterly numbers" },
            Octets = 100,
        };

        Assert.Equal("Quarterly numbers.eml", ImapInboxAdapter.AttachmentName(forwarded));
    }

    [Fact]
    public void Imap_part_lookup_walks_nested_multiparts_and_forwarded_messages()
    {
        var inner = Part("2.1", "application/pdf", "inner.pdf", 10);
        var forwarded = new BodyPartMessage(ContentType.Parse("message/rfc822"), "2") { Body = inner };
        var root = new BodyPartMultipart(ContentType.Parse("multipart/mixed"), "");
        root.BodyParts.Add(Part("1", "text/plain", null, 10));
        root.BodyParts.Add(forwarded);

        Assert.Same(inner, ImapAttachmentFetcher.FindPart(root, "2.1"));
        Assert.Same(forwarded, ImapAttachmentFetcher.FindPart(root, "2"));
        Assert.Null(ImapAttachmentFetcher.FindPart(root, "3"));
    }

    [Theory]
    [InlineData("INBOX")]
    [InlineData("Archive/2026/Invoices")]
    public void Imap_stable_ids_round_trip_including_folders_with_slashes(string folder)
    {
        var account = ImapSettings.Create("imap.example.com", 993, true, "me@example.com", "pw");
        var id = ImapMessageLookup.StableId(account, folder, 77, 1234);

        Assert.True(ImapMessageLookup.TryParseStableId(id, account, out var f, out var validity, out var uid));
        Assert.Equal((folder, 77u, 1234u), (f, validity, uid));

        var other = ImapSettings.Create("imap.example.com", 993, true, "someone@example.com", "pw");
        Assert.False(ImapMessageLookup.TryParseStableId(id, other, out _, out _, out _));
        Assert.False(ImapMessageLookup.TryParseStableId("<rfc@example.com>", account, out _, out _, out _));
    }

    [Fact]
    public async Task A_limited_read_stops_past_the_limit()
    {
        Assert.Equal(10, (await AttachmentDownload.ReadLimitedAsync(new MemoryStream(new byte[10]), 10))!.Length);
        Assert.Null(await AttachmentDownload.ReadLimitedAsync(new MemoryStream(new byte[11]), 10));
    }
}

public sealed class GmailAttachmentFetcherTests
{
    private static string B64Url(byte[] bytes) => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>One message "m1" with an inline part "1", a part "2" stored as an attachment, and a nested part "3.1".</summary>
    private sealed class Mailbox : HttpMessageHandler
    {
        public string AttachmentData { get; set; } = B64Url("PDF bytes"u8.ToArray());
        public int DeclaredSize { get; set; } = 9;
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add(path);
            if (path.EndsWith("/messages/m1"))
                return Json($$$"""
                    {"id":"m1","payload":{"partId":"","mimeType":"multipart/mixed","parts":[
                      {"partId":"0","mimeType":"text/plain","body":{"size":2,"data":"aGk"}},
                      {"partId":"1","mimeType":"text/csv","filename":"small.csv","body":{"size":3,"data":"{{{B64Url("a,b"u8.ToArray())}}}"}},
                      {"partId":"2","mimeType":"application/pdf","filename":"a.pdf","body":{"size":{{{DeclaredSize}}},"attachmentId":"att-now"}},
                      {"partId":"3","mimeType":"multipart/mixed","parts":[
                        {"partId":"3.1","mimeType":"text/plain","filename":"n.txt","body":{"size":4,"data":"bmVzdA"}}]}]}}
                    """);
            if (path.EndsWith("/messages/m1/attachments/att-now"))
                return Json($$$"""{"size":{{{DeclaredSize}}},"data":"{{{AttachmentData}}}"}""");
            return Json("""{"error":{"code":404,"message":"Not Found"}}""", HttpStatusCode.NotFound);
        }

        private static Task<HttpResponseMessage> Json(string body, HttpStatusCode status = HttpStatusCode.OK)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class Factory(HttpMessageHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
    }

    private static GmailAttachmentFetcher Fetcher(Mailbox mailbox) =>
        new(GmailSettings.Create("id", "secret", "refresh", "me@example.com"))
        {
            ServiceForTests = () => new GmailService(new BaseClientService.Initializer
            {
                HttpClientFactory = new Factory(mailbox), GZipEnabled = false, ApplicationName = "test",
            }),
        };

    private static readonly AttachmentFetchLimits Limits = new(1000);

    [Fact]
    public async Task Fetches_an_inline_part_without_a_second_call()
    {
        var mailbox = new Mailbox();
        var result = await Fetcher(mailbox).FetchAsync(new AttachmentRef("m1", "1"), Limits);

        Assert.Equal(AttachmentFetchStatus.Fetched, result.Status);
        Assert.Equal(("small.csv", "text/csv", "a,b"), (result.Content!.Name, result.Content.ContentType, Encoding.UTF8.GetString(result.Content.Bytes)));
        Assert.Single(mailbox.Calls);
    }

    [Fact]
    public async Task Fetches_a_stored_attachment_with_the_current_attachment_id()
    {
        var result = await Fetcher(new Mailbox()).FetchAsync(new AttachmentRef("m1", "2"), Limits);

        Assert.Equal(AttachmentFetchStatus.Fetched, result.Status);
        Assert.Equal("PDF bytes", Encoding.UTF8.GetString(result.Content!.Bytes));
        Assert.Equal("application/pdf", result.Content.ContentType);
    }

    [Fact]
    public async Task Finds_nested_parts()
    {
        var result = await Fetcher(new Mailbox()).FetchAsync(new AttachmentRef("m1", "3.1"), Limits);
        Assert.Equal("nest", Encoding.UTF8.GetString(result.Content!.Bytes));
    }

    [Fact]
    public async Task Unknown_parts_and_messages_and_parts_without_a_file_name_are_missing()
    {
        var fetcher = Fetcher(new Mailbox());
        Assert.Equal(AttachmentFetchStatus.Missing, (await fetcher.FetchAsync(new AttachmentRef("m1", "9"), Limits)).Status);
        Assert.Equal(AttachmentFetchStatus.Missing, (await fetcher.FetchAsync(new AttachmentRef("gone", "1"), Limits)).Status);
        Assert.Equal(AttachmentFetchStatus.Missing, (await fetcher.FetchAsync(new AttachmentRef("m1", "0"), Limits)).Status);
    }

    [Fact]
    public async Task A_declared_size_over_the_limit_is_refused_before_downloading()
    {
        var mailbox = new Mailbox { DeclaredSize = 5000 };
        var result = await Fetcher(mailbox).FetchAsync(new AttachmentRef("m1", "2"), Limits);

        Assert.Equal(AttachmentFetchStatus.TooLarge, result.Status);
        Assert.DoesNotContain(mailbox.Calls, c => c.Contains("/attachments/"));
    }

    [Fact]
    public async Task Content_larger_than_declared_is_still_refused()
    {
        var mailbox = new Mailbox { AttachmentData = B64Url(new byte[2000]) };
        Assert.Equal(AttachmentFetchStatus.TooLarge, (await Fetcher(mailbox).FetchAsync(new AttachmentRef("m1", "2"), Limits)).Status);
    }

    [Fact]
    public async Task Bad_base64_is_a_failure_not_an_empty_file()
    {
        var mailbox = new Mailbox { AttachmentData = "not*base64!" };
        var result = await Fetcher(mailbox).FetchAsync(new AttachmentRef("m1", "2"), Limits);
        Assert.Equal(AttachmentFetchStatus.Failed, result.Status);
        Assert.Null(result.Content);
    }
}

public sealed class GraphAttachmentFetcherTests
{
    private const string Prefix = "/v1.0/users/me@example.com/messages/g1/attachments/";

    private sealed class Graph : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Body, string ContentType)> Routes { get; } = new();
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            Calls.Add(path);
            var (status, body, type) = Routes.TryGetValue(path, out var r)
                ? r
                : (HttpStatusCode.NotFound, """{"error":{"code":"ErrorItemNotFound","message":"not found"}}""", "application/json");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, type),
                RequestMessage = request,
            });
        }

        public void File(string id, string name, int size, string content, string odataType = "#microsoft.graph.fileAttachment")
        {
            Routes[Prefix + id] = (HttpStatusCode.OK,
                $$"""{"@odata.type":"{{odataType}}","id":"{{id}}","name":"{{name}}","contentType":"application/pdf","size":{{size}}}""",
                "application/json");
            Routes[Prefix + id + "/$value"] = (HttpStatusCode.OK, content, "application/octet-stream");
        }
    }

    private static GraphAttachmentFetcher Fetcher(Graph graph) =>
        new(GraphSettings.Create("t", "c", "s", "me@example.com"),
            new GraphServiceClient(new HttpClient(graph), new AnonymousAuthenticationProvider()));

    private static readonly AttachmentFetchLimits Limits = new(100);

    [Fact]
    public async Task Fetches_a_file_through_its_value_without_content_bytes_in_the_metadata_call()
    {
        var graph = new Graph();
        graph.File("a1", "a.pdf", 9, "PDF bytes");

        var result = await Fetcher(graph).FetchAsync(new AttachmentRef("g1", "a1"), Limits);

        Assert.Equal(AttachmentFetchStatus.Fetched, result.Status);
        Assert.Equal(("a.pdf", "application/pdf", "PDF bytes"), (result.Content!.Name, result.Content.ContentType, Encoding.UTF8.GetString(result.Content.Bytes)));
        Assert.Equal([Prefix + "a1", Prefix + "a1/$value"], graph.Calls);
    }

    [Fact]
    public async Task A_reference_attachment_is_not_a_file()
    {
        var graph = new Graph();
        graph.File("r1", "link", 1, "", "#microsoft.graph.referenceAttachment");
        Assert.Equal(AttachmentFetchStatus.NotAFile, (await Fetcher(graph).FetchAsync(new AttachmentRef("g1", "r1"), Limits)).Status);
        Assert.Single(graph.Calls);
    }

    [Fact]
    public async Task An_attached_message_is_handed_on_as_eml()
    {
        var graph = new Graph();
        graph.File("i1", "Fwd: numbers", 30, "Subject: numbers\r\n\r\nBody", "#microsoft.graph.itemAttachment");

        var result = await Fetcher(graph).FetchAsync(new AttachmentRef("g1", "i1"), Limits);

        Assert.Equal(("Fwd: numbers.eml", "message/rfc822"), (result.Content!.Name, result.Content.ContentType));
    }

    [Fact]
    public async Task Missing_attachments_and_messages_are_missing()
    {
        Assert.Equal(AttachmentFetchStatus.Missing, (await Fetcher(new Graph()).FetchAsync(new AttachmentRef("g1", "nope"), Limits)).Status);
    }

    [Fact]
    public async Task A_declared_size_over_the_limit_is_refused_before_downloading()
    {
        var graph = new Graph();
        graph.File("big", "big.pdf", 5000, "x");

        Assert.Equal(AttachmentFetchStatus.TooLarge, (await Fetcher(graph).FetchAsync(new AttachmentRef("g1", "big"), Limits)).Status);
        Assert.Single(graph.Calls);
    }

    [Fact]
    public async Task Content_larger_than_declared_is_still_refused()
    {
        var graph = new Graph();
        graph.File("liar", "small.pdf", 5, new string('x', 500));
        Assert.Equal(AttachmentFetchStatus.TooLarge, (await Fetcher(graph).FetchAsync(new AttachmentRef("g1", "liar"), Limits)).Status);
    }

    [Fact]
    public async Task A_value_that_answers_405_is_not_a_file()
    {
        var graph = new Graph();
        graph.File("x1", "x", 1, "");
        graph.Routes[Prefix + "x1/$value"] = (HttpStatusCode.MethodNotAllowed,
            """{"error":{"code":"ErrorInvalidRequest","message":"no"}}""", "application/json");
        Assert.Equal(AttachmentFetchStatus.NotAFile, (await Fetcher(graph).FetchAsync(new AttachmentRef("g1", "x1"), Limits)).Status);
    }
}
