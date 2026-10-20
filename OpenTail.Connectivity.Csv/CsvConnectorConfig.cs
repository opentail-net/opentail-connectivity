namespace OpenTail.Connectivity.Csv;

/// <summary>
/// Config for a CSV row connector. Only the listed columns are read.
/// Pass the file path separately to CsvSourceConnector — same pattern as SqlConnectorConfig.
/// </summary>
public sealed record CsvConnectorConfig(
    string IdColumn,
    IReadOnlyList<string> BodyColumns,
    char Delimiter = ',',
    bool HasHeader = true,
    string? TitleColumn = null,
    string? ModifiedColumn = null,
    IReadOnlyList<string>? MetadataColumns = null);
