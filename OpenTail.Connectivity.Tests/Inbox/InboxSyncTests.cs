using System.Net;
using MailKit;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;

namespace OpenTail.Connectivity.Tests.Inbox;

public sealed class ImapUidSelectionTests
{
    private static List<UniqueId> Uids(params uint[] ids) => [.. ids.Select(i => new UniqueId(i))];

    [Fact]
    public void A_first_run_takes_the_newest()
    {
        Assert.Equal([4u, 5u], ImapInboxAdapter.SelectUids(Uids(1, 2, 3, 4, 5), 2, incremental: false).Select(u => u.Id));
    }

    [Fact]
    public void An_incremental_run_takes_the_oldest_new_ones_so_a_backlog_is_not_skipped()
    {
        var waiting = Uids(11, 12, 13, 14, 15);
        Assert.Equal([11u, 12u], ImapInboxAdapter.SelectUids(waiting, 2, incremental: true).Select(u => u.Id));
    }

    [Fact]
    public void Fewer_than_the_maximum_takes_them_all()
    {
        Assert.Equal([7u, 8u], ImapInboxAdapter.SelectUids(Uids(7, 8), 10, incremental: true).Select(u => u.Id));
        Assert.Empty(ImapInboxAdapter.SelectUids(Uids(), 10, incremental: false));
    }
}

public sealed class GraphInboxSyncTests : IDisposable
{
    private const string Base = "https://graph.microsoft.com/v1.0/users/me%40example.com/mailFolders/inbox/messages/delta";
    private readonly string _stateFile = Path.Combine(Path.GetTempPath(), "otail-graph-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try { File.Delete(_stateFile); } catch (IOException) { }
    }

    private static string Page(IEnumerable<string> ids, string? next = null, string? delta = null)
    {
        var value = string.Join(",", ids.Select(id =>
            $$$"""{"id":"{{{id}}}","internetMessageId":"<{{{id}}}@example.com>","subject":"s {{{id}}}","receivedDateTime":"2026-10-01T10:00:00Z","body":{"contentType":"text","content":"b"}}"""));
        var links = (next is null ? "" : $",\"@odata.nextLink\":\"{next}\"") + (delta is null ? "" : $",\"@odata.deltaLink\":\"{delta}\"");
        return $"{{\"value\":[{value}]{links}}}";
    }

    private (GraphInboxAdapter Adapter, GraphSyncStateStore Store, StubGraph Graph) Create(InboxSyncCommit commit = InboxSyncCommit.AfterEnumeration)
    {
        var graph = new StubGraph();
        var client = new GraphServiceClient(new HttpClient(graph), new AnonymousAuthenticationProvider());
        var store = new GraphSyncStateStore(_stateFile);
        var adapter = new GraphInboxAdapter(GraphSettings.Create("t", "c", "s", "me@example.com"), "tenant", store, commit, client);
        return (adapter, store, graph);
    }

    private static async Task<List<string>> Subjects(GraphInboxAdapter adapter, int max, DateTimeOffset? since = null)
    {
        var list = new List<string>();
        await foreach (var m in adapter.FetchRecentAsync(new InboxFetchOptions("INBOX", max, since))) list.Add(m.Subject);
        return list;
    }

    [Fact]
    public async Task Reads_every_page_and_saves_the_delta_link_per_folder()
    {
        var (adapter, store, graph) = Create();
        graph.Respond(u => u.Contains("skiptoken=2") ? Page(["c"], delta: Base + "?$deltatoken=D") : Page(["a", "b"], next: Base + "?$skiptoken=2"));

        Assert.Equal(["s a", "s b", "s c"], await Subjects(adapter, 10));
        Assert.Equal(Base + "?$deltatoken=D", (await store.TryLoadAsync("me@example.com", "inbox"))!.DeltaLink);
        Assert.Null(await store.TryLoadAsync("me@example.com")); // not under the old per-mailbox key
        Assert.All(graph.Requests, r => Assert.Equal("odata.maxpagesize=10", r.Prefer));
    }

    [Fact]
    public async Task Stopping_at_MaxMessages_saves_the_next_link_so_the_next_run_continues()
    {
        var (adapter, store, graph) = Create();
        graph.Respond(u => u.Contains("skiptoken=2") ? Page(["c", "d"], delta: Base + "?$deltatoken=D") : Page(["a", "b"], next: Base + "?$skiptoken=2"));

        Assert.Equal(["s a", "s b"], await Subjects(adapter, 2));
        Assert.Equal(Base + "?$skiptoken=2", (await store.TryLoadAsync("me@example.com", "inbox"))!.DeltaLink);

        Assert.Equal(["s c", "s d"], await Subjects(adapter, 2));
        Assert.Equal(Base + "?$deltatoken=D", (await store.TryLoadAsync("me@example.com", "inbox"))!.DeltaLink);
    }

