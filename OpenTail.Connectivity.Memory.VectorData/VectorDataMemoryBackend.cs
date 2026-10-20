using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;

namespace OpenTail.Connectivity.Memory.VectorData;

/// <summary>
/// One row per chunk in a vector store collection. The record is kept whole in <see cref="RecordJson"/>; every other data
/// field is derived from it so the connector can filter on it. Create the collection with <see cref="Definition"/>.
/// </summary>
public sealed class MemoryVectorRow
{
    public string Key { get; set; } = string.Empty;
    public string RecordId { get; set; } = string.Empty;
    public string ChunkId { get; set; } = string.Empty;
    public string ChunkKind { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public string Collection { get; set; } = string.Empty;
    public string Workspace { get; set; } = string.Empty;
    public string Project { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string PermissionScope { get; set; } = string.Empty;
    /// <summary>The record's required claims, sorted and joined with <c>|</c>; empty when it needs none.</summary>
    public string ClaimsKey { get; set; } = string.Empty;
    public string[] Principals { get; set; } = [];
    public bool AllowCrossWorkspace { get; set; }
    public int Trust { get; set; }
    public int SensitivityRank { get; set; }
    public bool Tainted { get; set; }
    public bool Derived { get; set; }
    public bool Superseded { get; set; }
    public bool Deleted { get; set; }
    public long ExpiresAtTicks { get; set; }
    public long ObservedAtTicks { get; set; }
    public bool AclPresent { get; set; }
    public bool AclCaptured { get; set; }
    public bool AclOpen { get; set; }
    public string RecordJson { get; set; } = string.Empty;
    public ReadOnlyMemory<float> Embedding { get; set; }

    /// <summary>The collection definition, with the embedding size and distance function of the caller's model.</summary>
    /// <param name="indexKind">The vector index, or null for the connector's default. An approximate index (HNSW, IVF) may apply
    /// filters after its own candidate search on some stores, which breaks filtering before ranking; check it with the conformance
    /// suite before relying on it. <see cref="IndexKind.Flat"/> is exact.</param>
    public static VectorStoreCollectionDefinition Definition(int dimensions, string distanceFunction = DistanceFunction.CosineSimilarity, string? indexKind = null)
    {
        static VectorStoreDataProperty Indexed(string name, Type type) => new(name, type) { IsIndexed = true };
        return new VectorStoreCollectionDefinition
        {
            Properties =
            [
                new VectorStoreKeyProperty(nameof(Key), typeof(string)),
                Indexed(nameof(RecordId), typeof(string)),
                new VectorStoreDataProperty(nameof(ChunkId), typeof(string)),
                new VectorStoreDataProperty(nameof(ChunkKind), typeof(string)),
                new VectorStoreDataProperty(nameof(Text), typeof(string)),
                Indexed(nameof(DocumentId), typeof(string)),
                Indexed(nameof(Collection), typeof(string)),
                Indexed(nameof(Workspace), typeof(string)),
                Indexed(nameof(Project), typeof(string)),
                Indexed(nameof(Repository), typeof(string)),
                Indexed(nameof(PermissionScope), typeof(string)),
                Indexed(nameof(ClaimsKey), typeof(string)),
                Indexed(nameof(Principals), typeof(string[])),
                Indexed(nameof(AllowCrossWorkspace), typeof(bool)),
                Indexed(nameof(Trust), typeof(int)),
                Indexed(nameof(SensitivityRank), typeof(int)),
                Indexed(nameof(Tainted), typeof(bool)),
                Indexed(nameof(Derived), typeof(bool)),
                Indexed(nameof(Superseded), typeof(bool)),
                Indexed(nameof(Deleted), typeof(bool)),
                Indexed(nameof(ExpiresAtTicks), typeof(long)),
                Indexed(nameof(ObservedAtTicks), typeof(long)),
                Indexed(nameof(AclPresent), typeof(bool)),
                Indexed(nameof(AclCaptured), typeof(bool)),
                Indexed(nameof(AclOpen), typeof(bool)),
                new VectorStoreDataProperty(nameof(RecordJson), typeof(string)),
                new VectorStoreVectorProperty(nameof(Embedding), typeof(ReadOnlyMemory<float>), dimensions) { DistanceFunction = distanceFunction, IndexKind = indexKind },
            ],
        };
    }
}

/// <summary>
/// A memory backend over any <c>Microsoft.Extensions.VectorData</c> collection. Every governance rule is translated into the
/// connector's filter expression, so the filter is applied inside the vector search; <see cref="MemoryGovernance.Deny"/> then
/// checks each hit exactly. Uses only equality, comparison, boolean logic and array <c>Contains</c>, which connectors commonly
/// translate; a connector that cannot translate one fails the search rather than filtering less.
/// </summary>
/// <remarks>
/// Required claims are matched exactly by listing every subset of the caller's claims, so a caller may hold at most
/// <see cref="MaxCallerClaims"/> claims. Embeddings come from the caller's generator; switching models means rebuilding the
/// collection from the records (<see cref="MemoryBackendCapabilities.EmbeddingModelId"/> names the model).
/// </remarks>
public sealed class VectorDataMemoryBackend : IMemoryBackend
{
    public const int MaxCallerClaims = 8;

    private const int DefaultGetLimit = 10_000;
    private const int MaxDeletePages = 100_000;

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly VectorStoreCollection<string, MemoryVectorRow> _collection;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    private readonly string _distanceFunction;
    private readonly double? _minScore;
    private readonly TimeProvider _time;
    private readonly int _getLimit;

    /// <param name="distanceFunction">The collection's distance function, so scores can be turned into "higher is better".</param>
    /// <param name="minScore">Hits scoring below this (after conversion) are dropped, so a query about nothing returns nothing.</param>
    public VectorDataMemoryBackend(
        VectorStoreCollection<string, MemoryVectorRow> collection,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        string embeddingModelId,
        string distanceFunction = DistanceFunction.CosineSimilarity,
        double? minScore = null,
        TimeProvider? time = null)
        : this(collection, embeddings, embeddingModelId, DefaultGetLimit, distanceFunction, minScore, time)
    {
    }

    internal VectorDataMemoryBackend(
        VectorStoreCollection<string, MemoryVectorRow> collection,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        string embeddingModelId,
        int getLimit,
        string distanceFunction = DistanceFunction.CosineSimilarity,
        double? minScore = null,
        TimeProvider? time = null)
    {
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModelId);
        _distanceFunction = distanceFunction;
        _minScore = minScore;
        _time = time ?? TimeProvider.System;
        ArgumentOutOfRangeException.ThrowIfLessThan(getLimit, 1);
        _getLimit = getLimit;
        Capabilities = new MemoryBackendCapabilities(
            Search: MemorySearchKinds.Vector,
            FiltersBeforeRanking: true,
            TemporalValidity: true,
            DeleteBySource: true,
            EmbeddingModelId: embeddingModelId);
    }

    public MemoryBackendCapabilities Capabilities { get; }

    public async Task UpsertAsync(MemoryRecord record, IReadOnlyList<MemoryChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Any(c => !string.Equals(c.ParentRecordId, record.Id, StringComparison.Ordinal)))
            throw new ArgumentException("Every chunk must belong to the record.", nameof(chunks));

        await _collection.EnsureCollectionExistsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<(string Id, MemoryChunkKind Kind, string Text)> parts = chunks.Count > 0
            ? [.. chunks.Select(static c => (c.Id, c.Kind, c.Content))]
            : [(record.Id, MemoryChunkKind.Unknown, record.Content)];
        // Below the page size, every page of this record's rows holds a stale one while any remain, so the cleanup ends.
        if (parts.Count >= _getLimit)
            throw new ArgumentException($"A record may have at most {_getLimit - 1} chunks.", nameof(chunks));

        var vectors = await _embeddings.GenerateAsync(parts.Select(static p => p.Text), cancellationToken: cancellationToken).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(record, Json);
        var rows = parts.Select((p, i) => Row(record, p.Id, p.Kind, p.Text, vectors[i].Vector, json)).ToList();
        await _collection.UpsertAsync(rows, cancellationToken).ConfigureAwait(false);

        // A crash here can leave old chunks, but the record remains available. Stale keys are worked out here rather than in
        // the filter: not every connector filters on the key property.
        var newKeys = rows.Select(static row => row.Key).ToHashSet(StringComparer.Ordinal);
        await DeleteRowsAsync(r => r.RecordId == record.Id, CancellationToken.None, keep: newKeys).ConfigureAwait(false);
    }

