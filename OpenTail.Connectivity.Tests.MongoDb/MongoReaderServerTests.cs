using System.Collections.Concurrent;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using OpenTail.Connectivity.MongoDb;
using Xunit;

namespace OpenTail.Connectivity.Tests.MongoDb;

/// <summary>
/// <see cref="MongoReader"/> against a real server. <c>OTAIL_MONGODB_ADMIN</c> is a root connection string (for setting up data);
/// <c>OTAIL_MONGODB_READ</c> a connection string, without its password, for a user with only the <c>read</c> role on <c>otail</c>;
/// <c>OTAIL_MONGODB_READ_PASSWORD</c> its password, passed separately as in production.
/// </summary>
public sealed class MongoReaderServerTests : IAsyncLifetime
{
    private static string? Admin => Environment.GetEnvironmentVariable("OTAIL_MONGODB_ADMIN");
    private static string? Read => Environment.GetEnvironmentVariable("OTAIL_MONGODB_READ");
    private static string? ReadPassword => Environment.GetEnvironmentVariable("OTAIL_MONGODB_READ_PASSWORD");
    private static bool Configured => Admin is { Length: > 0 } && Read is { Length: > 0 } && ReadPassword is { Length: > 0 };

    private readonly string _orders = "orders_" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _wide = "wide_" + Guid.NewGuid().ToString("N")[..8];

