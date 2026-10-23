using System.Globalization;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace OpenTail.Connectivity.MongoDb;

/// <summary>Limits of a <see cref="MongoReader"/>. Every value is checked when the reader is made (<see cref="Validate"/>).</summary>
/// <param name="MaxRows">Documents returned at most. Default 200.</param>
/// <param name="MaxCellChars">Strings in values are cut here. Default 2,000.</param>
/// <param name="MaxResponseBytes">Total size of a result as JSON, for every operation including the collections listing. Default 256 KB.</param>
/// <param name="CommandTimeoutSeconds">Server-side time limit (<c>maxTimeMS</c>): the server stops the operation. Default 15.</param>
/// <param name="SampleSize">Documents sampled per collection to describe its fields. Default 20.</param>
/// <param name="MaxFieldsPerCollection">Fields reported per collection by the listing. Default 200.</param>
public sealed record MongoReaderOptions(
    int MaxRows = 200,
    int MaxCellChars = 2000,
    int MaxResponseBytes = 256 * 1024,
    int CommandTimeoutSeconds = 15,
    int SampleSize = 20,
    int MaxFieldsPerCollection = 200,
    MongoQueryLimits? Limits = null)
{
    public MongoQueryLimits QueryLimits => Limits ?? new MongoQueryLimits();

    /// <summary>Refuses limits that make no sense or could overflow (MaxRows + 1 is appended to pipelines).</summary>
    public void Validate()
    {
        static void Range(int value, int min, int max, string name)
        {
            if (value < min || value > max)
                throw new ArgumentOutOfRangeException(name, value, $"{name} must be from {min} to {max}.");
        }

        Range(MaxRows, 1, 100_000, nameof(MaxRows));
        Range(MaxCellChars, 1, 1_000_000, nameof(MaxCellChars));
        Range(MaxResponseBytes, 1024, 16 * 1024 * 1024, nameof(MaxResponseBytes));
        Range(CommandTimeoutSeconds, 1, 3600, nameof(CommandTimeoutSeconds));
        Range(SampleSize, 1, 1000, nameof(SampleSize));
        Range(MaxFieldsPerCollection, 1, 10_000, nameof(MaxFieldsPerCollection));
        var l = QueryLimits;
        Range(l.MaxRequestBytes, 64, 1024 * 1024, "Limits.MaxRequestBytes");
        Range(l.MaxDepth, 2, 64, "Limits.MaxDepth");
        Range(l.MaxStages, 1, 100, "Limits.MaxStages");
        Range(l.MaxListValues, 1, 10_000, "Limits.MaxListValues");
        Range(l.MaxClauses, 1, 1000, "Limits.MaxClauses");
        Range(l.MaxOperators, 1, 10_000, "Limits.MaxOperators");
        Range(l.MaxSample, 1, 100_000, "Limits.MaxSample");
    }
}

/// <summary>A result: plain JSON values, and whether (and why) it was cut.</summary>
public sealed record MongoRows(IReadOnlyList<object?> Rows, bool Truncated, string? TruncatedBecause);

/// <summary>One collection in the listing: its fields (dotted paths, two levels deep) and the BSON types seen in the sample.</summary>
public sealed record MongoCollectionShape(string Collection, IReadOnlyList<(string Field, IReadOnlyList<string> Types)> Fields, bool FieldsTruncated);

public sealed record MongoCollections(IReadOnlyList<MongoCollectionShape> Collections, bool Truncated, string? TruncatedBecause);

/// <summary>
/// Governed read-only access to a MongoDB database: one allowed collection per query, every query checked by
/// <see cref="MongoGuard"/>, every operation with a server-side time limit and an optional comment (an action id, so the server's
/// profiler and Atlas's query insights can be tied to an audit record), and every result bounded.
/// </summary>
/// <remarks>The login should have the built-in <c>read</c> role on the database and nothing else: the server is the read-only
/// boundary, the guard the second line. Results are data from outside; callers should treat them as untrusted.</remarks>
public sealed class MongoReader
{
    private readonly IMongoDatabase _db;
    private readonly HashSet<string> _allowed;
    private readonly MongoReaderOptions _options;

    public MongoReader(IMongoDatabase database, IReadOnlyCollection<string> collections, MongoReaderOptions? options = null)
    {
        _db = database ?? throw new ArgumentNullException(nameof(database));
        if (collections is not { Count: > 0 }) throw new ArgumentException("Name at least one collection the reader may read.", nameof(collections));
        if (collections.FirstOrDefault(c => c.StartsWith("system.", StringComparison.Ordinal)) is { } system)
            throw new ArgumentException($"'{system}' is a system collection; those are never allowed.", nameof(collections));
        _allowed = new HashSet<string>(collections, StringComparer.Ordinal);
        _options = options ?? new MongoReaderOptions();
        _options.Validate();
    }