    [Fact]
    public async Task A_page_longer_than_asked_is_read_again_next_time_rather_than_cut()
    {
        var (adapter, store, graph) = Create();
        await store.SaveAsync(new GraphSyncState("me@example.com", Base + "?$deltatoken=OLD", DateTimeOffset.UtcNow, "inbox"));
        graph.Respond(_ => Page(["a", "b", "c"], delta: Base + "?$deltatoken=NEW")); // ignores the page size preference

        Assert.Equal(["s a", "s b"], await Subjects(adapter, 2));
        Assert.Equal(Base + "?$deltatoken=OLD", (await store.TryLoadAsync("me@example.com", "inbox"))!.DeltaLink);
    }

    [Fact]
    public async Task Removed_messages_are_skipped()
    {
        var (adapter, _, graph) = Create();
        graph.Respond(_ => """{"value":[{"id":"x","@removed":{"reason":"deleted"}},{"id":"a","subject":"s a"}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/d"}""");
        Assert.Equal(["s a"], await Subjects(adapter, 10));
    }

    [Fact]
    public async Task Since_becomes_a_received_date_filter()
    {
        var (adapter, _, graph) = Create();
        graph.Respond(_ => Page([], delta: Base + "?$deltatoken=D"));
        await Subjects(adapter, 10, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Contains("receivedDateTime ge 2026-09-01T00:00:00Z", Uri.UnescapeDataString(graph.Requests[0].Url));
    }

    [Fact]
    public async Task Manual_commit_saves_only_when_asked()
    {
        var (adapter, store, graph) = Create(InboxSyncCommit.Manual);
        graph.Respond(_ => Page(["a"], delta: Base + "?$deltatoken=D"));
        await Subjects(adapter, 10);
        Assert.True(adapter.HasPendingSyncState);
        Assert.Null(await store.TryLoadAsync("me@example.com", "inbox"));

        await adapter.CommitSyncStateAsync();
        Assert.False(adapter.HasPendingSyncState);
        Assert.Equal(Base + "?$deltatoken=D", (await store.TryLoadAsync("me@example.com", "inbox"))!.DeltaLink);
    }

    [Fact]
    public async Task Manual_commit_without_a_commit_replays_the_same_messages_next_run()
    {
        var (first, _, graph) = Create(InboxSyncCommit.Manual);
        graph.Respond(_ => Page(["a"], delta: Base + "?$deltatoken=D"));
        Assert.Equal(["s a"], await Subjects(first, 10));
        // The downstream store failed here, so CommitSyncStateAsync was never called.

        var (second, _, again) = Create(InboxSyncCommit.Manual);
        again.Respond(_ => Page(["a"], delta: Base + "?$deltatoken=D"));
        Assert.Equal(["s a"], await Subjects(second, 10));
        Assert.DoesNotContain("deltatoken=D", again.Requests[0].Url);
    }

    [Fact]
    public void A_sync_store_without_a_chosen_commit_mode_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new GraphInboxAdapter(GraphSettings.Create("t", "c", "s", "me@example.com"), "tenant",
            new GraphSyncStateStore(_stateFile)));
        Assert.Throws<ArgumentException>(() => new ImapInboxAdapter(new ImapSettings("imap.example.com", 993, true, "u", "p"), "tenant",
            new ImapSyncStateStore(_stateFile)));
        _ = new GraphInboxAdapter(GraphSettings.Create("t", "c", "s", "me@example.com"), "tenant"); // no store: nothing to commit
    }

    [Fact]
    public async Task State_saved_per_mailbox_before_folders_is_used_for_the_inbox()
    {
        var (adapter, store, graph) = Create();
        await store.SaveAsync(new GraphSyncState("me@example.com", Base + "?$deltatoken=LEGACY", DateTimeOffset.UtcNow));
        graph.Respond(_ => Page(["a"], delta: Base + "?$deltatoken=D"));
        await Subjects(adapter, 10);
        Assert.Contains("deltatoken=LEGACY", graph.Requests[0].Url);
    }

    internal sealed record SeenRequest(string Url, string? Prefer);

    internal sealed class StubGraph : HttpMessageHandler
    {
        private Func<string, string> respond = _ => "{\"value\":[]}";

        public List<SeenRequest> Requests { get; } = [];

        public void Respond(Func<string, string> body) => respond = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(new SeenRequest(url, request.Headers.TryGetValues("Prefer", out var p) ? string.Join(",", p) : null));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(url), Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }
}

public sealed class GmailScopeTests
{
    private static readonly GmailSettings Settings = GmailSettings.Create("c", "s", "r", "me@example.com");

    [Fact]
    public void The_provider_asks_for_the_scopes_it_is_given()
    {
        Assert.Equal(GmailScopes.ReadOnly, new GmailCredentialProvider(Settings, scopes: GmailScopes.ReadOnly).Scopes);
        Assert.Equal(GmailScopes.Full, new GmailCredentialProvider(Settings).Scopes);
    }

    [Fact]
    public void Send_without_read_has_no_modify_scope()
    {
        Assert.DoesNotContain(GmailScopes.SendWithoutRead, s => s.Contains("modify", StringComparison.Ordinal));
        Assert.Contains(GmailScopes.SendOnly, s => s.Contains("modify", StringComparison.Ordinal));
    }
}
