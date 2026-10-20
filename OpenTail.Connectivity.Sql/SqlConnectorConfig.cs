namespace OpenTail.Connectivity.Sql;

/// <summary>
/// Config for a SQL source connector. Only configured fields are read — no SELECT *.
/// </summary>
/// <param name="IsQuery">The query is trusted operator configuration; it must never come from a model or end user.</param>
public sealed record SqlConnectorConfig(
    string ConnectionString,
    string TableOrQuery,
    string IdField,
    string TitleField,
    IReadOnlyList<string> BodyFields,
    string? ModifiedField = null,
    IReadOnlyList<string>? MetadataFields = null,
    bool IsQuery = false);
