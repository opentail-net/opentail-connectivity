using System.Net;
using OpenTail.Connectivity.Drive.Google;
using OpenTail.Connectivity.Files;

namespace OpenTail.Connectivity.Tests.Drive;

public sealed class GoogleDriveConnectorTests
{
    private const string ReadOnly = """{"scope":"https://www.googleapis.com/auth/drive.readonly"}""";

    // ---- first run: start token, listing, switch to changes

    [Fact]
    public async Task A_first_run_takes_the_start_token_first_lists_with_checkpoints_then_switches_to_changes()
    {
        var api = new StubApi(req =>
        {
            var u = Url(req);
            if (u.Contains("changes/startPageToken")) return Json("""{"startPageToken":"S1"}""");
            if (u.Contains("/files?") && u.Contains("pageToken=L2")) return Json("""{"files":[{"id":"b","name":"b.pdf","mimeType":"application/pdf","size":"10"}]}""");
            if (u.Contains("/files?")) return Json("""{"files":[{"id":"a","name":"a.pdf","mimeType":"application/pdf","size":"10"}],"nextPageToken":"L2"}""");
            return null;
        });

        var changes = await Shared(api).GetChangesAsync(null).ToListAsync();

        var driveRequests = api.Requests.Where(r => r.Contains("googleapis.com/drive/v3/") && !r.Contains("/drives/")).ToList();
        Assert.Contains("startPageToken", driveRequests[0]);                       // the token before the listing
        Assert.All(driveRequests, r => Assert.Contains("supportsAllDrives=true", r));
        Assert.All(driveRequests, r => Assert.Contains("includeItemsFromAllDrives=true", r));
        Assert.All(driveRequests, r => Assert.Contains("driveId=sd1", r));
        Assert.Contains(driveRequests, r => r.Contains("/files?") && r.Contains("corpora=drive"));

        var cursors = changes.OfType<SourceChange.Checkpoint>().Select(c => c.Cursor).ToList();
        Assert.Equal(2, cursors.Count);
        Assert.Contains("\"list\"", cursors[0]);
        Assert.Contains("L2", cursors[0]);
        Assert.Contains("\"changes\"", cursors[1]);
        Assert.Contains("S1", cursors[1]);
        Assert.Equal(["a", "b"], changes.OfType<SourceChange.Upsert>().Select(u => u.Item.Id));
    }

    [Fact]
    public async Task A_run_resumed_mid_listing_keeps_its_start_token_and_page()
    {
        var api = new StubApi(req => Url(req).Contains("/files?")
            ? Json("""{"files":[]}""")
            : null);
        var cursor = """{"Phase":"list","StartToken":"S0","ListPageToken":"L7"}""";

        var changes = await Shared(api).GetChangesAsync(cursor).ToListAsync();

        Assert.DoesNotContain(api.Requests, r => r.Contains("startPageToken"));
        Assert.Contains(api.Requests, r => r.Contains("pageToken=L7"));
        Assert.Contains("S0", Assert.Single(changes.OfType<SourceChange.Checkpoint>()).Cursor);
    }

    // ---- mapping

