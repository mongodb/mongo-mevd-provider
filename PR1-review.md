# Code Review: PR #1 — "Drop MongoDB.VectorData provider"

> ⚠️ **PR title mismatch**: The title says "Drop MongoDB.VectorData provider" but this PR *adds* a complete new provider implementation (~6,876 additions, 70 files). Please update the title and description.

**Author:** @roji | **Base:** `main` | **Additions:** +6,876 | **Files:** 70 | **Commits:** 1

---

## Overview

This PR introduces the initial MongoDB provider for `Microsoft.Extensions.VectorData` (MEVD). It delivers:

- `MongoCollection<TKey, TRecord>` (core CRUD + vector/hybrid search)
- `MongoVectorStore` (collection management)
- BSON mapping (`MongoMapper<TRecord>`, `MongoDynamicMapper`, `IMongoMapper`)
- Filter translation (`MongoFilterTranslator`)
- Index creation (`MongoCollectionCreateMapping`, `MongoCollectionSearchMapping`)
- DI registration (`MongoServiceCollectionExtensions`)
- LegacySupport backports for `net472`/`netstandard2.1`
- Unit tests + conformance tests with Testcontainers
- GitHub Actions CI

The architecture is broadly sound and consistent with the MEVD provider pattern. However there are several correctness bugs — including a broken CI pipeline and two silent error-suppression defects — that must be addressed before merging.

---

## 🔴 P0 — Bugs / Build Breakers

### 1. CI workflow references non-existent `actions` versions

```yaml
uses: actions/checkout@v6        # does not exist — current is v4
uses: actions/upload-artifact@v7  # does not exist — current is v4
uses: actions/setup-dotnet@v5    # does not exist — current is v4
```

The workflow will fail on every run. Downgrade all three to `@v4`.

---

### 2. `ListCollectionNamesAsync` iterates the raw cursor, not the error-handling wrapper
**`MongoVectorStore.cs:105`**

```csharp
using var cursor = await VectorStoreErrorHandler.RunOperationAsync<IAsyncCursor<string>, MongoException>(...)...;
using var errorHandlingAsyncCursor = new ErrorHandlingAsyncCursor<string>(cursor, ...);

while (await cursor.MoveNextAsync(cancellationToken)  // ← BUG: should be errorHandlingAsyncCursor
```

`errorHandlingAsyncCursor` is constructed and immediately discarded. All `MongoException`s thrown during enumeration escape as-is, bypassing the `VectorStoreException` translation. (Also flagged by Copilot.)

---

### 3. `HybridSearchAsync` passes a raw cursor into `EnumerateAndMapSearchResultsAsync`

In `SearchAsync` the cursor is correctly wrapped in `ErrorHandlingAsyncCursor`. In `HybridSearchAsync`:

```csharp
var cursor = await this._mongoCollection.AggregateAsync<BsonDocument>(pipeline, ...)
return this.EnumerateAndMapSearchResultsAsync(cursor, ...);  // raw cursor — MongoException not wrapped
```

The two search paths have inconsistent error handling. Errors thrown during hybrid-search result streaming surface as raw `MongoException` instead of `VectorStoreException`.

---

### 4. `CreateIndexesAsync` checks the wrong MongoDB API for existing Atlas Search indexes

```csharp
var indexCursor = await this._mongoCollection.Indexes.ListAsync(cancellationToken);
var indexes = indexCursor.ToList(cancellationToken).Select(index => index["name"].ToString()) ?? [];

if (!indexes.Contains(this._vectorIndexName))  // ← will ALWAYS be true
```

`collection.Indexes.ListAsync()` returns **B-tree indexes**, not Atlas Search indexes. Atlas Search indexes must be queried via the `$listSearchIndexes` aggregation pipeline (as done correctly in `MongoTestStore.WaitForSearchIndexesAsync`). The duplicate-index guard will never fire, so every call to `EnsureCollectionExistsAsync` on a collection whose indexes already exist will attempt to create them again and fail with a server-side error.

