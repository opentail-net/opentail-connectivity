using System.Collections.Concurrent;

namespace OpenTail.Connectivity.Files.Inbox;

/// <summary>
/// Thread-safe in-memory pending send store. Optionally persists to a JSON file so
/// pending approvals survive a restart. If <paramref name="filePath"/> is null the
/// store is ephemeral.
/// </summary>
/// <remarks>
/// One process per file. Every change is saved before the call returns, and a failed save throws: a send must never go out on
/// a claim that was not saved. A record found in <see cref="PendingSendStatus.Sending"/> at startup was interrupted mid-send
/// and is loaded as <see cref="PendingSendStatus.Unknown"/>. A file that cannot be read is moved aside and the constructor
/// throws, rather than starting empty and losing the queue without a word.
/// </remarks>
public sealed class InMemoryPendingSendStore : IPendingSendStore
{
    private readonly ConcurrentDictionary<string, PendingSendRecord> _records = new(StringComparer.Ordinal);
    private readonly string? _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public InMemoryPendingSendStore(string? filePath = null)
    {
        _filePath = filePath;
        if (filePath is not null && File.Exists(filePath))
            LoadFromFile(filePath);
    }

    public async Task SaveAsync(PendingSendRecord record, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _records.TryGetValue(record.Id, out var previous);
            _records[record.Id] = record;
            try
            {
                await PersistAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                if (previous is null) _records.TryRemove(record.Id, out _);
                else _records[record.Id] = previous;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<PendingSendRecord?> GetAsync(string id, CancellationToken ct = default)
        => Task.FromResult(_records.TryGetValue(id, out var r) ? r : null);

    public Task<IReadOnlyList<PendingSendRecord>> GetPendingAsync(CancellationToken ct = default)
    {
        IReadOnlyList<PendingSendRecord> result = _records.Values
            .Where(r => r.Status == PendingSendStatus.Pending)
            .OrderBy(r => r.QueuedAt)
            .ToList();
        return Task.FromResult(result);
    }

    /// <summary>
    /// Sets the status whatever it was. The change is kept in memory even when saving it fails (it records what happened, such
    /// as a send that went out); the failure is thrown.
    /// </summary>
    public async Task UpdateStatusAsync(string id, string status, string? note = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_records.TryGetValue(id, out var existing)) return;
            _records[id] = existing with { Status = status, Note = note, ProcessedAt = DateTimeOffset.UtcNow };
            await PersistAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryTransitionAsync(string id, string from, string to, string? note = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_records.TryGetValue(id, out var existing) || existing.Status != from)
                return false;

            _records[id] = existing with { Status = to, Note = note, ProcessedAt = DateTimeOffset.UtcNow };
            try
            {
                await PersistAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                _records[id] = existing;
                throw;
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void LoadFromFile(string path)
    {
        List<PendingSendRecord>? list;
        try
        {
            list = JsonSerializer.Deserialize<List<PendingSendRecord>>(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            var aside = $"{path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            File.Move(path, aside);
            throw new InvalidDataException($"The pending send file could not be read and was moved to '{aside}'. Queued sends in it need restoring by hand.", ex);
        }

        foreach (var r in list ?? [])
        {
            _records[r.Id] = r.Status == PendingSendStatus.Sending
                ? r with { Status = PendingSendStatus.Unknown, Note = "interrupted while sending; check the mailbox before requeueing" }
                : r;
        }
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        if (_filePath is null) return;
        var json = JsonSerializer.Serialize(_records.Values.ToList());
        await AtomicFile.WriteAllTextAsync(_filePath, json, ct).ConfigureAwait(false);
    }
}