    [Fact]
    public async Task Files_map_to_downloads_exports_skips_and_deletions()
    {
        var api = new StubApi(req =>
        {
            var u = Url(req);
            if (u.Contains("changes/startPageToken")) return Json("""{"startPageToken":"S1"}""");
            if (u.Contains("/files?")) return Json("""
                {"files":[
                  {"id":"bin","name":"Report.pdf","mimeType":"application/pdf","size":"1200","md5Checksum":"m1","modifiedTime":"2026-10-20T09:00:00Z","webViewLink":"https://drive.google.com/bin","driveId":"sd1"},
                  {"id":"doc","name":"Plan.v2","mimeType":"application/vnd.google-apps.document","version":"12","driveId":"sd1"},
                  {"id":"sheet","name":"Budget","mimeType":"application/vnd.google-apps.spreadsheet","version":"3","driveId":"sd1"},
                  {"id":"dir","name":"Folder","mimeType":"application/vnd.google-apps.folder","driveId":"sd1"},
                  {"id":"short","name":"Link","mimeType":"application/vnd.google-apps.shortcut","driveId":"sd1"},
                  {"id":"form","name":"Survey","mimeType":"application/vnd.google-apps.form","driveId":"sd1"},
                  {"id":"bin2","name":"old.pdf","mimeType":"application/pdf","size":"5","trashed":true,"driveId":"sd1"},
                  {"id":"moved","name":"x.pdf","mimeType":"application/pdf","size":"5","driveId":"other"},
                  {"id":"exe","name":"tool.exe","mimeType":"application/octet-stream","size":"5","driveId":"sd1"}
                ]}
                """);
            return null;
        });

        var changes = await Shared(api).GetChangesAsync(null).ToListAsync();
        var items = changes.OfType<SourceChange.Upsert>().Select(u => u.Item).ToDictionary(i => i.Id);

        Assert.Equal(["bin", "doc", "sheet"], items.Keys.Order());
        Assert.Equal(1200, items["bin"].SizeBytes);
        Assert.Equal("m1", items["bin"].Metadata![SourceKeys.Version]);
        Assert.Equal("https://drive.google.com/bin", items["bin"].Metadata![SourceKeys.Url]);
        Assert.Equal("Engineering", items["bin"].Metadata![SourceKeys.Container]);
        Assert.Equal("Plan.v2.txt", items["doc"].DisplayName);                  // the export extension keeps "v2" in the title
        Assert.Equal("txt", items["doc"].Extension);
        Assert.Equal(0, items["doc"].SizeBytes);
        Assert.Equal("v12", items["doc"].Metadata![SourceKeys.Version]);
        Assert.Equal("Budget.xlsx", items["sheet"].DisplayName);

        var deletions = changes.OfType<SourceChange.Delete>().ToDictionary(d => d.SourceRef);
        Assert.Equal(DeletionReason.Deleted, deletions["bin2"].Reason);
        Assert.Equal(DeletionReason.OutOfScope, deletions["moved"].Reason);
        Assert.Equal(DeletionReason.OutOfScope, deletions["exe"].Reason);
        Assert.Equal(3, deletions.Count);
    }

    // ---- changes phase

    [Fact]
    public async Task Changes_report_removed_and_trashed_files_and_end_with_the_new_start_token()
    {
        var api = new StubApi(req =>
        {
            var u = Url(req);
            if (u.Contains("pageToken=C2")) return Json("""{"changes":[{"changeType":"file","fileId":"t","file":{"id":"t","name":"t.pdf","mimeType":"application/pdf","trashed":true,"driveId":"sd1"}}],"newStartPageToken":"C3"}""");
            if (u.Contains("/changes?")) return Json("""{"changes":[{"changeType":"file","fileId":"gone","removed":true},{"changeType":"drive","fileId":"x"},{"changeType":"file","fileId":"n","file":{"id":"n","name":"n.pdf","mimeType":"application/pdf","size":"3","driveId":"sd1"}}],"nextPageToken":"C2"}""");
            return null;
        });

        var changes = await Shared(api).GetChangesAsync("""{"Phase":"changes","PageToken":"C1"}""").ToListAsync();

        Assert.Contains(api.Requests, r => r.Contains("/changes?") && r.Contains("includeRemoved=true") && r.Contains("driveId=sd1") && r.Contains("supportsAllDrives=true"));
        Assert.Equal(["gone", "t"], changes.OfType<SourceChange.Delete>().Select(d => d.SourceRef));
        Assert.Equal(["n"], changes.OfType<SourceChange.Upsert>().Select(u => u.Item.Id));
        var cursors = changes.OfType<SourceChange.Checkpoint>().Select(c => c.Cursor).ToList();
        Assert.Contains("C2", cursors[0]);
        Assert.Contains("C3", cursors[1]);
    }