Additionally, `.ToList()` is synchronous and blocks the thread — use `await indexCursor.ToListAsync(cancellationToken)`.

---

### 5. `MongoMapper.MapFromStorageToDataModel` mutates the caller's `BsonDocument`

```csharp
storageModel.Remove(MongoConstants.MongoReservedKeyPropertyName);  // in-place mutation
storageModel[this._keyPropertyModelName] = value;
// ...
storageModel.Remove(vectorProperty.StorageName);
```

For search results, `storageModel` is a reference into the aggregation-result document. Mutating it corrupts subsequent uses of that document (e.g., if the caller retains the reference or the cursor yields multiple documents sharing the same backing store). The mapper must work on a copy.

---

### 6. `GetStorageKey` will throw `InvalidCastException` for `ObjectId` keys

```csharp
private static TKey GetStorageKey(BsonDocument document)
    => (TKey)BsonTypeMapper.MapToDotNetValue(document[MongoConstants.MongoReservedKeyPropertyName]);
```

`BsonTypeMapper.MapToDotNetValue` returns `object`. For `ObjectId` fields it returns a `MongoDB.Bson.ObjectId`, but the unchecked `(TKey)` cast can fail if the runtime type doesn't unbox to exactly `TKey`. This path is exercised by auto-generated key upserts. Add explicit type-specific handling for `ObjectId`, `Guid`, etc.

---

## 🟠 P1 — Significant Functional Issues

### 7. `MongoMapper` constructor registers global BSON conventions on every instantiation

```csharp
// In MongoMapper<TRecord> constructor:
ConventionRegistry.Register(
    nameof(MongoMapper<TRecord>),
    conventionPack,
    type => type == typeof(TRecord));
```

`ConventionRegistry` is a **process-wide global**. Calling this from the instance constructor registers the same pack repeatedly each time a new collection is created. This is a race condition in multi-threaded scenarios and can confuse the driver if conventions need to be changed. Move to a `static` constructor or a `Lazy<bool>` guard per `TRecord`.

---

### 8. Hybrid search RRF weights are hardcoded, asymmetric, and undocumented

```csharp
AddScore("fts_score", 0.9),   // FTS: 90%
AddScore("vs_score",  0.1),   // Vector: 10%
```

Standard Reciprocal Rank Fusion uses equal weights. The 0.9/0.1 split heavily favors full-text results in a way that will surprise users expecting balanced hybrid search. At minimum this should be documented; ideally the weights should be exposed as configurable parameters on `MongoCollectionOptions` with a 0.5/0.5 default.

---

### 9. `DefaultDistanceFunction` constant is inconsistent with the distance function mapping

```csharp
internal const string DefaultDistanceFunction = DistanceFunction.CosineDistance;
// but:
DistanceFunction.CosineSimilarity or null => "cosine",  // CosineSimilarity is the one mapped to "cosine"
```

`CosineDistance` and `CosineSimilarity` are distinct MEVD constants. Passing `DefaultDistanceFunction` through the switch would hit the `NotSupportedException` branch, not `"cosine"`. Use `CosineSimilarity` as the default constant, matching the switch.

---

### 10. Batch `UpsertAsync` issues N sequential round trips instead of `BulkWriteAsync`

```csharp
foreach (var record in records)
    await this.UpsertCoreAsync(record, i++, ...);  // separate ReplaceOne per record
```

MongoDB supports `BulkWriteAsync` with `ReplaceOneModel<BsonDocument>` entries, reducing N round trips to one. For large batches this is a material performance difference.

---

### 11. `GetFullTextSearchQuery` accepts a `filter` parameter but silently ignores it

```csharp
private static BsonDocument GetFullTextSearchQuery(
    ICollection<string> keywords,
    string fullTextSearchIndexName,
    string textPropertyName,
    BsonDocument? filter)   // ← never used in the method body
```

