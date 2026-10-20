# Design: SQL Server, Postgres and MySQL connectors

**Status:** proposal. Nothing here is implemented, and the code blocks are sketches that have not been compiled. Read it with
[shared-groundwork.md](shared-groundwork.md), which it depends on for stable IDs and the cursor store.

**Where it fits:** live SQL Server, Postgres and MySQL queries belong to the Host's Sql module, which already supports those
drivers; see the [roadmap](../roadmap.md#two-ways-to-use-business-data). This design applies only if this repository's `Sql`
package is extended for indexing rows as documents.

## Goal

Read rows from SQL Server, Postgres and MySQL and turn them into documents, with the same allow-list model and read-only stance
as the existing SQLite connector, without making every consumer take every database driver.

## Today

`SqliteSourceConnector` (in `OpenTail.Connectivity.Sql`) takes a `SqlConnectorConfig`: a connection string, a table or query, an
id field, a title field, body fields, and optional modified and metadata fields. It reads the listed columns only. Things to fix
while generalising:

| Behaviour today | Problem |
|---|---|
| `IsQuery = true` runs `TableOrQuery` as written, and wraps it in a subquery to fetch a row. | Raw SQL from configuration. Safe only if the config is fully trusted. |
| The connection is not opened read-only. | Relies on the account's permissions alone. |
| Each row is read once in `DiscoverAsync` and again by id in `FetchAsync`. | Two queries per row; slow on large tables and needs a unique id. |
| The stream is read in one pass with no ordering guarantee. | A run that fails midway cannot resume, and there is no incremental mode. |
| The modified time is parsed with `DateTimeOffset.TryParse` and falls back to *now* when it fails. | A bad or missing value makes the row look newly changed on every run, silently. |
| Values are converted with `ToString()`. | Dates and numbers depend on the machine's culture. |
| Column and table names are wrapped in `"` without escaping. | A name containing `"` breaks out of the quotes. Double quotes work for SQLite and Postgres; SQL Server accepts them only with `QUOTED_IDENTIFIER ON` (the default for `Microsoft.Data.SqlClient`), and MySQL only in `ANSI_QUOTES` mode, so use each dialect's own quoting (`[ ]`, backticks). |

## Package layout

Keep `OpenTail.Connectivity.Sql` as it is for SQLite, and add a provider-neutral core and one small package per driver:

| Package | Contains | Depends on |
|---|---|---|
| `OpenTail.Connectivity.Sql` (existing) | `SqliteSourceConnector`, `SqlConnectorConfig`, `SqlRowTextExtractor`; gains the neutral `DbSourceConnector` and `ISqlDialect`. | `Microsoft.Data.Sqlite.Core` (unchanged) |
| `OpenTail.Connectivity.Sql.SqlServer` | `SqlServerDialect`, a factory | `Microsoft.Data.SqlClient` |
| `OpenTail.Connectivity.Sql.Postgres` | `PostgresDialect`, a factory | `Npgsql` |
| `OpenTail.Connectivity.Sql.MySql` | `MySqlDialect`, a factory | `MySqlConnector` |

Each new package follows the repository's rules: central package versions in `Directory.Packages.props`, an entry in the solution
file, a `Description`, tests, and a row in the README and [connectors.md](../connectors.md). The existing `SqliteSourceConnector`
keeps its public shape so current users are unaffected.

This is the part of the plan that needs the maintainers' agreement, because it adds projects and changes build files.

## Public shape (sketch)

```csharp
public sealed class DbSourceConnector : ISourceConnector
{
    public DbSourceConnector(Func<DbConnection> connectionFactory, ISqlDialect dialect,
                             SqlConnectorConfig config, string tenantContext, int batchSize = 500);
}
// Implements IIncrementalSourceConnector when ModifiedField is set; the pipeline owns the cursor store
// (see sync-and-deletions.md).

public interface ISqlDialect
{
    string QuoteIdentifier(string name);
    Task ApplyReadOnlyAsync(DbConnection connection, CancellationToken ct); // session settings, where supported
    string BuildKeysetQuery(/* table, columns, order, limit */);
    string SchemaColumnsQuery(string table);                 // to check configured names exist
}

// In the driver package:
var connector = SqlServerConnector.Create(connectionString, config, tenantContext);
```

`SqlConnectorConfig` is reused. New optional fields: `BatchSize`, `MaxFieldChars` and `Schema`.

## Behaviour

### Allow-list and identifiers
- Only configured columns are selected. No `SELECT *`.
- Identifiers cannot be query parameters, so the connector (1) rejects names containing quote characters, whitespace or
  semicolons, (2) checks every configured column exists by reading the database's column catalogue, and (3) quotes with the
  dialect's rules. A missing column fails fast with a clear message before any data is read.
- Values, such as the cursor, are always parameters.

### Read-only
The honest position is least privilege first, with session settings as a second layer where the database supports them:

| Database | Account | Extra layer |
|---|---|---|
| SQL Server | A login with `db_datareader` on the needed tables only. | `ApplicationIntent=ReadOnly` routes to a read replica when one exists. It does not make the session read-only. |
| Postgres | A role with `SELECT` only. | `default_transaction_read_only = on` for the session. |
| MySQL | A user with `SELECT` only. | `SET SESSION TRANSACTION READ ONLY`. |
| SQLite | n/a | `Mode=ReadOnly` in the connection string. |

The docs must say that the connector cannot guarantee read-only on SQL Server by itself.

### Custom queries
Replace the free-form `IsQuery` string with these options, in order of preference:
1. **Table or view mode** (default). A DBA can create a view with the shape and filtering wanted.
2. **Named queries** supplied by the developer in code, not by end users, still run on the read-only connection, and are wrapped in
   a keyset-paged outer query like any table.
3. Keep `IsQuery` on the SQLite connector for compatibility, marked as trusted-input only in the docs.

The connector does not try to parse SQL to decide whether it is "safe". That is unreliable.

### Reading rows: one pass, keyset paging
- Order by the id column and read in batches of `BatchSize`: `WHERE id > @last ORDER BY id LIMIT @n` (or `TOP`, per dialect).
  Keyset paging stays fast on large tables and does not skip or repeat rows when data changes, unlike `OFFSET`.
- Each batch uses a short-lived connection. A long-lived open reader would hold locks and break on timeouts.
- `DiscoverAsync` keeps each batch's row payloads in memory, and `FetchAsync` returns them from there, so rows are not re-read. If a
  payload is missing (for example a caller fetches out of order), fall back to a by-id query.
- The id column must be unique and sortable. Composite keys are out of scope for the first version; use a view that concatenates
  them.

### Incremental runs
- With a `ModifiedField`, yield the high-water mark as a checkpoint after each batch
  ([sync-and-deletions.md](sync-and-deletions.md)), with scope `sql:<dialect>:<host>/<database>/<table>`. The cursor holds the last modified value and id, so rows with the same
  modified timestamp are not missed. Resume with `modified > @m OR (modified = @m AND id > @id)`. Postgres, MySQL and SQLite
  also accept the row-value form `(modified, id) > (@m, @id)`; SQL Server does not, so the dialect builds this clause.
- The modified column must be indexed and reliably updated by the application. Warn in the docs: if the app forgets to touch it,
  changes are missed.
- Deleted rows are invisible to incremental runs (`ReportsDeletions = false`). A periodic full run with `CollectSourceRefs` is
  how consumers detect deletions.
- Without a `ModifiedField`, every run is a full run.
- A modified value that cannot be read fails the row with a clear error, not "now" as today.

### Value handling
- Dates and times are written as ISO-8601 UTC (`O` format), numbers with the invariant culture, binary as skipped with a note, and
  `NULL` as an absent field.
- Large text is cut at `MaxFieldChars` (default 100,000) and flagged with `sql.truncated`.
- The payload for the extractor stays a JSON object, as today, so `SqlRowTextExtractor` keeps working.

### Metadata and permissions
Documents carry `sql.table`, `sql.id` and any configured metadata columns. The connection string and host are never put in
metadata. Permissions are not read from the database, so `source.acl.captured` is `false`; consumers must treat these documents as
restricted until they decide otherwise. Row-level security in the source is not carried across.

### Credentials and errors
- Connection strings contain passwords. Do not log them, and run exception messages through `CredentialRedactor` built from the
  password and the full connection string.
- Provider exceptions often include the server name and login. Wrap them and keep the original as the inner exception, but do not
  print it into the ingestion report.
- Set command timeouts, and make them configurable.

## Testing

- **Logic tests** run against SQLite in CI on both Ubuntu and Windows: allow-list and identifier checks, keyset paging, resume from a
  cursor, equal-timestamp rows, bad modified values, truncation, culture independence (run once under a non-English culture).
- **Dialect tests** check generated SQL text for each dialect without a database.
- **Integration tests** for SQL Server, Postgres and MySQL need real servers (for example containers). Keep them out of the default
  CI run behind an environment variable, and run them in a separate workflow job the maintainers choose to add. The existing
  `SqlServerSourceConnectorTests.cs_ignored` file shows this was attempted before.
- Until an integration run has passed against a real server, label each driver package as unverified in the README.

## Risks

| Risk | Mitigation |
|---|---|
| Unverified against real servers | Label as such; add integration tests; keep dialect classes small. |
| Large tables | Keyset batches, a `MaxItemsProcessed` cap, and a documented first-run warning. |
| Sensitive columns pulled by mistake | Allow-list only; docs and example configs use narrow views. |
| Driver security advisories | CI audit is on; new packages are covered by the existing transitive pins process. |
| Three new packages to maintain | Start with Postgres and SQL Server; add MySQL on demand. |

## Suggested first slice

1. Add `DbSourceConnector`, `ISqlDialect` and the SQLite dialect in the existing `Sql` package, with the keyset reader and the
   identifier checks. No new packages and no new dependencies. SQLite tests prove the logic.
2. Add Postgres (`Npgsql`), then SQL Server.
3. Add MySQL if asked for.

## Open questions

- Is a separate package per driver preferred, or one package with all three drivers?
- Do consumers need composite keys in the first version?
- Which databases do the target customers run? That sets the order.
- Should named queries ship in v1, or only tables and views?
