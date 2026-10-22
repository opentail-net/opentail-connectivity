using System.Net;
using Azure.Core;
using OpenTail.Connectivity.Files;
using OpenTail.Connectivity.Drive.Graph;

namespace OpenTail.Connectivity.Tests.Drive;

public sealed class GraphDriveConnectorTests
{
    private const string Base = "https://graph.microsoft.com/v1.0/";

    // ---- mapping

    [Fact]
    public async Task Files_become_upserts_with_metadata_and_deleted_or_filtered_files_become_deletions()
    {
        var graph = new StubHttp(req => Json($$"""
            { "value": [
                { "id": "f1", "name": "Report.docx", "size": 1200, "lastModifiedDateTime": "2026-10-20T09:00:00Z",
                  "webUrl": "https://contoso.sharepoint.com/sites/hr/Report.docx", "file": { "hashes": { "quickXorHash": "qx1" } },
                  "createdBy": { "user": { "email": "ann@contoso.example" } } },
                { "id": "d1", "name": "Folder", "folder": {} },
                { "id": "x1", "deleted": {} },
                { "id": "dx", "folder": {}, "deleted": {} },
                { "id": "n1", "name": "Notes", "package": { "type": "oneNote" } },
                { "id": "r1", "name": "Shortcut", "remoteItem": {} },
                { "id": "big", "name": "huge.pdf", "size": 999999999, "file": {} },
                { "id": "exe", "name": "tool.exe", "size": 10, "file": {} }
              ],
              "@odata.deltaLink": "{{Base}}drives/drv/root/delta?token=T1" }
            """));
        var changes = await Connector(graph).GetChangesAsync(null).ToListAsync();

        var upsert = Assert.Single(changes.OfType<SourceChange.Upsert>()).Item;
        Assert.Equal("drv:f1", upsert.Id);
        Assert.Equal("docx", upsert.Extension);
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T09:00:00Z"), upsert.Modified);
        Assert.Equal("https://contoso.sharepoint.com/sites/hr/Report.docx", upsert.Metadata![SourceKeys.Url]);
        Assert.Equal("ann@contoso.example", upsert.Metadata[SourceKeys.Owner]);
        Assert.Equal("qx1", upsert.Metadata[SourceKeys.Version]);
        Assert.Equal("false", upsert.Metadata[SourceKeys.AclCaptured]);
        Assert.Equal("acme", upsert.TenantContext);

        var deletions = changes.OfType<SourceChange.Delete>().ToList();
        Assert.Contains(deletions, d => d.SourceRef == "drv:x1" && d.Reason == DeletionReason.Deleted && d.TenantContext == "acme");
        Assert.Contains(deletions, d => d.SourceRef == "drv:big" && d.Reason == DeletionReason.OutOfScope);
        Assert.Contains(deletions, d => d.SourceRef == "drv:exe" && d.Reason == DeletionReason.OutOfScope);
        Assert.Equal(3, deletions.Count); // folders, notebooks and shortcuts produce nothing, deleted or not
        Assert.IsType<SourceChange.Checkpoint>(changes[^1]);
    }

    // ---- paging, checkpoints, resets

    [Fact]
    public async Task Each_page_ends_in_a_checkpoint_and_a_resumed_run_starts_at_it()
    {
        var graph = new StubHttp(req => req.RequestUri!.Query.Contains("page=2")
            ? Json($$"""{ "value": [ { "id": "b", "name": "b.txt", "size": 1, "file": {} } ], "@odata.deltaLink": "{{Base}}drives/drv/root/delta?token=T2" }""")
            : Json($$"""{ "value": [ { "id": "a", "name": "a.txt", "size": 1, "file": {} } ], "@odata.nextLink": "{{Base}}drives/drv/root/delta?page=2" }"""));
        var connector = Connector(graph);

        var changes = await connector.GetChangesAsync(null).ToListAsync();
        var checkpoints = changes.OfType<SourceChange.Checkpoint>().Select(c => c.Cursor).ToList();
        Assert.Equal(2, checkpoints.Count);
        Assert.Contains("page=2", checkpoints[0]);
        Assert.Contains("token=T2", checkpoints[1]);
        Assert.Contains(graph.Requests, r => r.Contains("/drives/drv/root/delta") && r.Contains("$top=200"));

        graph.Requests.Clear();
        await connector.GetChangesAsync(checkpoints[0]).ToListAsync();
        Assert.Equal("page=2", new Uri(graph.Requests.Single()).Query.TrimStart('?'));
    }