    [Fact]
    public async Task A_rejected_change_token_resets_and_lists_the_drive_again()
    {
        var api = new StubApi(req =>
        {
            var u = Url(req);
            if (u.Contains("/changes?")) return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":{"errors":[{"reason":"invalid","location":"pageToken"}]}}""") };
            if (u.Contains("changes/startPageToken")) return Json("""{"startPageToken":"S9"}""");
            if (u.Contains("/files?")) return Json("""{"files":[{"id":"a","name":"a.pdf","mimeType":"application/pdf","size":"1"}]}""");
            return null;
        });

        var changes = await Shared(api).GetChangesAsync("""{"Phase":"changes","PageToken":"BAD"}""").ToListAsync();

        Assert.IsType<SourceChange.Reset>(changes[0]);
        Assert.Single(changes.OfType<SourceChange.Upsert>());
        Assert.Contains("S9", changes.OfType<SourceChange.Checkpoint>().Last().Cursor);
    }

    // ---- My Drive

    [Fact]
    public async Task My_drive_lists_owned_files_and_treats_files_no_longer_owned_as_out_of_scope()
    {
        var api = new StubApi(req =>
        {
            var u = Url(req);
            if (u.Contains("changes/startPageToken")) return Json("""{"startPageToken":"S1"}""");
            if (u.Contains("/files?")) return Json("""{"files":[{"id":"mine","name":"a.pdf","mimeType":"application/pdf","size":"1","ownedByMe":true,"owners":[{"emailAddress":"ann@corp.example"}]},{"id":"theirs","name":"b.pdf","mimeType":"application/pdf","size":"1","ownedByMe":false}]}""");
            return null;
        });

        var changes = await Connector(api, new GoogleDriveTarget.MyDrive()).GetChangesAsync(null).ToListAsync();

        var list = Assert.Single(api.Requests, r => r.Contains("/files?"));
        Assert.Contains("corpora=user", list);
        Assert.Contains("'me' in owners", list);
        Assert.DoesNotContain("driveId=", list);
        var mine = Assert.Single(changes.OfType<SourceChange.Upsert>()).Item;
        Assert.Equal("ann@corp.example", mine.Metadata![SourceKeys.Owner]);
        Assert.Equal("mydrive:ann@corp.example", mine.Metadata[SourceKeys.Container]);
        Assert.Equal(DeletionReason.OutOfScope, Assert.Single(changes.OfType<SourceChange.Delete>()).Reason);
    }

    // ---- fetch

    [Fact]
    public async Task Binary_files_download_with_the_drive_flags_and_google_files_export()
    {
        var api = new StubApi(req => Url(req).Contains("/export?") || Url(req).Contains("alt=media")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("content") }
            : null);
        var connector = Shared(api);

        Assert.Equal("content", Encoding.UTF8.GetString(await connector.FetchAsync(Item("bin", "application/pdf"))));
        Assert.Equal("content", Encoding.UTF8.GetString(await connector.FetchAsync(Item("doc", "application/vnd.google-apps.document"))));
        Assert.Equal("content", Encoding.UTF8.GetString(await connector.FetchAsync(Item("sheet", "application/vnd.google-apps.spreadsheet"))));

        Assert.Contains(api.Requests, r => r.Contains("/files/bin?alt=media&supportsAllDrives=true"));
        Assert.Contains(api.Requests, r => r.Contains("/files/doc/export?mimeType=text/plain"));
        Assert.Contains(api.Requests, r => r.Contains("/files/sheet/export?mimeType=application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
    }

    [Fact]
    public async Task Downloads_stop_at_the_size_limit_and_a_vanished_file_throws_cleanly()
    {
        var api = new StubApi(req => Url(req).Contains("/files/gone")
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) });
        var connector = Connector(api, new GoogleDriveTarget.SharedDrive("sd1"), new GoogleDriveOptions(Filter: new FileFilterOptions(MaxFileSizeBytes: 1024)));

        await Assert.ThrowsAsync<InvalidDataException>(() => connector.FetchAsync(Item("big", "application/pdf")));
        await Assert.ThrowsAsync<FileNotFoundException>(() => connector.FetchAsync(Item("gone", "application/pdf")));
    }

    // ---- token scope and rate limits

    [Fact]
    public async Task A_token_with_more_than_read_only_drive_access_is_refused_before_any_drive_call()
    {
        var api = new StubApi(_ => Json("""{"files":[]}"""), tokenInfo: """{"scope":"openid https://www.googleapis.com/auth/drive"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Shared(api).GetChangesAsync(null).ToListAsync().AsTask());

        Assert.Contains("more than read-only", ex.Message);
        Assert.DoesNotContain(api.Requests, r => r.Contains("/drive/v3/"));
    }

    [Fact]
    public async Task Rate_limits_back_off_and_give_up_past_the_ceiling()
    {
        var calls = 0;
        var waits = new List<TimeSpan>();
        var api = new StubApi(req =>
        {
            if (!Url(req).Contains("startPageToken")) return Json("""{"files":[]}""");
            return calls++ == 0
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"error":{"errors":[{"reason":"userRateLimitExceeded"}]}}""") }
                : Json("""{"startPageToken":"S1"}""");
        });
        await Connector(api, new GoogleDriveTarget.SharedDrive("sd1"), delay: (t, _) => { waits.Add(t); return Task.CompletedTask; }).GetChangesAsync(null).ToListAsync();
        Assert.Single(waits);

        var always = new StubApi(_ => new HttpResponseMessage((HttpStatusCode)429));
        var capped = Connector(always, new GoogleDriveTarget.SharedDrive("sd1"), new GoogleDriveOptions(MaxThrottleWait: TimeSpan.FromSeconds(5)), (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<HttpRequestException>(() => capped.GetChangesAsync(null).ToListAsync().AsTask());
    }

    // ---- through the pipeline

    [Fact]
    public async Task Through_the_pipeline_files_are_exported_then_a_removed_file_is_deleted_next_run()
    {
        var api = new StubApi(req =>
        {
            var u = Url(req);
            if (u.Contains("changes/startPageToken")) return Json("""{"startPageToken":"S1"}""");
            if (u.Contains("/files?")) return Json("""{"files":[{"id":"a","name":"a.txt","mimeType":"text/plain","size":"70","driveId":"sd1"},{"id":"b","name":"b.txt","mimeType":"text/plain","size":"70","driveId":"sd1"}]}""");
            if (u.Contains("alt=media")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("a plain text file that is long enough to pass the document validator") };
            if (u.Contains("/changes?")) { Assert.Contains("pageToken=S1", u); return Json("""{"changes":[{"changeType":"file","fileId":"b","removed":true}],"newStartPageToken":"S2"}"""); }
            return null;
        });
        var state = Path.Combine(Path.GetTempPath(), "otail-gdrive-" + Guid.NewGuid().ToString("N") + ".json");
        var exporter = new Recorder();
        try
        {
            var options = new PipelineOptions(SyncStore: new FileSyncCursorStore(state));
            await new ConnectivityPipeline(Shared(api), new TxtExtractor(), [exporter], options, "gdrive").RunAsync();
            Assert.Equal(["a", "b"], exporter.Exported);

            var second = await new ConnectivityPipeline(Shared(api), new TxtExtractor(), [exporter], options, "gdrive").RunAsync();
            Assert.Equal(1, second.ItemsDeleted);
            Assert.Equal(ConnectivityPipeline.DocumentId("gdrive", "acme", "b"), Assert.Single(exporter.Deleted).Id);
        }
        finally
        {
            File.Delete(state);
        }
    }

    // ---- helpers

    private static GoogleDriveConnector Shared(StubApi api) => Connector(api, new GoogleDriveTarget.SharedDrive("sd1"));

    private static GoogleDriveConnector Connector(StubApi api, GoogleDriveTarget target, GoogleDriveOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        => new(_ => Task.FromResult("tok"), target, "acme", options, new HttpClient(api), delay);

    private static SourceItem Item(string id, string mime)
        => new(id, id, "pdf", 5, DateTimeOffset.UnixEpoch, "acme", new Dictionary<string, string> { ["gdrive.mimeType"] = mime });

    private static string Url(HttpRequestMessage req) => Uri.UnescapeDataString(req.RequestUri!.ToString());

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Answers token info and the drive's name itself; everything else goes to the test's function.</summary>
    private sealed class StubApi(Func<HttpRequestMessage, HttpResponseMessage?> respond, string tokenInfo = ReadOnly) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = Url(request);
            Requests.Add(url);
            if (url.Contains("oauth2.googleapis.com/tokeninfo")) return Task.FromResult(Json(tokenInfo));
            if (url.Contains("/drives/sd1")) return Task.FromResult(Json("""{"name":"Engineering"}"""));
            if (url.Contains("/about?")) return Task.FromResult(Json("""{"user":{"emailAddress":"ann@corp.example"}}"""));
            return Task.FromResult(respond(request) ?? new HttpResponseMessage(HttpStatusCode.NotImplemented) { Content = new StringContent("unexpected " + url) });
        }
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
