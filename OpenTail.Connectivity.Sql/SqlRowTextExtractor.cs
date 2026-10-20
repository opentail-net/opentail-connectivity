namespace OpenTail.Connectivity.Sql;

/// <summary>
/// Decodes the JSON-encoded row payload produced by SqliteSourceConnector.FetchAsync
/// and concatenates the configured body fields into plain text.
/// </summary>
public sealed class SqlRowTextExtractor : ITextExtractor
{
    private readonly SqlConnectorConfig _config;

    public SqlRowTextExtractor(SqlConnectorConfig config)
        => _config = config;

    public TextExtractionResult Extract(string fileName, byte[] content)
    {
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(content);
            var row = JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
            if (row is null)
                return new TextExtractionResult(false, string.Empty, "Failed to deserialise row payload.");

            var bodyParts = _config.BodyFields
                .Select(f => row.TryGetValue(f, out var v) ? v : null)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!.Trim());
            var body = string.Join("\n\n", bodyParts);

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_config.MetadataFields is not null)
                foreach (var f in _config.MetadataFields)
                    if (row.TryGetValue(f, out var v) && v is not null)
                        metadata[$"row.{f}"] = v;

            return new TextExtractionResult(true, body, null, metadata);
        }
        catch (Exception ex)
        {
            return new TextExtractionResult(false, string.Empty, ex.Message);
        }
    }
}
