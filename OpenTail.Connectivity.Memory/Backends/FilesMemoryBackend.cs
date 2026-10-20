namespace OpenTail.Connectivity.Memory;

/// <summary>
/// One Markdown file per record: a header with every field (one <c>key: value</c> line each, values in JSON so they round-trip
/// exactly) and the content as the body. People can read, edit and diff it in git. Keyword search scans the folder, so it suits
/// a few thousand records, not millions. Filters are applied before ranking. The folder must exist: a backend never creates the
/// place it stores, so a wrong path fails instead of starting an empty store somewhere else.
/// </summary>
public sealed class FilesMemoryBackend : IMemoryBackend
{
    private const string Fence = "---";

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _root;
    private readonly TimeProvider _time;

    public FilesMemoryBackend(string root, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        if (!Directory.Exists(_root))
            throw new DirectoryNotFoundException($"The memory folder does not exist: {_root}");
        _time = time ?? TimeProvider.System;
    }

    public MemoryBackendCapabilities Capabilities { get; } = new(
        Search: MemorySearchKinds.Keyword,
        FiltersBeforeRanking: true,
        TemporalValidity: true,
        DeleteBySource: true);

    /// <summary>The file a record lives in. Ids are escaped, so any id maps to one safe file name inside the folder.</summary>
    public string PathOf(string recordId) => Path.Combine(_root, Uri.EscapeDataString(recordId) + ".md");

    /// <summary>The record as its Markdown file reads.</summary>
    public static string ToMarkdown(MemoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var element = JsonSerializer.SerializeToElement(record with { Content = string.Empty }, Json);
        var lines = new List<string> { Fence };
        Flatten(element, prefix: null, lines);
        lines.Add(Fence);
        lines.Add(record.Content);
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>Reads a record from its Markdown file text.</summary>
    public static MemoryRecord FromMarkdown(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!text.StartsWith(Fence + "\n", StringComparison.Ordinal))
            throw new FormatException("A memory file starts with a '---' header.");
        var end = text.IndexOf("\n" + Fence + "\n", Fence.Length, StringComparison.Ordinal);
        if (end < 0)
            throw new FormatException("The memory file's header is not closed with '---'.");

        var root = new System.Text.Json.Nodes.JsonObject();
        foreach (var line in text[(Fence.Length + 1)..end].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0)
                throw new FormatException($"Not a 'key: value' line: {line}");
            Set(root, line[..colon].Split('.'), System.Text.Json.Nodes.JsonNode.Parse(line[(colon + 2)..]));
        }

        var body = text[(end + Fence.Length + 2)..];
        if (body.EndsWith('\n'))
            body = body[..^1];
        root["Content"] = body;
        return root.Deserialize<MemoryRecord>(Json) ?? throw new FormatException("The header does not describe a record.");
    }

    public async Task UpsertAsync(MemoryRecord record, IReadOnlyList<MemoryChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Any(c => !string.Equals(c.ParentRecordId, record.Id, StringComparison.Ordinal)))
            throw new ArgumentException("Every chunk must belong to the record.", nameof(chunks));

        // Write beside, then move over: a reader never sees half a file.
        var path = PathOf(record.Id);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, ToMarkdown(record), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    public async Task<MemoryRecord?> GetAsync(string recordId, CancellationToken cancellationToken = default)
    {
        var path = PathOf(recordId);
        return File.Exists(path) ? FromMarkdown(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)) : null;
    }

    public async Task<int> DeleteAsync(MemoryDeletion deletion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deletion);
        if (deletion.RecordId is { } id)
        {
            var path = PathOf(id);
            if (!File.Exists(path))
                return 0;
            File.Delete(path);
            return 1;
        }

        var removed = 0;
        await foreach (var record in AllAsync(cancellationToken).ConfigureAwait(false))
        {
            var match = deletion.DocumentId is { } doc ? record.Source.DocumentId == doc
                : deletion.Collection is { } col && string.Equals(record.Collection, col, StringComparison.OrdinalIgnoreCase);
            if (match)
            {
                File.Delete(PathOf(record.Id));
                removed++;
            }
        }

        return removed;
    }

    public async Task<IReadOnlyList<BackendHit>> SearchAsync(MemorySearch search, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        var terms = Terms(search.Query);
        if (terms.Count == 0 || search.TopK <= 0)
            return [];

        var now = _time.GetUtcNow();
        var scored = new List<(MemoryRecord Record, double Score)>();
        await foreach (var record in AllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (MemoryGovernance.Deny(record, search.Filter, now) is not null)
                continue;
            var text = Terms(record.Content);
            var score = (double)terms.Count(text.Contains) / terms.Count;
            if (score > 0)
                scored.Add((record, score));
        }

        return [.. scored
            .OrderByDescending(static s => s.Score)
            .ThenBy(static s => s.Record.Id, StringComparer.Ordinal)
            .Take(search.TopK)
            .Select(static s => new BackendHit(s.Record, s.Record.Id, MemoryChunkKind.Unknown, s.Record.Content, s.Score, MemorySearchKinds.Keyword))];
    }

    private async IAsyncEnumerable<MemoryRecord> AllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var path in Directory.EnumerateFiles(_root, "*.md").Order(StringComparer.Ordinal))
            yield return FromMarkdown(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
    }

    private static void Flatten(JsonElement element, string? prefix, List<string> lines)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name == nameof(MemoryRecord.Content) && prefix is null)
                continue;
            var key = prefix is null ? property.Name : $"{prefix}.{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.Object)
                Flatten(property.Value, key, lines);
            else
                lines.Add($"{key}: {property.Value.GetRawText()}");
        }
    }

    private static void Set(System.Text.Json.Nodes.JsonObject root, string[] path, System.Text.Json.Nodes.JsonNode? value)
    {
        var node = root;
        foreach (var part in path[..^1])
        {
            if (node[part] is not System.Text.Json.Nodes.JsonObject child)
                node[part] = child = new System.Text.Json.Nodes.JsonObject();
            node = child;
        }

        node[path[^1]] = value;
    }

    private static HashSet<string> Terms(string text)
        => [.. Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]{2,}").Select(static m => m.Value)];
}
