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
        var cursor = $$"""{"Phase":"list","StartToken":"S0","ListPageToken":"L7","Options":"{{Shared(api).OptionsFingerprint}}"}""";

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
    public async Task Changed_export_formats_or_filter_reset_to_a_full_listing()
    {
        var api = new StubApi(req => Url(req).Contains("startPageToken") ? Json("""{"startPageToken":"S9"}""") : Json("""{"files":[]}"""));
        var cursor = $$"""{"Phase":"changes","PageToken":"C1","Options":"{{Shared(api).OptionsFingerprint}}"}""";
        var exports = new Dictionary<string, string> { ["application/vnd.google-apps.document"] = "application/pdf" };
        var filtered = new GoogleDriveOptions(Filter: new FileFilterOptions(IncludePatterns: ["*.pdf"]));

        foreach (var options in new[] { new GoogleDriveOptions(ExportFormats: exports), filtered })
        {
            var changes = await Connector(api, new GoogleDriveTarget.SharedDrive("sd1"), options).GetChangesAsync(cursor).ToListAsync();
            Assert.IsType<SourceChange.Reset>(changes[0]);
        }
        Assert.DoesNotContain(api.Requests, r => r.Contains("pageToken=C1"));
    }

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

        var changes = await Shared(api).GetChangesAsync($$"""{"Phase":"changes","PageToken":"C1","Options":"{{Shared(api).OptionsFingerprint}}"}""").ToListAsync();

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

        var changes = await Shared(api).GetChangesAsync($$"""{"Phase":"changes","PageToken":"BAD","Options":"{{Shared(api).OptionsFingerprint}}"}""").ToListAsync();

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
        var api = new StubApi(req => FileMeta(req) ?? (Url(req).Contains("/export?") || Url(req).Contains("alt=media")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("content") }
            : null));
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
            : FileMeta(req) ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) });
        var connector = Connector(api, new GoogleDriveTarget.SharedDrive("sd1"), new GoogleDriveOptions(Filter: new FileFilterOptions(MaxFileSizeBytes: 1024)));

        await Assert.ThrowsAsync<InvalidDataException>(() => connector.FetchAsync(Item("big", "application/pdf")));
        await Assert.ThrowsAsync<FileNotFoundException>(() => connector.FetchAsync(Item("gone", "application/pdf")));
    }

    [Fact]
    public async Task Fetch_refuses_an_item_outside_the_configured_drive()
    {
        var api = new StubApi(req => Url(req).Contains("fields=mimeType,driveId,ownedByMe")
            ? Json("""{"mimeType":"application/pdf","driveId":"other","ownedByMe":true}""")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("secret") });

        await Assert.ThrowsAsync<InvalidOperationException>(() => Shared(api).FetchAsync(Item("forged", "application/pdf")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Connector(new StubApi(req => Url(req).Contains("fields=mimeType")
                ? Json("""{"mimeType":"application/pdf","ownedByMe":false}""") : null), new GoogleDriveTarget.MyDrive())
                .FetchAsync(Item("shared-with-me", "application/pdf")));
        Assert.DoesNotContain(api.Requests, r => r.Contains("alt=media"));
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
            if (FileMeta(req) is { } meta) return meta;
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

    // ---- folder scoping

    [Fact]
    public async Task A_root_folder_keeps_files_below_it_with_paths_and_reports_the_rest_out_of_scope()
    {
        var api = new StubApi(req =>
        {
            var u = Url(req);
            if (u.Contains("startPageToken")) return Json("""{"startPageToken":"S1"}""");
            if (u.Contains("/files/p26?")) return Json("""{"id":"p26","mimeType":"application/vnd.google-apps.folder","driveId":"sd1"}""");
            if (u.Contains("/files/sub?")) return Json("""{"id":"sub","name":"Sub","parents":["p26"]}""");
            return Json("""
                {"files":[
                  {"id":"pr","name":"Projects","mimeType":"application/vnd.google-apps.folder","parents":["sd1"]},
                  {"id":"p26","name":"2026","mimeType":"application/vnd.google-apps.folder","parents":["pr"]},
                  {"id":"ot","name":"Other","mimeType":"application/vnd.google-apps.folder","parents":["sd1"]},
                  {"id":"f1","name":"plan.txt","mimeType":"text/plain","size":"1","parents":["p26"]},
                  {"id":"f2","name":"deep.txt","mimeType":"text/plain","size":"1","parents":["sub"]},
                  {"id":"f3","name":"skip.txt","mimeType":"text/plain","size":"1","parents":["sub"]},
                  {"id":"f4","name":"else.txt","mimeType":"text/plain","size":"1","parents":["ot"]}
                ]}
                """);
        });
        var connector = Connector(api, new GoogleDriveTarget.SharedDrive("sd1"),
            new GoogleDriveOptions(RootFolderId: "p26", Filter: new FileFilterOptions(ExcludePatterns: ["Sub/skip.txt"])));

        var changes = await connector.GetChangesAsync(null).ToListAsync();

        Assert.Equal(["f1", "f2"], changes.OfType<SourceChange.Upsert>().Select(u => u.Item.Id));
        Assert.Equal(["f3", "f4"], changes.OfType<SourceChange.Delete>().Where(d => d.Reason == DeletionReason.OutOfScope).Select(d => d.SourceRef));
        Assert.Single(api.Requests, r => r.Contains("/files/sub?")); // looked up once, then remembered
        Assert.Contains("\"sub\":{\"p\":\"p26\",\"n\":\"Sub\"}", changes.OfType<SourceChange.Checkpoint>().Last().Cursor);
    }

    [Fact]
    public async Task A_root_folder_that_is_missing_or_in_another_drive_stops_the_run()
    {
        var missing = new StubApi(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        var elsewhere = new StubApi(_ => Json("""{"id":"x","mimeType":"application/vnd.google-apps.folder","driveId":"other"}"""));

        foreach (var api in new[] { missing, elsewhere })
            await Assert.ThrowsAsync<InvalidOperationException>(() => Connector(api, new GoogleDriveTarget.SharedDrive("sd1"),
                new GoogleDriveOptions(RootFolderId: "x")).GetChangesAsync(null).ToListAsync().AsTask());
        Assert.DoesNotContain(missing.Requests.Concat(elsewhere.Requests), r => r.Contains("startPageToken"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_renamed_folder_lists_the_drive_again_only_when_paths_decide_scope(bool withPatterns)
    {
        var api = new StubApi(req =>
        {
            var u = Url(req);
            if (u.Contains("startPageToken")) return Json("""{"startPageToken":"S9"}""");
            if (u.Contains("pageToken=C1"))
                return Json("""{"newStartPageToken":"C2","changes":[{"changeType":"file","fileId":"a","file":{"id":"a","name":"New","mimeType":"application/vnd.google-apps.folder","parents":["sd1"]}}]}""");
            return Json("""{"files":[]}""");
        });
        var options = withPatterns ? new GoogleDriveOptions(Filter: new FileFilterOptions(ExcludePatterns: ["Old/**"])) : null;
        var connector = Connector(api, new GoogleDriveTarget.SharedDrive("sd1"), options);
        var cursor = $$"""{"Phase":"changes","PageToken":"C1","Options":"{{connector.OptionsFingerprint}}","Folders":{"a":{"p":"sd1","n":"Old"} """ + "}}";

        var changes = await connector.GetChangesAsync(cursor).ToListAsync();

        Assert.Equal(withPatterns, changes.Any(c => c is SourceChange.Reset));
        Assert.Equal(withPatterns, api.Requests.Any(r => r.Contains("startPageToken")));
        if (!withPatterns)
            Assert.Contains("\"a\":{\"p\":\"sd1\",\"n\":\"New\"}", changes.OfType<SourceChange.Checkpoint>().Last().Cursor);
    }

    [Theory]
    [InlineData("sub", true)]
    [InlineData("p26", true)]
    [InlineData("ot", false)]
    [InlineData("sd1", false)]
    public async Task Fetch_refuses_a_file_outside_the_root_folder(string parent, bool allowed)
    {
        var api = new StubApi(req =>
        {
            var u = Url(req);
            if (u.Contains("fields=mimeType,driveId,ownedByMe")) return Json($$"""{"mimeType":"text/plain","driveId":"sd1","name":"f","parents":["{{parent}}"]}""");
            if (u.Contains("/files/p26?")) return Json("""{"id":"p26","name":"2026","mimeType":"application/vnd.google-apps.folder","driveId":"sd1","parents":["sd1"]}""");
            if (u.Contains("/files/sub?")) return Json("""{"id":"sub","name":"Sub","parents":["p26"]}""");
            if (u.Contains("/files/ot?")) return Json("""{"id":"ot","name":"Other","parents":["sd1"]}""");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("hi") };
        });
        var connector = Connector(api, new GoogleDriveTarget.SharedDrive("sd1"), new GoogleDriveOptions(RootFolderId: "p26"));

        if (allowed)
            Assert.Equal("hi"u8.ToArray(), await connector.FetchAsync(Item("f", "text/plain")));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => connector.FetchAsync(Item("f", "text/plain")));
        Assert.Equal(allowed, api.Requests.Any(r => r.Contains("alt=media")));
    }

    [Fact]
    public async Task My_drive_state_written_for_another_account_is_never_reused()
    {
        var api = new StubApi(req =>
        {
            var u = Url(req);
            if (u.Contains("changes/startPageToken")) return Json("""{"startPageToken":"S1"}""");
            if (u.Contains("/changes?")) return Json("""{"changes":[],"newStartPageToken":"S1"}""");
            if (u.Contains("/files?")) return Json("""{"files":[]}""");
            return null;
        });
        var cursor = (await Connector(api, new GoogleDriveTarget.MyDrive()).GetChangesAsync(null).ToListAsync())
            .OfType<SourceChange.Checkpoint>().Last().Cursor;
        Assert.Contains("ann@corp.example", cursor);

        var own = await Connector(api, new GoogleDriveTarget.MyDrive()).GetChangesAsync(cursor).ToListAsync();
        var foreign = await Connector(api, new GoogleDriveTarget.MyDrive()).GetChangesAsync(cursor.Replace("ann@corp.example", "bob@corp.example")).ToListAsync();

        Assert.DoesNotContain(own, c => c is SourceChange.Reset);
        Assert.IsType<SourceChange.Reset>(foreign[0]);
    }

    [Fact]
    public async Task A_my_drive_named_for_one_user_refuses_a_token_for_another()
    {
        var api = new StubApi(_ => null);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Connector(api, new GoogleDriveTarget.MyDrive("bob@corp.example")).GetChangesAsync(null).ToListAsync().AsTask());
        Assert.Contains("ann@corp.example", error.Message);
    }

    // ---- helpers

    private static GoogleDriveConnector Shared(StubApi api) => Connector(api, new GoogleDriveTarget.SharedDrive("sd1"));

    private static GoogleDriveConnector Connector(StubApi api, GoogleDriveTarget target, GoogleDriveOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
        => new(_ => Task.FromResult("tok"), target, "acme", options, new HttpClient(api), delay);

    private static SourceItem Item(string id, string mime)
        => new(id, id, "pdf", 5, DateTimeOffset.UnixEpoch, "acme", new Dictionary<string, string> { ["gdrive.mimeType"] = mime });

    // Answers FetchAsync's metadata lookup: every file is in shared drive sd1; the id picks the mime type.
    private static HttpResponseMessage? FileMeta(HttpRequestMessage req)
    {
        var u = Url(req);
        if (!u.Contains("fields=mimeType,driveId,ownedByMe")) return null;
        var mime = u.Contains("/files/doc?") ? "application/vnd.google-apps.document"
            : u.Contains("/files/sheet?") ? "application/vnd.google-apps.spreadsheet"
            : u.Contains("/files/bin?") || u.Contains("/files/big?") ? "application/pdf"
            : "text/plain";
        return Json($$"""{"mimeType":"{{mime}}","driveId":"sd1"}""");
    }

    private static string Url(HttpRequestMessage req) => Uri.UnescapeDataString(req.RequestUri!.ToString());

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Answers token info and the drive's name itself; everything else goes to the test's function.</summary>
    // ---- conformance

    [Fact]
    public async Task Passes_the_connector_conformance_suite() =>
        await OpenTail.Connectivity.Files.Conformance.SourceConnectorConformance.RunAsync(new ConformanceFixture());

    /// <summary>A shared drive of three files on a stubbed API: the changes feed reports nothing new, a removed file is a 404.</summary>
    private sealed class ConformanceFixture : OpenTail.Connectivity.Files.Conformance.IConnectorFixture
    {
        private readonly HashSet<string> _files = new(StringComparer.Ordinal) { "a", "b", "c" };

        public IReadOnlyList<string> SecretsGiven => [];

        public ISourceConnector Create() => Build(empty: false);

        public ISourceConnector CreateEmpty() => Build(empty: true);

        public Task RemoveOneItemAsync(string sourceRef)
        {
            _files.Remove(sourceRef);
            return Task.CompletedTask;
        }

        private GoogleDriveConnector Build(bool empty) => Shared(new StubApi(req =>
        {
            var u = Url(req);
            if (u.Contains("changes/startPageToken")) return Json("""{"startPageToken":"S1"}""");
            if (u.Contains("/changes?")) return Json("""{"changes":[],"newStartPageToken":"S1"}""");
            if (u.Contains("/files?"))
            {
                var files = empty ? "" : string.Join(",", _files.Order(StringComparer.Ordinal).Select(id =>
                    $$"""{"id":"{{id}}","name":"{{id}}.txt","mimeType":"text/plain","size":"40","modifiedTime":"2026-10-20T09:00:00Z","driveId":"sd1"}"""));
                return Json($$"""{"files":[{{files}}]}""");
            }
            var id = req.RequestUri!.AbsolutePath[(req.RequestUri.AbsolutePath.LastIndexOf('/') + 1)..];
            if (!_files.Contains(id)) return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            if (FileMeta(req) is { } meta) return meta;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("A file body that is long enough for validation.") };
        }));
    }

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