The filter is instead applied as a separate `$match` stage after the `$search` stage. The correct Atlas Search approach is to embed the filter inside the `$search` stage's `filter` field, which allows the search engine to apply it during index traversal rather than post-scan. The current approach forces a full FTS scan followed by post-filter application.

---

### 12. `RequiresUnreferencedCode`/`RequiresDynamicCode` messages swapped
**`MongoServiceCollectionExtensions.cs:33`**

```csharp
private const string DynamicCodeMessage    = "This method is incompatible with NativeAOT...";
private const string UnreferencedCodeMessage = "This method is incompatible with trimming...";

[RequiresUnreferencedCode(DynamicCodeMessage)]   // ← wrong message for this attribute
[RequiresDynamicCode(UnreferencedCodeMessage)]   // ← wrong message for this attribute
```

The constants are swapped on the first two `AddMongoVectorStore` overloads. (Also flagged by Copilot.)

---

## 🟡 P2 — Design & Quality

### 13. `VectorStoreErrorHandler.cs` carries dead SQL-era code with a wrong XML doc

The file contains `ExecuteWithErrorHandlingAsync(DbConnection ...)`, `ReadWithErrorHandlingAsync(DbDataReader ...)`, and `ConfiguredCancelableErrorHandlingAsyncEnumerable<T>` — all copied from a relational provider and completely unused. The file-level XML doc also incorrectly describes the class as "helpers for reading vector store model properties/attributes." Remove the dead code and fix the summary. (Also flagged by Copilot.)

---

### 14. `VectorStoreErrorHandler.RunOperationWithRetryAsync` has an unreachable `throw` and a `maxRetries=0` edge case

```csharp
while (retries < maxRetries) { try { return ...; } catch { retries++; if (retries >= maxRetries) throw; } }
throw new VectorStoreException(..., new AggregateException(exceptions));  // unreachable dead code
```

The final `throw` after the loop is dead code. Separately, if `maxRetries == 0` the loop body never executes and this throw fires immediately with an empty `AggregateException`, with no operation ever attempted. Add a guard or document that `maxRetries >= 1` is required.

---

### 15. DI extensions hard-code `TKey = string`

```csharp
services.Add(new ServiceDescriptor(typeof(MongoCollection<string, TRecord>), ...))
```

The provider supports `string`, `Guid`, `ObjectId`, `int`, and `long` keys. Any caller who wants to inject `MongoCollection<Guid, MyRecord>` cannot use these DI helpers. Add typed overloads or a `TKey` type parameter, or at minimum document the limitation clearly.

---

### 16. `MongoDynamicMapper` stores null vectors as empty arrays, not `BsonNull`

```csharp
null => Array.Empty<object>(),
```

Round-tripping a null vector stores `[]` and reads back as a `ReadOnlyMemory<float>` of length 0 instead of null. This is a semantic data loss. Use `BsonNull.Value` for null vectors.

---

### 17. `[BsonElement]` silently overrides `[VectorStoreData(StorageName=...)]` with no documentation

`MongoModelBuilder.ProcessProperty` calls the base first (which applies `[VectorStoreData]`) then overwrites with `[BsonElement]`. This precedence is verified in `MongoBsonMappingTests` but is nowhere documented in the public API. Add an XML doc note on the relevant attribute classes or the model builder.

---

### 18. `MongoConstants.SupportedKeyTypes` is dead code and misleading

```csharp
internal static readonly IReadOnlyList<Type> SupportedKeyTypes = [typeof(string)];
```

Actual key validation uses `s_validKeyTypes` in `MongoCollection.cs` (which includes `Guid`, `ObjectId`, `int`, `long`). The `SupportedKeyTypes` constant is never used and implies only `string` is supported. Remove it or align it with reality.

---

### 19. `MongoCollectionCreateMapping` silently ignores `IndexKind` and unvalidated `Dimensions`

- `property.Dimensions` is never validated — if null or zero, the `createSearchIndexes` command will fail server-side with an opaque error rather than a clear provider exception.
- The `IndexKind` attribute value is read but never used when building the index definition. All vectors are indexed identically regardless of the attribute.

