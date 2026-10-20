namespace OpenTail.Connectivity.Files.Pipeline;

/// <summary>An item whose fetch failed, kept with the cursor so the cursor can move on and the item is tried again next run.</summary>
internal sealed class RetryEntry
{
    public required SourceItem Item { get; init; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset FirstFailedAt { get; init; }
}

/// <summary>
/// The stored cursor, wrapped with the retry list when there is one, so both are saved in the same write and can never disagree.
/// With no retries the connector's cursor is stored exactly as before, so existing cursors and stores are unaffected.
/// </summary>
internal static class RetryEnvelope
{
    private const string Prefix = "otail-retry-v1:";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static (string? Cursor, List<RetryEntry> Retries) Read(string? stored)
    {
        if (stored is null || !stored.StartsWith(Prefix, StringComparison.Ordinal))
            return (stored, []);

        var body = JsonSerializer.Deserialize<Body>(stored[Prefix.Length..], Json)
            ?? throw new InvalidDataException("The stored sync cursor has a retry list that cannot be read.");
        return (body.Cursor, body.Retries ?? []);
    }

    public static string? Write(string? cursor, IReadOnlyCollection<RetryEntry> retries)
        => retries.Count == 0 ? cursor : Prefix + JsonSerializer.Serialize(new Body(cursor, [.. retries]), Json);

    private sealed record Body(string? Cursor, List<RetryEntry>? Retries);
}
