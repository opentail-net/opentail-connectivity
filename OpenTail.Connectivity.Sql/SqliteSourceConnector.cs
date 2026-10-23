namespace OpenTail.Connectivity.Sql;

/// <summary>
/// Reads rows from a SQLite database and yields them as SourceItems.
/// Body fields are concatenated; all values come from the allowlist in SqlConnectorConfig.
/// </summary>
/// <remarks>
/// Incremental runs (<see cref="GetChangesAsync"/>, through the pipeline with a sync store) need a table and a
/// <see cref="SqlConnectorConfig.ModifiedField"/>. Rows are read in (modified, id) order in batches, with a checkpoint after each
/// holding both values, and a resumed run starts strictly after that pair. So a run stopped by an item cap inside a group of rows
/// sharing one modified value carries on where it stopped. <see cref="SqlConnectorConfig.IdField"/> must be unique (it is the item
/// id). Rows whose modified value is NULL are not read incrementally. A row committed later with an older modified value than one
/// already read is missed, and so is one committed later with exactly the checkpointed value and a lower id, which only a coarse
/// column (dates, whole seconds) makes likely: keep the column set by the database, at fine resolution, on every write.
/// For gap-free paging, use an integer change counter that the database increments on every write as ModifiedField; its unique,
/// increasing values avoid equal-value/lower-id gaps and page numerically.
/// Rows whose id is NULL or empty are not read in either mode.
/// </remarks>
public sealed class SqliteSourceConnector : IIncrementalSourceConnector
{
    private readonly SqlConnectorConfig _config;
    private readonly string _tenantContext;
    private readonly int _batchSize;