    [Fact]
    public async Task An_expired_token_resets_and_lists_again_from_the_location_it_gives()
    {
        var graph = new StubHttp(req =>
        {
            if (req.RequestUri!.Query.Contains("token=OLD"))
            {
                var gone = new HttpResponseMessage(HttpStatusCode.Gone) { Content = new StringContent("""{"error":{"code":"resyncChangesApplyDifferences"}}""") };
                gone.Headers.Location = new Uri(Base + "drives/drv/root/delta?fresh=1");
                return gone;
            }
            return Json($$"""{ "value": [ { "id": "a", "name": "a.txt", "size": 1, "file": {} } ], "@odata.deltaLink": "{{Base}}drives/drv/root/delta?token=NEW" }""");
        });
        var cursor = JsonSerializer.Serialize(new { DriveId = "drv", Link = Base + "drives/drv/root/delta?token=OLD" });

        var changes = await Connector(graph).GetChangesAsync(cursor).ToListAsync();

        Assert.IsType<SourceChange.Reset>(changes[0]);
        Assert.Contains(graph.Requests, r => r.Contains("fresh=1"));
        Assert.Single(changes.OfType<SourceChange.Upsert>());
    }

    [Fact]
    public async Task A_cursor_for_another_drive_resets_and_a_tampered_cursor_is_refused()
    {
        var graph = new StubHttp(_ => Json($$"""{ "value": [], "@odata.deltaLink": "{{Base}}drives/drv/root/delta?token=T" }"""));

        var otherDrive = JsonSerializer.Serialize(new { DriveId = "old-drive", Link = Base + "drives/old-drive/root/delta?token=X" });
        var changes = await Connector(graph).GetChangesAsync(otherDrive).ToListAsync();
        Assert.IsType<SourceChange.Reset>(changes[0]);
        Assert.DoesNotContain(graph.Requests, r => r.Contains("old-drive"));

        var tampered = JsonSerializer.Serialize(new { DriveId = "drv", Link = "https://evil.example/steal" });
        await Assert.ThrowsAsync<InvalidDataException>(() => Connector(graph).GetChangesAsync(tampered).ToListAsync().AsTask());
        Assert.DoesNotContain(graph.Requests, r => r.Contains("evil"));
    }

    // ---- downloads

    [Fact]
    public async Task Downloads_go_without_the_graph_token_and_an_expired_url_is_refreshed_once()
    {
        var urls = 0;
        var graph = new StubHttp(req => Json($$"""{ "id": "f1", "@microsoft.graph.downloadUrl": "https://files.example/dl{{++urls}}" }"""));
        var download = new StubHttp(req => req.RequestUri!.AbsolutePath == "/dl1"
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("hello"u8.ToArray()) });

        var bytes = await Connector(graph, download: download).FetchAsync(Item("drv:f1"));