    public IReadOnlyCollection<string> Collections => _allowed;

    /// <summary>
    /// A database from a connection string without its password, the password given separately (from a secret store), so it never
    /// sits in configuration. A connection string that contains a password is refused.
    /// </summary>
    public static IMongoDatabase Connect(string connectionString, string? password, string? database = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var url = new MongoUrlBuilder(connectionString);
        if (!string.IsNullOrEmpty(url.Password))
            throw new ArgumentException("The connection string must not contain a password; pass it separately.", nameof(connectionString));
        if (password is { Length: > 0 }) url.Password = password;
        var name = database ?? url.DatabaseName ?? throw new ArgumentException("Name the database, in the connection string or separately.", nameof(database));
        return new MongoClient(url.ToMongoUrl()).GetDatabase(name);
    }

    private TimeSpan MaxTime => TimeSpan.FromSeconds(_options.CommandTimeoutSeconds);

    private static BsonValue? Comment(string? comment) => comment is { Length: > 0 } ? new BsonString(comment) : null;

    public async Task<MongoCollections> CollectionsAsync(string? comment = null, CancellationToken ct = default)
    {
        var names = await (await _db.ListCollectionNamesAsync(new ListCollectionNamesOptions
        {
            Filter = new BsonDocument("name", new BsonDocument("$in", new BsonArray(_allowed))),
            Comment = Comment(comment),
        }, ct).ConfigureAwait(false)).ToListAsync(ct).ConfigureAwait(false);

        var result = new List<MongoCollectionShape>();
        var bytes = 0;
        string? cut = null;
        foreach (var name in names.Order(StringComparer.Ordinal))
        {
            var sample = await _db.GetCollection<BsonDocument>(name)
                .Find(FilterDefinition<BsonDocument>.Empty, new FindOptions { MaxTime = MaxTime, Comment = Comment(comment) })
                .Limit(_options.SampleSize).ToListAsync(ct).ConfigureAwait(false);
            var fields = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            foreach (var doc in sample) Fields(doc, "", 0, fields);
            var listed = fields.Take(_options.MaxFieldsPerCollection)
                .Select(f => (Truncate(f.Key), (IReadOnlyList<string>)f.Value.ToList())).ToList();
            var shape = new MongoCollectionShape(name, listed, fields.Count > listed.Count);
            var size = JsonSerializer.SerializeToUtf8Bytes(new { shape.Collection, fields = listed.Select(f => new { f.Item1, f.Item2 }) }).Length;
            if (bytes + size > _options.MaxResponseBytes) { cut = $"more than {_options.MaxResponseBytes / 1024} KB"; break; }
            bytes += size;
            result.Add(shape);
        }
        return new MongoCollections(result, cut is not null, cut);
    }

    public async Task<MongoRows> FindAsync(string collection, JsonElement? filter = null, JsonElement? projection = null, JsonElement? sort = null,
        int? limit = null, string? comment = null, CancellationToken ct = default)
    {
        CheckSize(filter, projection, sort);
        var limits = _options.QueryLimits;
        if (filter is { } f) MongoGuard.CheckFilter(f, limits);
        if (projection is { } p) MongoGuard.CheckProjection(p, limits);
        if (sort is { } s) MongoGuard.CheckSort(s, limits);

        var coll = Collection(collection);
        var take = Math.Clamp(limit ?? _options.MaxRows, 0, _options.MaxRows);
        var find = coll.Find(filter is { } ff ? Doc(ff) : FilterDefinition<BsonDocument>.Empty,
            new FindOptions { MaxTime = MaxTime, Comment = Comment(comment), AllowDiskUse = false });
        if (projection is { } pp) find = find.Project<BsonDocument>(Doc(pp));
        if (sort is { } ss) find = find.Sort(Doc(ss));
        using var cursor = await find.Limit(take + 1).ToCursorAsync(ct).ConfigureAwait(false);
        return await ReadAsync(cursor, take, ct).ConfigureAwait(false);
    }

    public async Task<MongoRows> AggregateAsync(string collection, JsonElement pipeline, string? comment = null, CancellationToken ct = default)
    {
        CheckSize(pipeline);
        MongoGuard.CheckPipeline(pipeline, _options.QueryLimits);
        var coll = Collection(collection);
        var stages = BsonSerializer.Deserialize<BsonArray>(pipeline.GetRawText()).Select(s => s.AsBsonDocument).ToList();
        stages.Add(new BsonDocument("$limit", _options.MaxRows + 1));
        using var cursor = await coll.AggregateAsync<BsonDocument>(stages,
            new AggregateOptions { MaxTime = MaxTime, Comment = Comment(comment), AllowDiskUse = false }, ct).ConfigureAwait(false);
        return await ReadAsync(cursor, _options.MaxRows, ct).ConfigureAwait(false);
    }

