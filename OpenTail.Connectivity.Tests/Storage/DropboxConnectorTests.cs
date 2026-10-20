using System.Net;
using System.Text;
using OpenTail.Connectivity.Files;
using OpenTail.Connectivity.Files.Contracts;
using OpenTail.Connectivity.Files.Models;
using OpenTail.Connectivity.Files.Pipeline;
using OpenTail.Connectivity.Storage.Dropbox;

namespace OpenTail.Connectivity.Tests.Storage;

public sealed class DropboxConnectorTests
{
    private const string FileEntry = "{\".tag\":\"file\",\"path_display\":\"/root/report.txt\",\"size\":70,\"server_modified\":\"2026-10-01T10:00:00Z\",\"content_hash\":\"hash-1\"}";

    [Fact]
    public async Task First_run_pages_and_checkpoints_each_page()
    {
        var handler = new ApiHandler(request => request.Uri.AbsolutePath.EndsWith("/continue", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, $"{{\"entries\":[{FileEntry}],\"cursor\":\"c2\",\"has_more\":false}}")
            : Json(HttpStatusCode.OK, $"{{\"entries\":[{FileEntry}],\"cursor\":\"c1\",\"has_more\":true}}"));
        using var http = new HttpClient(handler);
        using var client = new DropboxObjectStoreClient(_ => Task.FromResult("token"), "/root", http);
        var connector = new DropboxConnector(client, "tenant", pageSize: 10);

        var changes = await connector.GetChangesAsync(null).ToListAsync();

        Assert.True(connector.ReportsDeletions);
        Assert.Equal(2, changes.OfType<SourceChange.Upsert>().Count());
        Assert.Equal(["c1", "c2"], changes.OfType<SourceChange.Checkpoint>().Select(c => c.Cursor));
        var item = Assert.IsType<SourceChange.Upsert>(changes[0]).Item;
        Assert.Equal("dropbox:///root/report.txt", item.Id);
        Assert.Equal("hash-1", item.Metadata![SourceKeys.Version]);
        Assert.Equal(new[] { "/2/files/list_folder", "/2/files/list_folder/continue" }, handler.Requests.Select(r => r.Uri.AbsolutePath));
    }

    [Fact]
    public async Task Incremental_run_reports_deletions_and_changed_files()
    {
        var handler = new ApiHandler(_ => Json(HttpStatusCode.OK,
            """{"entries":[{".tag":"deleted","path_display":"/root/gone.txt"},{".tag":"file","path_display":"/root/changed.txt","size":80,"server_modified":"2026-10-02T10:00:00Z","content_hash":"hash-2"}],"cursor":"c2","has_more":false}"""));
        using var http = new HttpClient(handler);
        using var client = new DropboxObjectStoreClient(_ => Task.FromResult("token"), "/root", http);
        var changes = await new DropboxConnector(client, "tenant").GetChangesAsync("saved-cursor").ToListAsync();

        var deletion = Assert.IsType<SourceChange.Delete>(Assert.Single(changes.OfType<SourceChange.Delete>()));
        Assert.Equal("dropbox:///root/gone.txt", deletion.SourceRef);
        Assert.Equal(DeletionReason.Deleted, deletion.Reason);
        Assert.Equal("tenant", deletion.TenantContext);
        Assert.Single(changes.OfType<SourceChange.Upsert>());
        Assert.Equal("saved-cursor", handler.Requests[0].Body.RootElement.GetProperty("cursor").GetString());
    }

    [Fact]
    public async Task Expired_cursor_resets_then_lists_from_root()
    {
        var handler = new ApiHandler(request => request.Uri.AbsolutePath.EndsWith("/continue", StringComparison.Ordinal)
            ? Json(HttpStatusCode.Conflict, "{\"error_summary\":\"reset/..\"}")
            : Json(HttpStatusCode.OK, $"{{\"entries\":[{FileEntry}],\"cursor\":\"fresh\",\"has_more\":false}}"));
        using var http = new HttpClient(handler);
        using var client = new DropboxObjectStoreClient(_ => Task.FromResult("token"), "/root", http);

        var changes = await new DropboxConnector(client, "tenant").GetChangesAsync("expired").ToListAsync();

        Assert.IsType<SourceChange.Reset>(changes[0]);
        Assert.Single(changes.OfType<SourceChange.Upsert>());
        Assert.Equal("fresh", Assert.Single(changes.OfType<SourceChange.Checkpoint>()).Cursor);
        Assert.Equal("/2/files/list_folder", handler.Requests[1].Uri.AbsolutePath);
    }