    public async Task<MemoryRecord?> GetAsync(string recordId, CancellationToken cancellationToken = default)
    {
        if (!await _collection.CollectionExistsAsync(cancellationToken).ConfigureAwait(false))
            return null;
        await foreach (var row in _collection.GetAsync(r => r.RecordId == recordId, 1, cancellationToken: cancellationToken).ConfigureAwait(false))
            return JsonSerializer.Deserialize<MemoryRecord>(row.RecordJson, Json);
        return null;
    }

    public async Task<int> DeleteAsync(MemoryDeletion deletion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deletion);
        if (!await _collection.CollectionExistsAsync(cancellationToken).ConfigureAwait(false))
            return 0;

        Expression<Func<MemoryVectorRow, bool>> where = deletion switch
        {
            { RecordId: { } id } => r => r.RecordId == id,
            { DocumentId: { } doc } => r => r.DocumentId == doc,
            { Collection: { } col } => WhereCollection(Norm(col)),
            _ => throw new ArgumentException("A deletion must name a record, a document or a collection.", nameof(deletion)),
        };
        return await DeleteRowsAsync(where, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BackendHit>> SearchAsync(MemorySearch search, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        if (search.TopK <= 0 || string.IsNullOrWhiteSpace(search.Query) || !await _collection.CollectionExistsAsync(cancellationToken).ConfigureAwait(false))
            return [];

        var now = _time.GetUtcNow();
        var filter = Filter(search.Filter, now);
        var query = await _embeddings.GenerateAsync(search.Query, cancellationToken: cancellationToken).ConfigureAwait(false);

        var hits = new List<BackendHit>();
        var options = new VectorSearchOptions<MemoryVectorRow> { Filter = filter };
        await foreach (var result in _collection.SearchAsync(query.Vector, search.TopK, options, cancellationToken).ConfigureAwait(false))
        {
            var score = HigherIsBetter(result.Score ?? 0);
            if (_minScore is { } min && score < min)
                continue;

            var record = JsonSerializer.Deserialize<MemoryRecord>(result.Record.RecordJson, Json)!;
            if (MemoryGovernance.Deny(record, search.Filter, now) is not null)
                continue;

            var kind = Enum.TryParse<MemoryChunkKind>(result.Record.ChunkKind, out var k) ? k : MemoryChunkKind.Unknown;
            hits.Add(new BackendHit(record, result.Record.ChunkId, kind, result.Record.Text, score, MemorySearchKinds.Vector));
        }

        return hits;
    }

    /// <summary>The governance rules as a filter expression the connector translates. Public so connector authors can test it.</summary>
    public static Expression<Func<MemoryVectorRow, bool>> Filter(MemoryFilter filter, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var r = Expression.Parameter(typeof(MemoryVectorRow), "r");
        Expression Field(string name) => Expression.Property(r, name);
        static Expression Constant<T>(T value) => Expression.Constant(value, typeof(T));
        Expression Eq<T>(string name, T value) => Expression.Equal(Field(name), Constant(value));
        static Expression AnyOf(IEnumerable<Expression> parts) => parts.Aggregate(Expression.OrElse);

        var all = new List<Expression>
        {
            Eq(nameof(MemoryVectorRow.Deleted), false),
            Expression.GreaterThan(Field(nameof(MemoryVectorRow.ExpiresAtTicks)), Constant(nowUtc.UtcTicks)),
        };

        if (!filter.IncludeSuperseded) all.Add(Eq(nameof(MemoryVectorRow.Superseded), false));
        if (!filter.IncludeDerived) all.Add(Eq(nameof(MemoryVectorRow.Derived), false));
        if (!filter.IncludeTainted) all.Add(Eq(nameof(MemoryVectorRow.Tainted), false));

        if (filter.Collections is { Count: > 0 } collections)
            all.Add(AnyOf(collections.Select(c => Eq(nameof(MemoryVectorRow.Collection), Norm(c)))));

        // Every claim the record requires must be held: its claims key must equal some subset of the caller's claims.
        var claims = (filter.Claims ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (claims.Count > MaxCallerClaims)
            throw new NotSupportedException($"This backend matches required claims exactly for up to {MaxCallerClaims} caller claims; the caller holds {claims.Count}.");
        all.Add(AnyOf(Subsets(claims).Select(key => Eq(nameof(MemoryVectorRow.ClaimsKey), key))));

        var scopes = new List<string> { string.Empty };
        scopes.AddRange((filter.Scopes ?? []).Select(Norm));
        all.Add(AnyOf(scopes.Distinct(StringComparer.Ordinal).Select(s => Eq(nameof(MemoryVectorRow.PermissionScope), s))));

        // Absence is not permission: a missing ACL, or one that was not captured, restricts.
        var aclAllows = new List<Expression> { Eq(nameof(MemoryVectorRow.AclOpen), true) };
        var contains = typeof(Enumerable).GetMethods().Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2).MakeGenericMethod(typeof(string));
        foreach (var principal in (filter.Principals ?? []).Distinct(StringComparer.Ordinal))
            aclAllows.Add(Expression.Call(contains, Field(nameof(MemoryVectorRow.Principals)), Constant(principal)));
        all.Add(Expression.AndAlso(Eq(nameof(MemoryVectorRow.AclCaptured), true), AnyOf(aclAllows)));

        if (!string.IsNullOrWhiteSpace(filter.Workspace))
            all.Add(Expression.OrElse(Eq(nameof(MemoryVectorRow.Workspace), Norm(filter.Workspace)), Eq(nameof(MemoryVectorRow.AllowCrossWorkspace), true)));
        if (!string.IsNullOrWhiteSpace(filter.ProjectScope))
            all.Add(Eq(nameof(MemoryVectorRow.Project), Norm(filter.ProjectScope)));
        if (!string.IsNullOrWhiteSpace(filter.RepositoryScope))
            all.Add(Eq(nameof(MemoryVectorRow.Repository), Norm(filter.RepositoryScope)));
        if (filter.MinTrustLevel is { } min && min > MemoryTrustLevel.Low)
            all.Add(Expression.GreaterThanOrEqual(Field(nameof(MemoryVectorRow.Trust)), Constant((int)min)));
        if (filter.MaxSensitivity is { } max)
            all.Add(Expression.LessThanOrEqual(Field(nameof(MemoryVectorRow.SensitivityRank)), Constant(MemoryGovernance.SensitivityRank(max))));
        if (filter.FromUtc is { } from)
            all.Add(Expression.GreaterThanOrEqual(Field(nameof(MemoryVectorRow.ObservedAtTicks)), Constant(from.UtcTicks)));
        if (filter.ToUtc is { } to)
            all.Add(Expression.LessThanOrEqual(Field(nameof(MemoryVectorRow.ObservedAtTicks)), Constant(to.UtcTicks)));

        return Expression.Lambda<Func<MemoryVectorRow, bool>>(all.Aggregate(Expression.AndAlso), r);
    }

    private static MemoryVectorRow Row(MemoryRecord record, string chunkId, MemoryChunkKind kind, string text, ReadOnlyMemory<float> vector, string json)
    {
        var policy = record.Policy;
        var source = record.Source;
        return new MemoryVectorRow
        {
            Key = record.Id + "\u001f" + chunkId,
            RecordId = record.Id,
            ChunkId = chunkId,
            ChunkKind = kind.ToString(),
            Text = text,
            DocumentId = source.DocumentId ?? string.Empty,
            Collection = Norm(record.Collection),
            Workspace = Norm(source.Workspace),
            Project = Norm(source.ProjectScope),
            Repository = Norm(source.RepositoryScope),
            PermissionScope = Norm(policy.PermissionScope),
            ClaimsKey = string.Join('|', (policy.RequiredClaims ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
            Principals = [.. (source.Acl?.Principals ?? []).Distinct(StringComparer.Ordinal)],
            AllowCrossWorkspace = policy.AllowCrossWorkspaceRetrieval,
            Trust = (int)policy.TrustLevel,
            SensitivityRank = MemoryGovernance.SensitivityRank(policy.Sensitivity),
            Tainted = policy.Tainted,
            Derived = record.IsDerived,
            Superseded = record.IsSuperseded,
            Deleted = record.DeletedAtUtc is not null,
            ExpiresAtTicks = policy.ExpiresAtUtc?.UtcTicks ?? long.MaxValue,
            ObservedAtTicks = record.ObservedAtUtc.UtcTicks,
            AclPresent = source.Acl is not null,
            AclCaptured = source.Acl?.Captured == true,
            AclOpen = source.Acl is { Captured: true } acl
                      && (string.Equals(acl.Visibility, "public", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(acl.Visibility, "organisation", StringComparison.OrdinalIgnoreCase)),
            RecordJson = json,
            Embedding = vector,
        };
    }

    /// <param name="keep">Matching rows to leave in place. Callers keep fewer than a page of them, or the loop could stall.</param>
    private async Task<int> DeleteRowsAsync(Expression<Func<MemoryVectorRow, bool>> where, CancellationToken cancellationToken, IReadOnlySet<string>? keep = null)
    {
        var records = new HashSet<string>(StringComparer.Ordinal);
        var deletedKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < MaxDeletePages; page++)
        {
            var keys = new List<string>();
            await foreach (var row in _collection.GetAsync(where, _getLimit, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (keep?.Contains(row.Key) == true)
                    continue;
                keys.Add(row.Key);
                records.Add(row.RecordId);
            }

            var nextKeys = keys.Distinct(StringComparer.Ordinal).Where(key => !deletedKeys.Contains(key)).ToList();
            if (nextKeys.Count == 0)
            {
                if (keys.Count > 0)
                    throw new InvalidOperationException("Vector store returned only rows already deleted during this operation.");
                return records.Count;
            }

            await _collection.DeleteAsync(nextKeys, cancellationToken).ConfigureAwait(false);
            deletedKeys.UnionWith(nextKeys);
        }

        throw new InvalidOperationException($"Vector store deletion exceeded the page limit ({MaxDeletePages}).");
    }

    private static Expression<Func<MemoryVectorRow, bool>> WhereCollection(string collection) => r => r.Collection == collection;

    private double HigherIsBetter(double score) => _distanceFunction switch
    {
        DistanceFunction.CosineDistance => 1 - score,
        DistanceFunction.EuclideanDistance or DistanceFunction.EuclideanSquaredDistance or DistanceFunction.ManhattanDistance or DistanceFunction.HammingDistance => -score,
        DistanceFunction.NegativeDotProductSimilarity => -score,
        _ => score,
    };

    private static IEnumerable<string> Subsets(IReadOnlyList<string> sorted)
    {
        for (var mask = 0; mask < 1 << sorted.Count; mask++)
            yield return string.Join('|', sorted.Where((_, i) => (mask & (1 << i)) != 0));
    }

    private static string Norm(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}
