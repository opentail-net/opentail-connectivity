namespace OpenTail.Connectivity.Inbox.Graph;

/// <param name="DeltaLink">The link the next run starts from: a delta link, or a next link when the last run stopped early.</param>
/// <param name="Folder">The folder the state belongs to. Null in state saved before state was kept per folder (the inbox).</param>
public sealed record GraphSyncState(
    string UserEmail,
    string? DeltaLink,
    DateTimeOffset LastFetchAt,
    string? Folder = null)
{
    internal string Key => Folder is null ? UserEmail : UserEmail + "|" + Folder;
}

public sealed class GraphSyncStateStore
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions _writeOptions = new() { WriteIndented = true };

    public GraphSyncStateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public Task<GraphSyncState?> TryLoadAsync(string userEmail, CancellationToken ct = default) => TryLoadAsync(userEmail, null, ct);

    public async Task<GraphSyncState?> TryLoadAsync(string userEmail, string? folder, CancellationToken ct = default)
    {
        var key = new GraphSyncState(userEmail, null, default, folder).Key;
        if (!File.Exists(_filePath)) return null;
        try
        {
            await using var stream = File.OpenRead(_filePath);
            var states = await JsonSerializer.DeserializeAsync<Dictionary<string, GraphSyncState>>(stream, cancellationToken: ct);
            return states?.GetValueOrDefault(key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    public async Task SaveAsync(GraphSyncState state, CancellationToken ct = default)
    {
        Dictionary<string, GraphSyncState> states;
        if (File.Exists(_filePath))
        {
            try
            {
                await using var rs = File.OpenRead(_filePath);
                states = await JsonSerializer.DeserializeAsync<Dictionary<string, GraphSyncState>>(rs, cancellationToken: ct) ?? [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { states = []; }
        }
        else
        {
            states = [];
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        states[state.Key] = state;
        await OpenTail.Connectivity.Files.AtomicFile.WriteAllTextAsync(_filePath,
            JsonSerializer.Serialize(states, _writeOptions),
            ct);
    }
}
