using System.Text.Json;

namespace OpenTail.Connectivity.Inbox.Gmail;

/// <param name="CoveredUntil">All mail up to this time is known read (null in state saved by earlier versions).</param>
/// <param name="Backfill">A full listing an expired history id started, not finished yet.</param>
public sealed record GmailSyncState(
    string UserEmail,
    string? HistoryId,
    DateTimeOffset LastFetchAt,
    DateTimeOffset? CoveredUntil = null,
    GmailBackfill? Backfill = null);

/// <summary>Mail between <paramref name="After"/> and <paramref name="Before"/> (Unix seconds; no upper bound when null) still to be
/// read. Listing is newest first, so each run reads the newest part and lowers <paramref name="Before"/>.</summary>
public sealed record GmailBackfill(long After, long? Before)
{
    internal string Query => Before is { } before ? $"after:{After} before:{before}" : $"after:{After}";
}

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
