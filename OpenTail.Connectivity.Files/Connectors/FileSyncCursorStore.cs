namespace OpenTail.Connectivity.Files.Connectors;

/// <summary>
/// <see cref="ISyncCursorStore"/> in one JSON file holding many scopes, written atomically. A missing or unreadable file is no
/// state (a full run). Writes are single-writer per file across instances and processes; deployments needing heavy concurrent
/// writers should use a database-backed <see cref="ISyncCursorStore"/>.
/// </summary>
public sealed class FileSyncCursorStore : ISyncCursorStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileSyncCursorStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>True when the last read found a file it could not parse (and treated it as no state).</summary>
    public bool LastReadWasCorrupt { get; private set; }

    public async Task<SyncState?> LoadAsync(string scope, CancellationToken cancellationToken = default)
    {
        var all = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return all.GetValueOrDefault(scope);
    }

    public async Task<SyncState> SaveAsync(string scope, string? cursor, long expectedVersion, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(cancellationToken).ConfigureAwait(false);
            var all = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var current = all.GetValueOrDefault(scope)?.Version ?? 0;
            if (current != expectedVersion) throw new SyncConflictException(scope, expectedVersion, current);
            var state = new SyncState(cursor, current + 1, DateTimeOffset.UtcNow);
            all[scope] = state;
            await AtomicFile.WriteAllTextAsync(_path, JsonSerializer.Serialize(all, Json), cancellationToken).ConfigureAwait(false);
            return state;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetAsync(string scope, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(cancellationToken).ConfigureAwait(false);
            var all = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (all.Remove(scope))
                await AtomicFile.WriteAllTextAsync(_path, JsonSerializer.Serialize(all, Json), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.None);
            }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new TimeoutException($"Timed out waiting for the cursor store lock for '{_path}'.", ex);
            }
        }
    }

    private async Task<Dictionary<string, SyncState>> ReadAsync(CancellationToken cancellationToken)
    {
        LastReadWasCorrupt = false;
        if (!File.Exists(_path)) return new(StringComparer.Ordinal);
        try
        {
            await using var stream = File.OpenRead(_path);
            var all = await JsonSerializer.DeserializeAsync<Dictionary<string, SyncState>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return all is null ? new(StringComparer.Ordinal) : new(all, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            LastReadWasCorrupt = true;
            return new(StringComparer.Ordinal);
        }
    }
}
