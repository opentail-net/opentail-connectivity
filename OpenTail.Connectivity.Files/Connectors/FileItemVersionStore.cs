using System.Text.Json;
using OpenTail.Connectivity.Files.Contracts;

namespace OpenTail.Connectivity.Files.Connectors;

/// <summary>
/// <see cref="IItemVersionStore"/> backed by one JSON file written atomically. Writes are batched in memory and flushed via <see cref="FlushAsync"/>.
/// </summary>
public sealed class FileItemVersionStore : IItemVersionStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _cache;
    private bool _dirty;

    public FileItemVersionStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public async Task<string?> GetAsync(string documentId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dict = await EnsureLoadedAsync(ct).ConfigureAwait(false);
            return dict.GetValueOrDefault(documentId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string documentId, string version, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dict = await EnsureLoadedAsync(ct).ConfigureAwait(false);
            dict[documentId] = version;
            _dirty = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string documentId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dict = await EnsureLoadedAsync(ct).ConfigureAwait(false);
            if (dict.Remove(documentId))
                _dirty = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_dirty && _cache is not null) return;
            await using var fileLock = await AcquireFileLockAsync(ct).ConfigureAwait(false);
            var dict = await EnsureLoadedAsync(ct).ConfigureAwait(false);
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            await AtomicFile.WriteAllTextAsync(_path, JsonSerializer.Serialize(dict, Json), ct).ConfigureAwait(false);
            _dirty = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_cache is not null) return _cache;
        if (!File.Exists(_path))
        {
            _cache = new Dictionary<string, string>(StringComparer.Ordinal);
            return _cache;
        }
        try
        {
            await using var stream = File.OpenRead(_path);
            var deserialized = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: ct).ConfigureAwait(false);
            _cache = deserialized is not null ? new Dictionary<string, string>(deserialized, StringComparer.Ordinal) : new Dictionary<string, string>(StringComparer.Ordinal);
            return _cache;
        }
        catch (JsonException)
        {
            _cache = new Dictionary<string, string>(StringComparer.Ordinal);
            return _cache;
        }
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
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
                throw new TimeoutException($"Timed out waiting for the version store lock for '{_path}'.", ex);
            }
        }
    }
}