    public SqliteSourceConnector(SqlConnectorConfig config, string tenantContext, int incrementalBatchSize = 500)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantContext);
        ArgumentOutOfRangeException.ThrowIfLessThan(incrementalBatchSize, 1);
        _config = config;
        _tenantContext = tenantContext;
        _batchSize = incrementalBatchSize;
    }

    /// <summary>
    /// "sqlite:" + tenant + ":" + the database file + "/" + the table + "#" + a hash of every field setting, so two configurations
    /// that read rows differently, or make different documents from them, never share a cursor: a changed body field would not
    /// change a row's modified value, and the old cursor would keep its documents stale. The connection string's other parts (which can hold a
    /// password) are left out. A scope saved by an earlier version is not found, which gives one full run.
    /// </summary>
    public string SyncScope =>
        $"sqlite:{_tenantContext}:{new SqliteConnectionStringBuilder(_config.ConnectionString).DataSource}/{_config.TableOrQuery}#" +
        OpenTail.Connectivity.Files.FileFilter.Hash("id=" + _config.IdField, "modified=" + _config.ModifiedField, "query=" + _config.IsQuery,
            "title=" + _config.TitleField, "body=" + string.Join(',', _config.BodyFields),
            "meta=" + string.Join(',', _config.MetadataFields ?? []));

    public async IAsyncEnumerable<SourceChange> GetChangesAsync(string? cursor, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_config.ModifiedField is not { } modifiedField || _config.IsQuery)
            throw new InvalidOperationException("Incremental runs need a table (not a query) and a ModifiedField.");

        await using var connection = await OpenConnectionAsync(cancellationToken);

        var mod = Quote(modifiedField);
        var id = Quote(_config.IdField);
        var select = $"SELECT {BuildFieldList()}, {mod} AS \"__mod\", {id} AS \"__id\" FROM {Quote(_config.TableOrQuery)}";
        var saved = cursor is null ? null : JsonSerializer.Deserialize<KeysetCursor>(cursor);
        object? lastMod = saved?.Value();
        object? lastId = saved?.IdValue();
        // A cursor written before it carried the id resumes at the modified value itself, once; the next checkpoint has both.
        var resuming = lastMod is not null && lastId is null;

        while (true)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = lastMod is null
                ? $"{select} WHERE {mod} IS NOT NULL AND {id} IS NOT NULL AND {id} <> '' ORDER BY {mod}, {id} LIMIT @n"
                : resuming
                    ? $"{select} WHERE {mod} >= @m AND {id} IS NOT NULL AND {id} <> '' ORDER BY {mod}, {id} LIMIT @n"
                    : $"{select} WHERE ({mod} > @m OR ({mod} = @m AND {id} > @i)) AND {id} IS NOT NULL AND {id} <> '' ORDER BY {mod}, {id} LIMIT @n";
            cmd.Parameters.AddWithValue("@n", _batchSize);
            if (lastMod is not null) cmd.Parameters.AddWithValue("@m", lastMod);
            if (lastId is not null) cmd.Parameters.AddWithValue("@i", lastId);

            var rows = 0;
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows++;
                    lastMod = reader["__mod"];
                    lastId = reader["__id"];
                    yield return new SourceChange.Upsert(Item(reader));
                }
            }

            if (rows == 0) yield break;
            resuming = false;
            yield return new SourceChange.Checkpoint(JsonSerializer.Serialize(KeysetCursor.From(lastMod!, lastId!)));
            if (rows < _batchSize) yield break;
        }
    }

    /// <summary>The last committed (modified, id) pair, each with its SQLite type so it compares as stored.</summary>
    private sealed record KeysetCursor(string Type, string Text, string? IdType = null, string? IdText = null)
    {
        public static KeysetCursor From(object modified, object id)
        {
            var (type, text) = Encode(modified);
            var (idType, idText) = Encode(id);
            return new(type, text, idType, idText);
        }

        public object Value() => Decode(Type, Text);

        public object? IdValue() => IdType is null || IdText is null ? null : Decode(IdType, IdText);

        private static (string Type, string Text) Encode(object value) => value switch
        {
            long l => ("integer", l.ToString(CultureInfo.InvariantCulture)),
            double d => ("real", d.ToString("R", CultureInfo.InvariantCulture)),
            _ => ("text", Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""),
        };

        private static object Decode(string type, string text) => type switch
        {
            "integer" => long.Parse(text, CultureInfo.InvariantCulture),
            "real" => double.Parse(text, CultureInfo.InvariantCulture),
            _ => text,
        };
    }

    private SourceItem Item(SqliteDataReader reader)
    {
        var id = Text(reader[_config.IdField])!;
        return new SourceItem(
            Id: id,
            DisplayName: Text(reader[_config.TitleField]) ?? id,
            Extension: "sql-row",
            SizeBytes: 0,
            Modified: ParseModified(reader),
            TenantContext: _tenantContext);
    }

    public async IAsyncEnumerable<SourceItem> DiscoverAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        var sql = BuildQuery();
        await using var cmd = new SqliteCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var id = Text(reader[_config.IdField]);
            if (string.IsNullOrEmpty(id))
                continue;
            var title = Text(reader[_config.TitleField]) ?? id;
            var modified = ParseModified(reader);

            // Pack the full row payload into RawPayload via a JSON blob stored as DisplayName
            // FetchAsync will re-read it from the DB; for now yield the item.
            yield return new SourceItem(
                Id: id,
                DisplayName: title,
                Extension: "sql-row",
                SizeBytes: 0,
                Modified: modified,
                TenantContext: _tenantContext);
        }
    }

    /// <summary>
    /// Re-reads the row by ID and returns its body+metadata as UTF-8 JSON bytes.
    /// The pipeline's ITextExtractor receives these bytes; SqlRowTextExtractor decodes them.
    /// </summary>
    public async Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        var fieldList = BuildFieldList();
        var table = _config.IsQuery ? $"({_config.TableOrQuery})" : Quote(_config.TableOrQuery);
        var sql = $"SELECT {fieldList} FROM {table} WHERE {Quote(_config.IdField)} = @id LIMIT 1";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", item.Id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
            return [];//  row gone since discovery

        var payload = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
            payload[reader.GetName(i)] = reader.IsDBNull(i) ? null : Text(reader.GetValue(i));

        return System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
    }

    private string BuildQuery()
    {
        if (_config.IsQuery)
            return _config.TableOrQuery;

        return $"SELECT {BuildFieldList()} FROM {Quote(_config.TableOrQuery)}";
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder(_config.ConnectionString);
        var suppliedSettings = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = _config.ConnectionString };
        if (suppliedSettings.TryGetValue("Mode", out var configuredMode)
            && Enum.TryParse<SqliteOpenMode>(Convert.ToString(configuredMode, CultureInfo.InvariantCulture), true, out var mode)
            && mode is SqliteOpenMode.ReadWrite or SqliteOpenMode.ReadWriteCreate)
            throw new InvalidOperationException("This connector is read-only; SQLite Mode must not allow writes.");

        builder.Mode = SqliteOpenMode.ReadOnly;
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only = ON;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private string BuildFieldList()
    {
        var allFields = new List<string> { _config.IdField, _config.TitleField };
        allFields.AddRange(_config.BodyFields);
        if (_config.ModifiedField is not null) allFields.Add(_config.ModifiedField);
        if (_config.MetadataFields is not null) allFields.AddRange(_config.MetadataFields);
        return string.Join(", ", allFields.Distinct().Select(Quote));
    }

    /// <summary>A quoted SQLite identifier: a double quote inside the name is doubled, so a name cannot end the quoting.</summary>
    internal static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    /// <summary>A value as text without the current culture: dates as ISO 8601, numbers with a dot.</summary>
    internal static string? Text(object? value) => value switch
    {
        null or DBNull => null,
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
        byte[] b => Convert.ToBase64String(b),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>
    /// The row's modified time: ISO 8601 or other invariant date text (UTC when it has no offset), or Unix seconds. Without a
    /// ModifiedField it is the time of the run. A value that is empty or does not parse is <see cref="UnknownModified"/>, not
    /// "now": "now" made every such row look changed on every run.
    /// </summary>
    private DateTimeOffset ParseModified(SqliteDataReader reader)
    {
        if (_config.ModifiedField is null) return DateTimeOffset.UtcNow;
        try
        {
            return ParseModified(reader[_config.ModifiedField]);
        }
        catch (Exception ex) when (ex is InvalidCastException or InvalidOperationException or FormatException)
        {
            return UnknownModified;
        }
    }

    /// <summary>The modified time given to rows whose ModifiedField cannot be read.</summary>
    public static readonly DateTimeOffset UnknownModified = DateTimeOffset.UnixEpoch;

    internal static DateTimeOffset ParseModified(object? value) => value switch
    {
        long seconds => DateTimeOffset.FromUnixTimeSeconds(seconds),
        double seconds when double.IsFinite(seconds) => DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)),
        string text when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) => parsed,
        string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds),
        DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, d.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : d.Kind)),
        DateTimeOffset d => d,
        _ => UnknownModified,
    };
}