        Assert.Equal("hello", Encoding.UTF8.GetString(bytes));
        Assert.Equal(2, urls);
        Assert.All(download.Authorizations, a => Assert.Null(a));
        Assert.All(graph.Authorizations, a => Assert.StartsWith("Bearer ", a));
    }

    [Fact]
    public async Task Fetch_refuses_an_item_from_another_drive_without_calling_graph()
    {
        var graph = new StubHttp(_ => Json("""{ "id": "f1", "@microsoft.graph.downloadUrl": "https://files.example/dl" }"""));
        var download = new StubHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("secret"u8.ToArray()) });

        await Assert.ThrowsAsync<InvalidOperationException>(() => Connector(graph, download: download).FetchAsync(Item("other-drive:f1")));

        Assert.Empty(graph.Authorizations);
        Assert.Empty(download.Authorizations);
    }

    [Fact]
    public async Task Downloads_stop_at_the_size_limit_and_a_vanished_item_throws_cleanly()
    {
        var graph = new StubHttp(req => req.RequestUri!.AbsolutePath.EndsWith("/gone", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":{"code":"itemNotFound"}}""") }
            : Json("""{ "id": "f1", "@microsoft.graph.downloadUrl": "https://files.example/dl" }"""));
        var download = new StubHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) });
        var connector = Connector(graph, download: download, options: new GraphDriveOptions(Filter: new FileFilterOptions(MaxFileSizeBytes: 1024)));

        await Assert.ThrowsAsync<InvalidDataException>(() => connector.FetchAsync(Item("drv:f1")));
        await Assert.ThrowsAsync<FileNotFoundException>(() => connector.FetchAsync(Item("drv:gone")));
    }

    [Fact]
    public void A_download_client_with_an_authorization_header_is_refused()
    {
        var download = new HttpClient();
        download.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "x");

        Assert.Throws<ArgumentException>(() => new GraphDriveConnector(new FakeCredential(), new GraphDriveTarget.Drive("drv"), "acme", downloadClient: download));
    }

    // ---- permissions and throttling

    [Theory]
    [InlineData("""{"roles":["Files.ReadWrite.All"]}""")]
    [InlineData("""{"roles":["Sites.Read.All","Sites.FullControl.All"]}""")]
    [InlineData("""{"scp":"Files.Read Files.ReadWrite"}""")]
    public async Task A_token_that_can_write_is_refused_before_any_call(string claims)
    {
        var graph = new StubHttp(_ => Json("""{ "value": [] }"""));
        var connector = Connector(graph, credential: new FakeCredential(claims));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => connector.GetChangesAsync(null).ToListAsync().AsTask());
        Assert.Contains("write access", ex.Message);
        Assert.Empty(graph.Requests);
    }

    [Fact]
    public async Task Throttling_waits_for_retry_after_and_gives_up_past_the_ceiling()
    {
        var calls = 0;
        var waits = new List<TimeSpan>();
        var graph = new StubHttp(_ =>
        {
            if (calls++ == 0)
            {
                var throttled = new HttpResponseMessage((HttpStatusCode)429);
                throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
                return throttled;
            }
            return Json($$"""{ "value": [], "@odata.deltaLink": "{{Base}}drives/drv/root/delta?token=T" }""");
        });
        await Connector(graph, delay: (t, _) => { waits.Add(t); return Task.CompletedTask; }).GetChangesAsync(null).ToListAsync();
        Assert.Equal([TimeSpan.FromSeconds(3)], waits);

        var always = new StubHttp(_ =>
        {
            var throttled = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(4));
            return throttled;
        });
        var capped = Connector(always, delay: (_, _) => Task.CompletedTask, options: new GraphDriveOptions(MaxThrottleWait: TimeSpan.FromMinutes(5)));
        await Assert.ThrowsAsync<HttpRequestException>(() => capped.GetChangesAsync(null).ToListAsync().AsTask());
    }

    // ---- targets

    [Fact]
    public async Task A_site_library_resolves_through_the_site_and_its_drives()
    {
        var graph = new StubHttp(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.Contains("/sites/contoso.sharepoint.com:/sites/hr")) return Json("""{ "id": "site-1" }""");
            if (path.EndsWith("/sites/site-1/drives", StringComparison.Ordinal)) return Json("""{ "value": [ { "id": "other", "name": "Archive" }, { "id": "lib-1", "name": "Documents" } ] }""");
            return Json($$"""{ "value": [], "@odata.deltaLink": "{{Base}}drives/lib-1/root/delta?token=T" }""");
        });
        var connector = new GraphDriveConnector(new FakeCredential(), new GraphDriveTarget.SiteLibrary("https://contoso.sharepoint.com/sites/hr", "documents"),
            "acme", graphClient: new HttpClient(graph));

        await connector.GetChangesAsync(null).ToListAsync();

        Assert.Contains(graph.Requests, r => r.Contains("/drives/lib-1/root/delta"));
        Assert.Equal("graph-drive:acme:site:https://contoso.sharepoint.com/sites/hr|documents", connector.SyncScope);
    }

    // ---- through the pipeline

    [Fact]
    public async Task Through_the_pipeline_files_are_exported_deletions_applied_and_the_next_run_reads_only_changes()
    {
        var round = 1;
        var graph = new StubHttp(req =>
        {
            var uri = req.RequestUri!.ToString();
            if (uri.Contains("@microsoft.graph.downloadUrl")) return Json("""{ "id": "x", "@microsoft.graph.downloadUrl": "https://files.example/dl" }""");
            if (round == 1)
                return Json($$"""{ "value": [ { "id": "a", "name": "a.txt", "size": 80, "file": {} }, { "id": "b", "name": "b.txt", "size": 80, "file": {} } ], "@odata.deltaLink": "{{Base}}drives/drv/root/delta?token=R1" }""");
            Assert.Contains("token=R1", uri);
            return Json($$"""{ "value": [ { "id": "b", "deleted": {} } ], "@odata.deltaLink": "{{Base}}drives/drv/root/delta?token=R2" }""");
        });
        var download = new StubHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("a text file long enough to pass the validator easily, really") });
        var state = Path.Combine(Path.GetTempPath(), "otail-gdrive-" + Guid.NewGuid().ToString("N") + ".json");
        var exporter = new Recorder();
        try
        {
            var options = new PipelineOptions(SyncStore: new FileSyncCursorStore(state));
            var first = await new ConnectivityPipeline(Connector(graph, download: download), new TxtExtractor(), [exporter], options, "graph-drive").RunAsync();
            Assert.Equal(["drv:a", "drv:b"], exporter.Exported);
            Assert.True(first.SyncCommitted);

            round = 2;
            var second = await new ConnectivityPipeline(Connector(graph, download: download), new TxtExtractor(), [exporter], options, "graph-drive").RunAsync();
            Assert.Equal(1, second.ItemsDeleted);
            Assert.Equal(ConnectivityPipeline.DocumentId("graph-drive", "acme", "drv:b"), Assert.Single(exporter.Deleted).Id);
        }
        finally
        {
            File.Delete(state);
        }
    }

    // ---- helpers

    private static GraphDriveConnector Connector(StubHttp graph, StubHttp? download = null, GraphDriveOptions? options = null,
        TokenCredential? credential = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
        => new(credential ?? new FakeCredential(), new GraphDriveTarget.Drive("drv"), "acme", options,
            new HttpClient(graph), download is null ? null : new HttpClient(download), delay);

    private static SourceItem Item(string id) => new(id, "file.txt", "txt", 5, DateTimeOffset.UnixEpoch, "acme");

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public List<string?> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(Uri.UnescapeDataString(request.RequestUri!.ToString()));
            Authorizations.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>A token whose payload carries the given claims, like an Entra access token.</summary>
    private sealed class FakeCredential(string claims = """{"roles":["Sites.Selected"]}""") : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new("e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig",
                DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(GetToken(requestContext, cancellationToken));
    }

    private sealed class TxtExtractor : ITextExtractor
    {
        public TextExtractionResult Extract(string fileName, byte[] content) => new(true, Encoding.UTF8.GetString(content));
    }

    private sealed class Recorder : IConnectivityExporter, IDeletionExporter
    {
        public List<string> Exported { get; } = [];

        public List<DocumentDeletion> Deleted { get; } = [];

        public Task ExportAsync(NormalisedDocument document, CancellationToken cancellationToken = default)
        {
            Exported.Add(document.SourceRef);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(DocumentDeletion deletion, CancellationToken cancellationToken = default)
        {
            Deleted.Add(deletion);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