    public async ValueTask InitializeAsync()
    {
        if (!Configured) return;
        var db = new MongoClient(Admin).GetDatabase("otail");
        var regions = new[] { "north", "south", "east" };
        await db.GetCollection<BsonDocument>(_orders).InsertManyAsync(Enumerable.Range(1, 300).Select(i => new BsonDocument
        {
            ["n"] = i,
            ["region"] = regions[i % 3],
            ["status"] = i % 4 == 0 ? "open" : "closed",
            ["total"] = i * 10,
            ["created"] = new BsonDateTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i)),
        }));
        await db.GetCollection<BsonDocument>(_wide).InsertManyAsync(Enumerable.Range(1, 200).Select(i => new BsonDocument
        {
            ["n"] = i, ["a"] = new string('a', 5000), ["b"] = new string('b', 5000), ["c"] = new string('c', 5000),
        }));
    }

    public async ValueTask DisposeAsync()
    {
        if (!Configured) return;
        var db = new MongoClient(Admin).GetDatabase("otail");
        await db.DropCollectionAsync(_orders);
        await db.DropCollectionAsync(_wide);
    }

    private static void RequireServer() => Assert.SkipUnless(Configured, "Set OTAIL_MONGODB_ADMIN, OTAIL_MONGODB_READ and OTAIL_MONGODB_READ_PASSWORD.");

    private MongoReader Reader(MongoReaderOptions? options = null)
        => new(MongoReader.Connect(Read!, ReadPassword, "otail"), [_orders, _wide], options ?? new MongoReaderOptions(MaxRows: 50));

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task The_four_operations_read_through_the_read_only_login()
    {
        RequireServer();
        var reader = Reader();

        var listing = await reader.CollectionsAsync(ct: TestContext.Current.CancellationToken);
        var orders = Assert.Single(listing.Collections, c => c.Collection == _orders);
        Assert.Contains(orders.Fields, f => f.Field == "region" && f.Types.Contains("string"));

        var found = await reader.FindAsync(_orders, J("""{ "status": "open", "total": { "$gte": 1000 } }"""), J("""{ "_id": 0, "n": 1, "total": 1 }"""),
            J("""{ "total": -1 }"""), limit: 3, ct: TestContext.Current.CancellationToken);
        Assert.Equal([3000L, 2960L, 2920L], found.Rows.Select(r => Convert.ToInt64(((Dictionary<string, object?>)r!)["total"])));
        Assert.True(found.Truncated); // more than 3 matched

        var grouped = await reader.AggregateAsync(_orders, J("""[ { "$group": { "_id": "$region", "n": { "$sum": 1 } } }, { "$sort": { "_id": 1 } } ]"""),
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(["east", "north", "south"], grouped.Rows.Select(r => (string?)((Dictionary<string, object?>)r!)["_id"]));

        Assert.Equal(75, await reader.CountAsync(_orders, J("""{ "status": "open" }"""), ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Refused_queries_and_other_collections_never_reach_the_server()
    {
        RequireServer();
        var reader = Reader();
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<ArgumentException>(() => reader.FindAsync(_orders, J("""{ "$where": "sleep(5000) || true" }"""), ct: ct));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.AggregateAsync(_orders, J($$"""[ { "$out": "{{_orders}}_copy" } ]"""), ct: ct));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.AggregateAsync(_orders, J("""[ { "$lookup": { "from": "system.users", "localField": "a", "foreignField": "b", "as": "u" } } ]"""), ct: ct));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.CountAsync("system.users", ct: ct));
        var names = await (await new MongoClient(Admin).GetDatabase("otail").ListCollectionNamesAsync(cancellationToken: ct)).ToListAsync(ct);
        Assert.DoesNotContain(_orders + "_copy", names);
    }

    [Fact]
    public async Task A_large_result_is_cut_at_the_response_size()
    {
        RequireServer();
        var r = await Reader().FindAsync(_wide, ct: TestContext.Current.CancellationToken);
        Assert.True(r.Truncated);
        Assert.Contains("KB", r.TruncatedBecause);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(r.Rows).Length <= 256 * 1024);
        Assert.All(r.Rows, row => Assert.True(((string)((Dictionary<string, object?>)row!)["a"]!).Length <= 2001)); // strings cut
    }

    [Fact]
    public async Task The_collections_listing_is_bounded_too()
    {
        RequireServer();
        var listing = await Reader(new MongoReaderOptions(MaxResponseBytes: 1024, MaxFieldsPerCollection: 2)).CollectionsAsync(ct: TestContext.Current.CancellationToken);
        Assert.All(listing.Collections, c => Assert.True(c.Fields.Count <= 2));
        Assert.Contains(listing.Collections, c => c.FieldsTruncated);
    }

    [Fact]
    public async Task The_read_login_cannot_write()
    {
        RequireServer();
        var orders = MongoReader.Connect(Read!, ReadPassword, "otail").GetCollection<BsonDocument>(_orders);
        await Assert.ThrowsAnyAsync<MongoException>(() => orders.InsertOneAsync(new BsonDocument("n", -1), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(300, await new MongoClient(Admin).GetDatabase("otail").GetCollection<BsonDocument>(_orders)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Every_operation_carries_the_server_time_limit_and_the_comment()
    {
        RequireServer();
        var sent = new ConcurrentQueue<BsonDocument>();
        var url = new MongoUrlBuilder(Read) { Password = ReadPassword };
        var settings = MongoClientSettings.FromUrl(url.ToMongoUrl());
        settings.ClusterConfigurator = cb => cb.Subscribe<CommandStartedEvent>(e =>
        {
            // The driver's command is a raw document it disposes after the event: keep a full copy.
            if (e.CommandName is "find" or "aggregate" or "listCollections")
                sent.Enqueue(MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonDocument>(e.Command.ToBson()));
        });
        var reader = new MongoReader(new MongoClient(settings).GetDatabase("otail"), [_orders], new MongoReaderOptions(CommandTimeoutSeconds: 7));
        var ct = TestContext.Current.CancellationToken;

        await reader.FindAsync(_orders, limit: 1, comment: "opentail:find-1", ct: ct);
        await reader.AggregateAsync(_orders, J("""[ { "$match": {} } ]"""), "opentail:agg-1", ct);
        await reader.CollectionsAsync("opentail:list-1", ct);

        var commands = sent.ToList();
        Assert.Contains(commands, c => c.Contains("find") && c["maxTimeMS"].ToInt64() == 7000 && c["comment"].AsString == "opentail:find-1");
        Assert.Contains(commands, c => c.Contains("aggregate") && c["maxTimeMS"].ToInt64() == 7000 && c["comment"].AsString == "opentail:agg-1"
                                       && c["allowDiskUse"].AsBoolean == false && c["pipeline"].AsBsonArray.Last().AsBsonDocument.Contains("$limit"));
        Assert.Contains(commands, c => c.Contains("listCollections") && c["comment"].AsString == "opentail:list-1");
    }
}

/// <summary>Checks that need no server, so this project never runs zero tests (which the test platform reports as an error).</summary>
public sealed class MongoReaderConfigurationTests
{
    private static IMongoDatabase Unconnected => new MongoClient("mongodb://localhost:1").GetDatabase("x"); // the driver connects lazily

    [Fact]
    public void System_collections_and_an_empty_list_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new MongoReader(Unconnected, ["system.users"]));
        Assert.Throws<ArgumentException>(() => new MongoReader(Unconnected, []));
    }

    [Fact]
    public void Unsafe_limits_are_refused_when_the_reader_is_made()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new MongoReader(Unconnected, ["orders"], new MongoReaderOptions(CommandTimeoutSeconds: 0)));

    [Fact]
    public void A_password_in_the_connection_string_is_refused()
        => Assert.Throws<ArgumentException>(() => MongoReader.Connect("mongodb://agent:pw@localhost/x", null));
}
