# Plan: Postgres and MySQL in the Host's Sql module

**Status:** plan. **The code it concerns is in [`opentail-net/opentail-host`](https://github.com/opentail-net/opentail-host),
not in this repository.** It is kept here because this is where the SQL design work happened; the coder can see both. Written
from reading `OpenTail.Modules.Sql` at commit `3dd97bb`. Nothing has been run against a real Postgres or MySQL server. Item 1
was checked with a line-for-line port of `SqlGuard.Tokenise`; whether the database then runs the hidden statement is still to be
confirmed (step 1.5).

## Where this needs fixing (opentail-host)

| Item | File | Where |
|---|---|---|
| 1.1 Guard misses Postgres and MySQL quoting **(security)** | `OpenTail.Modules.Sql/SqlGuard.cs` | `Tokenise` (line 65) and `Skip` (line 103): only `'`, `"`, `[ ]`, `` ` ``, `--` and `/* */` are known (lines 78-95). `Classify` needs the dialect. |
| 1.2 One login for reads and writes **(security)** | `OpenTail.Modules.Sql/SqlModuleFactory.cs`, `SqlModule.cs` | `OpenAsync` in `CreateAsync` (factory lines 75-88) builds the only connection; `SqlModuleOptions` has one `ConnectionFactory`/`ConnectionFactoryAsync` (module lines 10-13), used by every tool. |
| 1.3 No read-only transaction for queries | `OpenTail.Modules.Sql/SqlModule.cs` | `QueryAsync` (line 94); read-only is set only for SQLite (factory line 83). |
| 1.5 Tests for the bypasses | `OpenTail.Host.Tests/MoreModuleTests.cs` | `SqlGuardTests.Refuses` (around line 106). |
| 2 Schema tool | `SqlModule.cs`, `SqlModuleFactory.cs` | `InformationSchemaQuery` (module line 41), its use in the factory (line 101), and `SchemaAsync` using `MaxRows` (module line 88). |
| 3 MySQL previews write for real | `SqlModule.cs` | `WritePreview.PreviewAsync`, the rollback preview (from line 218). |
| 4 Values | `SqlModule.cs` | `Cell` (line 182) and the per-cell read in `ReadAsync` (line 176). |
| 5 Docs | `OpenTail.Host/MODULES.md`, `OpenTail.Host/SECURITY.md` | The `sql` row in `MODULES.md` (around line 117). |
| 6 Tests on real servers | `OpenTail.Host.Tests/MoreModuleTests.cs` | `SqlModuleTests` (from line 130) is SQLite-only. |

Line numbers are from `3dd97bb` and will drift.

## Where it stands

The module is **not SQLite-only**. `SqlModuleFactory` resolves `sqlserver`, `postgres` and `mysql` by reflection when the
deployment references `Microsoft.Data.SqlClient`, `Npgsql` or `MySqlConnector`, and `MODULES.md` documents it. What is missing
is everything that would make those providers trustworthy:

- Every behaviour test runs on SQLite. The only Postgres test checks the "driver package missing" error.
- Read-only is enforced at the connection only for SQLite (`Mode=ReadOnly`). Other providers rely on the login, plus `SqlGuard`.
- `SqlGuard` knows standard SQL quoting only. Postgres and MySQL have more (below).
- The default schema query and the value mapping were written with SQLite and SQL Server in mind.

## 1. Fix first: the guard can be bypassed on Postgres and MySQL

`SqlGuard.Tokenise` skips `'...'` literals, `"..."`, `[...]` and `` `...` `` identifiers, and comments. Postgres and MySQL have
quoting it does not model, so it can skip text the database executes. These are classified as a plain `SELECT`, although each hides
`; DELETE FROM accounts ...`:

| Dialect | Statement | Why the guard misses it |
|---|---|---|
| Postgres | `SELECT $$ ' $$; DELETE FROM accounts WHERE true; SELECT ' ` | `$$...$$` is a string to Postgres. The guard ignores `$` and treats `' $$; DELETE ... '` as one literal. |
| Postgres | `SELECT E'a\'' ; DELETE FROM accounts WHERE true; SELECT ' '` | In an `E'...'` string, `\'` is an escaped quote. The guard reads `''` as an escaped quote instead and stays "inside" the literal. |
| MySQL | `SELECT 'a\'' ; DELETE FROM accounts WHERE 1=1; SELECT ' '` | MySQL treats backslash as an escape in ordinary strings by default. Same effect. |
| MySQL | `SELECT 1 # '` *newline* `; DELETE FROM accounts WHERE 1=1; -- '` | `#` starts a comment in MySQL, hiding the `'`. The guard does not know `#` and reads `' ... '` as one literal. |

The plain `SELECT 1; DELETE ...` is refused, as it should be.

**Why it matters.** `sql.query` is Read risk, so policy runs it without review. The module says the login is the real boundary,
and with a read-only login nothing is lost. But `AllowWrites` needs a login that can write, and the same login serves
`sql.query`. A prompt-injected model could then delete rows through `sql.query`, without the queue and approval `sql.delete`
is meant to force. SQLite and SQL Server are not affected by these three forms.

**Fixes, in order:**

1. **Fail closed in the guard** for anything it cannot model, depending on the provider. Refuse `$` followed by `$` or an
   identifier and `$` (dollar quotes), an `E'`/`e'` prefix, and a backslash inside a literal, for Postgres; a backslash inside a
   literal, and `#` comments, for MySQL. `Classify(sql)` becomes `Classify(sql, SqlDialect)`, and the factory passes the dialect.
   Rejecting is cheaper and safer than writing a full lexer per dialect; the AI can rewrite the query without them.
2. **Separate logins for reading and writing.** `schema` and `query` use a read-only login; `write` and `delete` use a second
   one. New optional credentials `write-password` / `write-connection` (and `WriteConnectionString`), required when
   `AllowWrites` is on with a non-SQLite provider. Then a guard bypass cannot write, whatever the guard misses next. This is the
   structural fix; step 1 only closes the known holes.
3. **Read-only transactions for queries** as a second layer: run `sql.query` inside `START TRANSACTION READ ONLY` (Postgres and
   MySQL both have it). On its own it is not enough while multiple statements are possible, since a smuggled `COMMIT` ends it.
4. **Refuse multiple statements in the driver** where the driver allows it. Check whether Npgsql (for example with SQL
   rewriting turned off, which also means positional `$1` parameters) and MySqlConnector can be told to send one statement only.
5. **Confirm against real servers** that the three statements above run their `DELETE` today, and are refused after the fix.
   Add them to `SqlGuardTests.Refuses` (with the dialect), and to the integration tests in item 6.

## 2. The schema tool

`InformationSchemaQuery` returns `table_name, column_name, data_type` from `information_schema.columns` with no filter, and the
result is cut at `MaxRows` (200):

- Postgres includes `pg_catalog` and `information_schema`, so system tables fill the 200 rows.
- MySQL includes every database the login can see (`mysql`, `sys`, `performance_schema`, `information_schema`, others).
- Neither result says which schema a table is in, so `public.orders` and `sales.orders` look the same.

Plan:
- Per-provider defaults with a `table_schema` column:
  - Postgres: `... WHERE table_schema NOT IN ('pg_catalog','information_schema') ORDER BY table_schema, table_name, ordinal_position`
  - MySQL: `... WHERE table_schema = DATABASE() ORDER BY table_name, ordinal_position`
  - SQL Server: add `table_schema` (it already lists one database).
  - SQLite: `'main' AS table_schema`, so every provider returns the same columns.
- An optional `table` argument on `schema`, so the AI can list tables first and then ask for one table's columns.
- A separate `MaxSchemaRows` (for example 2,000), so the schema is not cut at the query row limit.
- `Config.Schemas`: an optional allow-list of schemas the tool reports. It does not restrict queries; the login does.

## 3. Write previews

`PreviewByRollback` runs the write in a transaction and rolls it back to report the row count.

- **MySQL: tables that are not transactional (MyISAM, MEMORY, ARCHIVE) are changed for real by a "preview".** The rollback has no
  effect on them. Plan: for `mysql`, at start-up, query `information_schema.tables` for the database's tables whose engine is not
  InnoDB. If there are any, turn preview-by-rollback off for that module and say why in the manifest notes (the preview then
  shows the statement without a row count), unless the operator has listed those tables as never to be written.
- Postgres: a preview advances sequences (`nextval` in an insert) and fires triggers. Sequence gaps are harmless; triggers that
  reach outside the database (for example `dblink`) are not. Document it.
- All providers: a preview takes the same row locks as the real write, for the length of the preview. Keep the command timeout
  short, as it is.

## 4. Values

`Cell()` passes primitives, formats dates as ISO 8601 and calls `ToString()` on everything else.

- Postgres arrays come back as .NET arrays, so they print as `System.Int32[]`. Map any `IEnumerable` (not string) to a JSON
  array, applying the same cell rules to its items.
- Some values throw when read rather than when formatted: Postgres `infinity` timestamps and `NaN` or very large `numeric`
  values, and MySQL zero dates (`0000-00-00`) unless the connection string has `ConvertZeroDateTime=true`. Catch per cell and
  return `<unreadable value (type)>`, so one odd value does not fail the whole query. Recommend `ConvertZeroDateTime=true` for
  MySQL in the docs.
- `jsonb`, `uuid`, `inet` and `interval` come through as strings via `ToString()`; check each formats without the culture.

## 5. Logins, timeouts and connections (docs)

`MODULES.md` and `SECURITY.md` get a short section per provider:
- **Postgres:** a role with `CONNECT`, `USAGE` on the schemas and `SELECT` on the tables only; not a superuser, no
  `pg_read_server_files`, `pg_write_server_files`, `pg_execute_server_program` or `pg_signal_backend`.
  `ALTER ROLE agent_ro SET default_transaction_read_only = on` and `SET statement_timeout = '15s'` as server-side backstops.
  TLS: `SSL Mode=VerifyFull`.
- **MySQL:** `SELECT` on the one database only, no `FILE`, `PROCESS` or `SUPER`; `max_execution_time` for the user's
  sessions. TLS: `SslMode=VerifyFull`.
- **The write login** (item 1.2): only `INSERT`, `UPDATE` and `DELETE` on the tables the AI may change.
- Database error messages can name the server, database and login. Previews pass them on truncated to 300 characters; tool errors
  may reach the model in full, depending on how the Host reports them. Strip connection details, or at least document that they are visible.

## 6. Tests

- Make `SqlModuleTests` run against each provider: SQLite always; Postgres and MySQL (and SQL Server) from containers, behind an
  environment variable, in a separate CI job as the maintainers choose. Every existing test runs on every provider.
- Add per provider: the guard cases from item 1; read-only login refusing a write through `query`; the schema filter; MySQL
  preview refused on a MyISAM table; arrays, `infinity`, `NaN`, zero dates; TLS settings accepted.
- Until a provider has passed against a real server, `MODULES.md` labels it untested.

## 7. Packaging

The Host does not reference the drivers; a deployment does. Document the driver versions the tests ran against, and that the
deployment's own vulnerability audit covers them. A sample deployment (like `OpenTail.Host.Sample.*`) with Postgres would show the
whole setup.

## Order

1. Item 1, the guard and separate read/write logins, with its tests. This is a security fix; it is worth doing before anyone
   enables `AllowWrites` on Postgres or MySQL.
2. Item 2 (schema) and item 3 (MySQL previews).
3. Item 6 (tests on real servers), which also confirms item 1.5.
4. Items 4, 5 and 7.

## From the Connectivity design

The [Connectivity SQL design](https://github.com/opentail-net/opentail-connectivity/blob/main/docs/design/sql-connectors.md) was
written for ingestion. What carries over is used above: read-only rules per database, value formatting without the culture, and
keeping connection details out of messages. Its identifier quoting and allow-lists do not apply, because here the AI writes the
SQL; the login and the guard take their place.

## Open questions

- Is a second login for writes acceptable to operators, or should `AllowWrites` require it only for non-SQLite providers?
- Should the guard refuse dollar quotes and backslashes outright, or should it learn each dialect's quoting?
- Should `schema` change its output columns (adding `table_schema`), given that existing prompts may expect three columns?
- Which providers matter first to customers: Postgres, MySQL or SQL Server?
