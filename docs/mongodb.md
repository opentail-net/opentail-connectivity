# MongoDB reader

`OpenTail.Connectivity.MongoDb` gives governed, read-only access to a MongoDB database: one allowed collection per query, every
query checked against an operator allow-list, every operation with a server-side time limit and an optional comment, and every
result bounded. It is built for agents asking questions of document data. The design and its three reviews are in
[design/redis-pinecone-mongodb.md](design/redis-pinecone-mongodb.md).

**State: Tested.** Unit tests for the guard, the output and the option checks; a suite against `mongo:8` with a read-only user in
`mongodb.yml`. Not yet run against Atlas. A Host module wrapping it as agent tools comes once the package is published.

## Use

```csharp
var db = MongoReader.Connect("mongodb+srv://ot_agent@cluster0.example.mongodb.net/?readPreference=secondaryPreferred",
    password: secrets["SALES_MONGO_PASSWORD"], database: "sales");
var reader = new MongoReader(db, ["orders", "customers"], new MongoReaderOptions(MaxRows: 200, CommandTimeoutSeconds: 15));

var shapes = await reader.CollectionsAsync(comment: "opentail:" + actionId);
var open = await reader.FindAsync("orders", filter, projection, sort, limit: 50, comment: "opentail:" + actionId);
var byRegion = await reader.AggregateAsync("orders", pipeline, comment: "opentail:" + actionId);
var count = await reader.CountAsync("orders", filter, comment: "opentail:" + actionId);
```

- **`Connect`** refuses a connection string with a password in it; the password is passed separately (from a secret store).
- **The collections** are an allow-list; `system.*` is never allowed.
- **The comment** shows in the profiler, `currentOp` and Atlas's query insights, so an operation can be tied to an audit record.
- **Results** are plain JSON values (`MongoRows.Rows`), with `Truncated` and `TruncatedBecause`. They are data from outside: treat
  them as untrusted.
- **`MongoReaderOptions.Validate`** runs when the reader is made: a zero timeout, a row limit that could overflow, or a guard
  limit out of range is refused at start-up.

## What the guard allows

Every key starting with `$` must be on an allow-list for the place it appears; anything else is refused, including operators
MongoDB adds later.

- **Stages:** `$match`, `$project`, `$addFields`, `$set`, `$unset`, `$group`, `$sort`, `$limit`, `$skip`, `$unwind`, `$count`,
  `$sortByCount`, `$replaceRoot`, `$replaceWith`, `$sample` (at most 200).
- **In filters:** `$eq`, `$ne`, `$gt`, `$gte`, `$lt`, `$lte`, `$in`, `$nin`, `$and`, `$or`, `$nor`, `$not`, `$exists`, `$type`,
  `$all`, `$elemMatch`, `$size`, `$expr`.
- **In expressions:** the list in `MongoGuard.ExpressionOperators` (arithmetic, comparison, boolean, conditional, a few string,
  date and type conversions, reading arrays, `$literal`).
- **Accumulators** (in `$group` only): `$sum`, `$avg`, `$min`, `$max`, `$count`, `$first`, `$last`.
- **Values:** plain JSON, and Extended JSON type wrappers (`$date`, `$oid`, `$numberLong`, `$numberInt`, `$numberDouble`,
  `$numberDecimal`).

**Never:** server-side JavaScript (`$where`, `$function`, `$accumulator`), writes (`$out`, `$merge`), server internals.
**Not in this version:** joins (`$lookup`, `$graphLookup`, `$unionWith`), `$facet`, `$regex`, `$push`, `$addToSet`.

**Bounds:** request 16 KB; nesting 32; 20 stages; 100 values per `$in`/`$nin`/`$all`; 20 clauses per `$and`/`$or`/`$nor`; 200
operators. Results: `MaxRows` documents, strings cut at `MaxCellChars`, nested values summarised past depth 8 or 200 elements, and
every result, the collections listing included, cut at `MaxResponseBytes` (256 KB). The listing also reports at most
`MaxFieldsPerCollection` fields per collection.

**Not bounded by the guard:** the work inside `$group` and an unindexed `$sort`. The server limits each stage to 100 MB without
disk use (the reader sets `allowDiskUse: false`), and `maxTimeMS` stops the operation.

## The database user

Give the reader a user with the built-in **`read`** role on one database and nothing else; the server is the read-only boundary,
the guard the second line.

```js
db.getSiblingDB("sales").createUser({ user: "ot_agent", pwd: "<password>", roles: [ { role: "read", db: "sales" } ] })
```

On Atlas: a database user with read access scoped to the database (or a custom role with `find` on the allowed collections), and
the reader's address in the project's IP access list.
