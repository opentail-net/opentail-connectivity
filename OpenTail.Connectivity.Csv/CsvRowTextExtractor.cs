namespace OpenTail.Connectivity.Csv;

/// <summary>
/// Decodes the JSON-encoded row payload from CsvSourceConnector.FetchAsync
/// and concatenates the configured body columns into plain text.
/// </summary>
public sealed class CsvRowTextExtractor : ITextExtractor
{
    private readonly CsvConnectorConfig _config;

    public CsvRowTextExtractor(CsvConnectorConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    public TextExtractionResult Extract(string fileName, byte[] content)
    {
        try
        {
            var json = Encoding.UTF8.GetString(content);
            var row = JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
            if (row is null)
                return new TextExtractionResult(false, string.Empty, "Failed to deserialise row payload.");

            var bodyParts = _config.BodyColumns
                .Select(col => row.TryGetValue(col, out var v) ? v : null)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!.Trim());
            var body = string.Join("\n\n", bodyParts);

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (_config.MetadataColumns is not null)
                foreach (var col in _config.MetadataColumns)
                    if (row.TryGetValue(col, out var v) && v is not null)
                        metadata[$"row.{col}"] = v;

            return new TextExtractionResult(true, body, null, metadata);
        }
        catch (Exception ex)
        {
            return new TextExtractionResult(false, string.Empty, ex.Message);
        }
    }
}
