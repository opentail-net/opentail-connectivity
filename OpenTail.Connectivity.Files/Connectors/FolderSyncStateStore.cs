namespace OpenTail.Connectivity.Files.Connectors;

public sealed class FolderSyncStateStore
{
    private readonly string _stateFilePath;

    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public FolderSyncStateStore(string stateFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFilePath);
        _stateFilePath = stateFilePath;
    }

    public FolderSyncState? Load()
    {
        if (!File.Exists(_stateFilePath)) return null;
        try
        {
            var json = File.ReadAllText(_stateFilePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<FolderSyncState>(json, _opts);
        }
        catch { return null; }
    }

    public async Task SaveAsync(FolderSyncState state, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(state, _opts);
        await AtomicFile.WriteAllTextAsync(_stateFilePath, json, cancellationToken);
    }

    public async Task<int> CleanStaleEntriesAsync(CancellationToken cancellationToken = default)
    {
        var state = Load();
        if (state is null) return 0;

        var cleaned = state.FileTimestamps
            .Where(kv => File.Exists(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var removed = state.FileTimestamps.Count - cleaned.Count;
        if (removed > 0)
        {
            var updated = state with { FileTimestamps = cleaned, UpdatedAt = DateTimeOffset.UtcNow };
            await SaveAsync(updated, cancellationToken);
        }
        return removed;
    }
}
