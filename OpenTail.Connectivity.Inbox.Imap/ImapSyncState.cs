using System.Text.Json;

namespace OpenTail.Connectivity.Inbox.Imap;

public sealed record ImapSyncState(
    string Folder,
    uint UidValidity,
    uint HighestSeenUid,
    DateTimeOffset UpdatedAt);

public sealed class ImapSyncStateStore
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions _writeOptions = new() { WriteIndented = true };

    public ImapSyncStateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public async Task<ImapSyncState?> TryLoadAsync(string folder, CancellationToken ct = default)
    {
        if (!File.Exists(_filePath)) return null;
        try
        {
            await using var stream = File.OpenRead(_filePath);
            var states = await JsonSerializer.DeserializeAsync<Dictionary<string, ImapSyncState>>(stream, cancellationToken: ct);
            return states?.GetValueOrDefault(folder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    public async Task SaveAsync(ImapSyncState state, CancellationToken ct = default)
    {
        Dictionary<string, ImapSyncState> states;
        if (File.Exists(_filePath))
        {
            try
            {
                await using var readStream = File.OpenRead(_filePath);
                states = await JsonSerializer.DeserializeAsync<Dictionary<string, ImapSyncState>>(readStream, cancellationToken: ct)
                    ?? new Dictionary<string, ImapSyncState>();
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { states = new Dictionary<string, ImapSyncState>(); }
        }
        else
        {
            states = new Dictionary<string, ImapSyncState>();
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        states[state.Folder] = state;
        await OpenTail.Connectivity.Files.AtomicFile.WriteAllTextAsync(_filePath,
            JsonSerializer.Serialize(states, _writeOptions),
            ct);
    }
}