    [Fact]
    public async Task Filtered_file_becomes_out_of_scope_deletion()
    {
        var handler = new ApiHandler(_ => Json(HttpStatusCode.OK,
            """{"entries":[{".tag":"file","path_display":"/root/ignored.bin","size":12,"server_modified":"2026-10-02T10:00:00Z","content_hash":"hash"}],"cursor":"c","has_more":false}"""));
        using var http = new HttpClient(handler);
        using var client = new DropboxObjectStoreClient(_ => Task.FromResult("token"), "/root", http);

        var changes = await new DropboxConnector(client, "tenant").GetChangesAsync(null).ToListAsync();
        var change = Assert.IsType<SourceChange.Delete>(Assert.Single(changes.OfType<SourceChange.Delete>()));

        Assert.Equal("dropbox:///root/ignored.bin", change.SourceRef);
        Assert.Equal(DeletionReason.OutOfScope, change.Reason);
    }

    [Fact]
    public async Task Pipeline_sends_dropbox_deletion_to_exporter_with_matching_document_id()
    {
        var handler = new ApiHandler(request =>
        {
            if (request.Uri.AbsolutePath.EndsWith("/files/download", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("This is a sufficiently long plain text document for the pipeline validator.") };
            if (request.Uri.AbsolutePath.EndsWith("/continue", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """{"entries":[{".tag":"deleted","path_display":"/root/report.txt"}],"cursor":"c2","has_more":false}""");
            return Json(HttpStatusCode.OK, $"{{\"entries\":[{FileEntry}],\"cursor\":\"c1\",\"has_more\":false}}");
        });
        using var http = new HttpClient(handler);
        using var client = new DropboxObjectStoreClient(_ => Task.FromResult("token"), "/root", http);
        var connector = new DropboxConnector(client, "tenant");
        var statePath = Path.Combine(Path.GetTempPath(), "otail-dropbox-" + Guid.NewGuid().ToString("N") + ".json");
        var exporter = new RecordingExporter();
        var options = new PipelineOptions(SyncStore: new FileSyncCursorStore(statePath), Sync: new SyncOptions(CheckpointEveryItems: 1));
        try
        {
            await new ConnectivityPipeline(connector, new PlainTextExtractor(), [exporter], options, "dropbox").RunAsync();
            await new ConnectivityPipeline(connector, new PlainTextExtractor(), [exporter], options, "dropbox").RunAsync();
        }
        finally
        {
            File.Delete(statePath);
        }

        var deletion = Assert.Single(exporter.Deletions);
        Assert.Equal(ConnectivityPipeline.DocumentId("dropbox", "tenant", "dropbox:///root/report.txt"), deletion.Id);
        Assert.Equal("dropbox:///root/report.txt", deletion.SourceRef);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record SeenRequest(Uri Uri, System.Text.Json.JsonDocument Body);

    private sealed class ApiHandler(Func<SeenRequest, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<SeenRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            var seen = new SeenRequest(request.RequestUri!, System.Text.Json.JsonDocument.Parse(body));
            Requests.Add(seen);
            return respond(seen);
        }
    }

    private sealed class PlainTextExtractor : ITextExtractor
    {
        public TextExtractionResult Extract(string fileName, byte[] content) => new(true, Encoding.UTF8.GetString(content));
    }

    private sealed class RecordingExporter : IConnectivityExporter, IDeletionExporter
    {
        public List<DocumentDeletion> Deletions { get; } = [];
        public Task ExportAsync(NormalisedDocument document, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(DocumentDeletion deletion, CancellationToken cancellationToken = default) { Deletions.Add(deletion); return Task.CompletedTask; }
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