    public async Task<long> CountAsync(string collection, JsonElement? filter = null, string? comment = null, CancellationToken ct = default)
    {
        CheckSize(filter);
        if (filter is { } f) MongoGuard.CheckFilter(f, _options.QueryLimits);
        return await Collection(collection).CountDocumentsAsync(filter is { } ff ? Doc(ff) : FilterDefinition<BsonDocument>.Empty,
            new CountOptions { MaxTime = MaxTime, Comment = Comment(comment) }, ct).ConfigureAwait(false);
    }

    // -- plumbing ---------------------------------------------------------------------------------

    private void CheckSize(params JsonElement?[] parts)
    {
        var bytes = parts.Sum(p => p is { } e ? Encoding.UTF8.GetByteCount(e.GetRawText()) : 0);
        if (bytes > _options.QueryLimits.MaxRequestBytes)
            throw new ArgumentException($"The query is {bytes} bytes; the limit is {_options.QueryLimits.MaxRequestBytes}.");
    }

    /// <summary>Extended JSON in (so <c>{"$date": ...}</c> and <c>{"$oid": ...}</c> work); the guard has checked it first.</summary>
    private static BsonDocument Doc(JsonElement e) => BsonDocument.Parse(e.GetRawText());

    private IMongoCollection<BsonDocument> Collection(string collection)
    {
        if (!_allowed.Contains(collection))
            throw new ArgumentException($"'{collection}' is not one of the collections this reader may read: {string.Join(", ", _allowed.Order(StringComparer.Ordinal))}.");
        return _db.GetCollection<BsonDocument>(collection);
    }

    private string Truncate(string s) => s.Length > _options.MaxCellChars ? s[.._options.MaxCellChars] + "…" : s;

    private async Task<MongoRows> ReadAsync(IAsyncCursor<BsonDocument> cursor, int take, CancellationToken ct)
    {
        var rows = new List<object?>();
        var bytes = 0;
        string? cut = null;
        while (cut is null && await cursor.MoveNextAsync(ct).ConfigureAwait(false))
            foreach (var doc in cursor.Current)
            {
                if (rows.Count >= take) { cut = $"more than {take} documents"; break; }
                var row = MongoOutput.ToJson(doc, _options.MaxCellChars);
                var size = JsonSerializer.SerializeToUtf8Bytes(row).Length;
                if (bytes + size > _options.MaxResponseBytes) { cut = $"more than {_options.MaxResponseBytes / 1024} KB"; break; }
                bytes += size;
                rows.Add(row);
            }
        return new MongoRows(rows, cut is not null, cut);
    }

    private static void Fields(BsonDocument doc, string prefix, int depth, SortedDictionary<string, SortedSet<string>> fields)
    {
        foreach (var e in doc)
        {
            var path = prefix + e.Name;
            if (!fields.TryGetValue(path, out var types)) fields[path] = types = new SortedSet<string>(StringComparer.Ordinal);
            types.Add(e.Value.BsonType.ToString().ToLowerInvariant());
            if (e.Value is BsonDocument inner && depth < 2) Fields(inner, path + ".", depth + 1, fields);
        }
    }
}

/// <summary>BSON to plain JSON values, with the same limits for every operation: strings cut, depth 8, 200 elements per array.</summary>
internal static class MongoOutput
{
    private const int MaxDepth = 8;
    private const int MaxArray = 200;

    public static object? ToJson(BsonValue value, int maxChars, int depth = 0) => value switch
    {
        BsonNull or BsonUndefined => null,
        BsonDocument d when depth >= MaxDepth => $"<document with {d.ElementCount} fields>",
        BsonDocument d => d.ToDictionary(e => e.Name, e => ToJson(e.Value, maxChars, depth + 1)),
        BsonArray a when depth >= MaxDepth => $"<array of {a.Count}>",
        BsonArray a => a.Count > MaxArray
            ? a.Take(MaxArray).Select(v => ToJson(v, maxChars, depth + 1)).Append($"<{a.Count - MaxArray} more>").ToList()
            : a.Select(v => ToJson(v, maxChars, depth + 1)).ToList(),
        BsonString s => s.Value.Length > maxChars ? s.Value[..maxChars] + "…" : s.Value,
        BsonBoolean b => b.Value,
        BsonInt32 i => i.Value,
        BsonInt64 l => l.Value,
        BsonDouble d => double.IsFinite(d.Value) ? d.Value : d.Value.ToString(CultureInfo.InvariantCulture),
        BsonDecimal128 m => m.Value.ToString(),
        BsonDateTime t => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        BsonObjectId o => o.Value.ToString(),
        BsonBinaryData bin => $"<{bin.Bytes.Length} bytes>",
        BsonTimestamp ts => ts.Value,
        _ => value.ToString(),
    };
}