---

### 20. `MongoMapper<TRecord>` and `MongoDynamicMapper` are attributed `[ExcludeFromCodeCoverage]`

These are critical paths. Excluding them hides real coverage gaps. Remove the attribute from both.

---

### 21. Copyright headers say "Microsoft" throughout a MongoDB-owned package

```csharp
// Copyright (c) Microsoft. All rights reserved.
```

Every source file carries this. This is a MongoDB repository. Headers should read `Copyright (c) MongoDB, Inc.`

---

### 22. `MongoTestStore` uses reflection to access a private `_model` field

```csharp
// in MongoTestStore.cs
var model = (CollectionModel)typeof(MongoCollection<,>)
    .GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!
    .GetValue(collection)!;
```

This couples the test infrastructure to internal implementation details. If `_model` is renamed, the conformance tests break silently at runtime rather than compile time. Expose a `protected` or `internal` accessor, or use an interface.

---

## 🟢 Nits / Minor

| Location | Issue |
|---|---|
| `MongoFilterTranslator.cs:159` | Typo: `"MongogDB"` → `"MongoDB"` (Copilot) |
| `MongoDynamicMapper.cs:31` | Unclosed quote: `"Missing value for key property '{model.KeyProperty.ModelName}"` — missing closing `'` (Copilot) |
| `MongoCollectionSearchMapping.cs:137` | Typo: `"pipeilne"` → `"pipeline"` (Copilot) |
| `.editorconfig:14` | `insert_final_newline` appears twice — `= true` then `= false`; the second wins, likely unintentionally (Copilot) |
| `MongoDynamicCollection` constructor | Requires non-nullable `MongoCollectionOptions` while every other constructor accepts `options = default` |
| `MongoFilterTranslator` | Stateless class instantiated fresh on every search call — make it a singleton or use `static` methods |
| `MongoDynamicMapper.GetDataPropertyValue` `DateTimeOffset?` case | Captures `dateTime` from `ToNullableUniversalTime()` but then uses `value.ToUniversalTime()` instead — logically inconsistent (same result in practice, but the capture is dead) |
| `DefaultIndexKind` | Set to `IndexKind.IvfFlat` but MongoDB Atlas uses HNSW — confusing constant |

---

## Test Coverage Gaps

- No unit test for `HybridSearchAsync` (integration-only)
- No test for auto-generated key path (`IsAutoGenerated = true` → `Guid.NewGuid()` or `ObjectId`)
- No test for `EnsureCollectionExistsAsync` when Atlas Search indexes already exist
- No test for `GetAsync(Expression<Func<TRecord, bool>> filter, ...)` filtered overload
- No negative test for `MongoDynamicCollection` constructed without a `Definition`
- No regression guard against repeated `ConventionRegistry.Register` calls for the same type

---

## Summary Table

| Priority | Count | Top examples |
|---|---|---|
| 🔴 P0 — build/data bugs | 6 | Broken CI versions, raw cursor in `ListCollectionNamesAsync`, wrong index API in `EnsureCollectionExistsAsync`, document mutation in mapper |
| 🟠 P1 — functional issues | 6 | Missing error wrapping in `HybridSearchAsync`, unsafe global convention register, non-standard RRF weights, N×1 batch upsert |
| 🟡 P2 — design/quality | 10 | Dead SQL code in `VectorStoreErrorHandler`, DI hard-codes `string` key, wrong copyright, ignored `IndexKind` |
| 🟢 Nits | 8 | Typos, editorconfig conflict, stateless translator per-call, etc. |

### Highest-priority fixes before merge

1. **CI action versions** — nothing runs without them
2. **`EnsureCollectionExistsAsync` wrong index API** — breaks idempotency on every second call
3. **Document mutation in mapper** — data integrity risk for search results
4. **Missing `ErrorHandlingAsyncCursor` wrapping** in `ListCollectionNamesAsync` and `HybridSearchAsync` — silent exception swallowing
