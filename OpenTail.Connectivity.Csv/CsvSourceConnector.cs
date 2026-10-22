namespace OpenTail.Connectivity.Csv;

/// <summary>
/// Reads rows from a CSV file and yields each as a SourceItem.
/// Row payloads are cached during DiscoverAsync and served from FetchAsync — call DiscoverAsync first.
/// </summary>
public sealed class CsvSourceConnector : ISourceConnector
{
    private readonly CsvConnectorConfig _config;
    private readonly string _filePath;
    private readonly string _tenantContext;
    private readonly Dictionary<string, Dictionary<string, string?>> _rowCache = new();

    public CsvSourceConnector(CsvConnectorConfig config, string filePath, string tenantContext)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantContext);
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"CSV file not found: {filePath}", filePath);
        _config = config;
        _filePath = filePath;
        _tenantContext = tenantContext;
    }

    public async IAsyncEnumerable<SourceItem> DiscoverAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _rowCache.Clear();

        var fileModified = new DateTimeOffset(new FileInfo(_filePath).LastWriteTimeUtc, TimeSpan.Zero);
        var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = _config.Delimiter.ToString(),
            HasHeaderRecord = _config.HasHeader,
        };

        using var reader = new StreamReader(_filePath, Encoding.UTF8);
        using var csv = new CsvReader(reader, csvConfig);

        if (!_config.HasHeader)
            throw new InvalidOperationException("CsvSourceConnector requires HasHeader = true.");

        csv.Read();
        csv.ReadHeader();

        var headers = csv.HeaderRecord!;
        ValidateHeaders(headers);

        var rowNumber = 0;
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var nextSuffix = new Dictionary<string, int>(StringComparer.Ordinal);
        while (csv.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowNumber++;

            var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in headers)
                row[header] = csv.GetField(header);

            // A row without an id gets one from its position, which is stable while the file is only appended to.
            var id = GetField(row, _config.IdColumn) is { Length: > 0 } given ? given : $"row:{rowNumber}";
            // A repeated id would replace the earlier row's payload; later rows get "#2", "#3" in file order instead, skipping
            // any suffix already taken (rows "A", "A", "A#2" become "A", "A#2", "A#2#2"), so every final id is unique.
            if (!usedIds.Add(id))
            {
                var count = nextSuffix.GetValueOrDefault(id, 1);
                string candidate;
                do candidate = $"{id}#{++count}"; while (!usedIds.Add(candidate));
                nextSuffix[id] = count;
                id = candidate;
            }

            var title = _config.TitleColumn is not null
                ? GetField(row, _config.TitleColumn) ?? id
                : id;

            var modified = fileModified;
            if (_config.ModifiedColumn is not null && row.TryGetValue(_config.ModifiedColumn, out var modStr)
                && modStr is not null
                && DateTimeOffset.TryParse(modStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                modified = parsed;

            _rowCache[id] = row;

            yield return new SourceItem(
                Id: id,
                DisplayName: title,
                Extension: "csv-row",
                SizeBytes: 0,
                Modified: modified,
                TenantContext: _tenantContext);
        }

        await Task.CompletedTask;
    }

    public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default)
    {
        if (_rowCache.TryGetValue(item.Id, out var row))
            return Task.FromResult(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row)));
        return Task.FromResult(Array.Empty<byte>());
    }

    private static string? GetField(Dictionary<string, string?> row, string column)
    {
        foreach (var key in row.Keys)
            if (key.Equals(column, StringComparison.OrdinalIgnoreCase))
                return row[key];
        return null;
    }

    private void ValidateHeaders(IReadOnlyList<string> headers)
    {
        var duplicates = headers
            .GroupBy(h => h, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();
        if (duplicates.Length > 0)
            throw new InvalidOperationException($"CSV headers contain case-insensitive duplicates: {string.Join(", ", duplicates)}");

        RequireColumn(_config.IdColumn, "IdColumn");
        if (_config.TitleColumn is not null)
            RequireColumn(_config.TitleColumn, "TitleColumn");
        if (_config.ModifiedColumn is not null)
            RequireColumn(_config.ModifiedColumn, "ModifiedColumn");
        foreach (var column in _config.BodyColumns ?? [])
            RequireColumn(column, "BodyColumns");
        foreach (var column in _config.MetadataColumns ?? [])
            RequireColumn(column, "MetadataColumns");

        void RequireColumn(string column, string settingName)
        {
            if (!headers.Any(h => h.Equals(column, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"{settingName} '{column}' not found in CSV headers: {string.Join(", ", headers)}");
        }
    }
}
