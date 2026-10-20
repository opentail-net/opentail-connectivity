using System.Text.Json;

namespace OpenTail.Connectivity.Inbox.Gmail;

public sealed record GmailSyncState(
    string UserEmail,
    string? HistoryId,
    DateTimeOffset LastFetchAt);

public sealed class GmailSyncStateStore
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions _writeOptions = new() { WriteIndented = true };

    public GmailSyncStateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public async Task<GmailSyncState?> TryLoadAsync(string userEmail, CancellationToken ct = default)
    {
        if (!File.Exists(_filePath)) return null;
        try
        {
            await using var stream = File.OpenRead(_filePath);
            var states = await JsonSerializer.DeserializeAsync<Dictionary<string, GmailSyncState>>(stream, cancellationToken: ct);
            return states?.GetValueOrDefault(userEmail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    public async Task SaveAsync(GmailSyncState state, CancellationToken ct = default)
    {
        Dictionary<string, GmailSyncState> states;
        if (File.Exists(_filePath))
        {
            try
            {
                await using var rs = File.OpenRead(_filePath);
                states = await JsonSerializer.DeserializeAsync<Dictionary<string, GmailSyncState>>(rs, cancellationToken: ct)
                    ?? [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { states = []; }
        }
        else
        {
            states = [];
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        states[state.UserEmail] = state;
        await OpenTail.Connectivity.Files.AtomicFile.WriteAllTextAsync(_filePath,
            JsonSerializer.Serialize(states, _writeOptions),
            ct);
    }
}
