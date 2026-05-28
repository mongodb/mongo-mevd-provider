---
area: MongoDB.VectorData provider source
scope: ["src/MongoDB/**"]
reviewer-agents: [collection-reviewer, mapping-reviewer, search-reviewer, public-api-reviewer]
---

# MongoDB.VectorData — src/MongoDB AGENTS.md

`src/MongoDB/` is **flat** — there are no subdirectories. Area ownership is by file group below. When you modify a file, consult the section that owns it and the corresponding reviewer brief in `.claude/agents/`.

## What this provider is

`MongoDB.VectorData` implements the `Microsoft.Extensions.VectorData` (MEVD) abstractions for MongoDB Atlas. The two public entry points are:

- `MongoVectorStore` — overrides MEVD's `VectorStore`. Owns `IMongoDatabase` and produces `MongoCollection<TKey, TRecord>` / `MongoDynamicCollection` instances. Operations at the store level are limited to `ListCollectionNamesAsync`, `CollectionExistsAsync`, and `EnsureCollectionDeletedAsync` (the latter two delegate to a throwaway dynamic collection — there's no DI-shared collection cache).
- `MongoCollection<TKey, TRecord>` — overrides MEVD's `VectorStoreCollection<TKey, TRecord>` and additionally implements `IKeywordHybridSearchable<TRecord>` (vector search is implicit via `VectorStoreCollection`'s `SearchAsync`). All record CRUD, vector search, hybrid search, and filtered get pass through here.

The dynamic variant (`MongoDynamicCollection : MongoCollection<object, Dictionary<string, object?>>`) lets callers work with untyped records on AOT/trim configurations. Non-dynamic paths are gated with `[RequiresDynamicCode]` / `[RequiresUnreferencedCode]`.

## Pipeline at a glance

```
Caller (MEVD API)
   │
   ▼  MongoVectorStore.GetCollection<TKey, TRecord>(name, definition)
   │       └─ new MongoCollection<TKey, TRecord>(database, name, options)
   │              ├─ MongoModelBuilder.Build(...) → CollectionModel
   │              └─ MongoMapper<TRecord> (or MongoDynamicMapper for the dynamic variant)
   │
   ▼  MongoCollection.UpsertAsync / GetAsync / SearchAsync / HybridSearchAsync / GetAsync(filter)
   │       ├─ Process embeddings (per VectorPropertyModel.GenerateEmbeddingsAsync) if needed
   │       ├─ Map record → BSON via IMongoMapper<TRecord>
   │       ├─ Build filter via MongoFilterTranslator (search / filtered get paths)
   │       ├─ Build pipeline via MongoCollectionSearchMapping (search / hybrid paths)
   │       ├─ Build search-index definitions via MongoCollectionCreateMapping (EnsureCollectionExistsAsync)
   │       └─ Call IMongoCollection<BsonDocument>.{ReplaceOne, FindAsync, AggregateAsync, DeleteOne/Many}Async
   │
   ▼  VectorStoreErrorHandler.RunOperation[WithRetry]Async
   │       └─ Wraps MongoException / AggregateException(inner MongoException) into VectorStoreException
   │           with VectorStoreSystemName / VectorStoreName / CollectionName / OperationName metadata.
   │
   ▼  ErrorHandlingAsyncCursor.MoveNextAsync (search/get streaming results)
           └─ Same wrap on the cursor iteration path.
```

## Functional areas (file groups)

### 1. Collection lifecycle, CRUD, search execution, retry & error handling — `collection-reviewer`

Files: `MongoCollection.cs`, `MongoDynamicCollection.cs`, `MongoVectorStore.cs`, `MongoCollectionCreateMapping.cs`, `MongoCollectionTestHook.cs`, `MongoConstants.cs`, `VectorStoreErrorHandler.cs`, `ErrorHandlingAsyncCursor.cs`, `Throw.cs`.

What lives here:

- **`MongoVectorStore`** — MEVD `VectorStore` override. Holds `IMongoDatabase`, `MongoVectorStoreOptions.EmbeddingGenerator`, and `VectorStoreMetadata`. `GetCollection<TKey, TRecord>` / `GetDynamicCollection` instantiate fresh collections per call (no caching). `CollectionExistsAsync` / `EnsureCollectionDeletedAsync` route through a transient dynamic collection over a placeholder `s_generalPurposeDefinition` so MEVD's "collection without a typed model" use case works. `ListCollectionNamesAsync` streams names through `ErrorHandlingAsyncCursor<string>`. `GetService(Type, object?)` is the MEVD service-locator hook — returns the metadata, `IMongoDatabase`, or `this` only for the unkeyed case. **Be deliberate about what you expose via `GetService`** — MEVD callers rely on it to reach the underlying database.
- **`MongoCollection<TKey, TRecord>`** — the workhorse. Single ctor pair: a `public` typed-record ctor that wires `MongoMapper<TRecord>` through `MongoModelBuilder.Build(...)`, and an `internal` ctor taking a `Func<MongoCollectionOptions, CollectionModel>` so `MongoDynamicCollection` can pass `BuildDynamic(...)`. Key types are restricted to `string`, `Guid`, `ObjectId`, `int`, `long` (or `object` for dynamic) — `s_validKeyTypes` is the canonical list, mirrored loosely in `MongoModelBuilder.ValidateKeyProperty`. Auto-generated keys are filled in `UpsertCoreAsync` for `Guid.Empty` and `ObjectId.Empty`; other types throw `UnreachableException` (which means `MongoModelBuilder.SupportsKeyAutoGeneration` and this switch must stay in sync). Search results are projected into a `{ similarityScore, document }` shape so the same pipeline tail can serve both vector search and hybrid search; the shaper (`EnumerateAndMapSearchResultsAsync`) skips locally based on `options.Skip` because MEVD's `Skip` doesn't map onto `$vectorSearch`. Index creation (`CreateIndexesAsync`) issues a single `createSearchIndexes` admin command rather than per-index calls. `GetService(Type, object?)` exposes the internal `IMongoCollection<BsonDocument>` and `IMongoDatabase`; the conformance fixture (`MongoTestStore`) relies on this to reach the driver collection for index management.
- **`MongoDynamicCollection`** — sealed sibling that forces `TKey = object`, `TRecord = Dictionary<string, object?>` and routes the model build through `MongoModelBuilder.BuildDynamic(...)`. No AOT-attributes — this is the AOT/trim-safe path.
- **`MongoCollectionCreateMapping`** — pure helpers that produce the BSON fragment lists for `createSearchIndexes`: `GetVectorIndexFields`, `GetFilterableDataIndexFields`, `GetFullTextSearchableDataIndexFields`. Reads `VectorPropertyModel.IndexKind` (defaulted from `MongoConstants.DefaultIndexKind`) and `DistanceFunction` (from `MongoConstants.DefaultDistanceFunction`) and writes the Atlas `vectorSearch` / `search` JSON schema. Storage-name policy lives here too: filterable fields are keyed by `DataPropertyModel.StorageName`, **not** the model name.
- **`MongoConstants`** — sealed bag of constants used across the area. `VectorStoreSystemName = "mongodb"` (echoed back as `VectorStoreCollectionMetadata.VectorStoreSystemName`), `MongoReservedKeyPropertyName = "_id"`, `DataModelReservedKeyPropertyName = "Id"`, default index names (`vector_index`, `full_text_search_index`), default `IndexKind` / `DistanceFunction`, default `NumCandidates` ratio (10). The `SupportedKeyTypes` / `SupportedDataTypes` / `SupportedVectorTypes` sets in here are stale-looking duplicates of the truth in `MongoModelBuilder` — be cautious about treating them as authoritative; the live validation is in `MongoModelBuilder`.
- **`VectorStoreErrorHandler`** — copied/forked from the MEVD shared `ProviderServices.Filter` helpers (note `Microsoft.Extensions.VectorData` namespace, `#pragma warning disable MEVD9000`). Wraps `TException` (`MongoException` at this provider's call sites) into `VectorStoreException` with `VectorStoreSystemName` / `VectorStoreName` / `CollectionName` / `OperationName` metadata. Provides retry variants used for vector-index creation and hybrid-search calls. **Don't add provider-specific logic here** — this is intentionally generic so it can stay in lockstep with MEVD's upstream version.
- **`ErrorHandlingAsyncCursor<T>`** — wraps `IAsyncCursor<T>` to translate `MongoException` thrown during cursor iteration into `VectorStoreException`. Used for both `Find` and `Aggregate` streaming paths.
- **`MongoCollectionTestHook`** — internal static class with `VectorIndexNameResolver` / `FullTextSearchIndexNameResolver` `Func<string, string?>` properties. `MongoCollection`'s `VectorIndexName` / `FullTextSearchIndexName` read through these resolvers before falling back to the configured names. **The only legitimate use is conformance-test fixture wiring** (`MongoTestStore` static ctor installs these so the per-collection-name index-name overrides survive across construction paths). Production code must not set these.
- **`Throw`** — re-exposes `Microsoft.Shared.Diagnostics.Throw` helpers (`IfNull`, `IfNullOrWhitespace`, `IfLessThan`) used throughout the provider for argument validation. Don't expand it without a reason.

What does *not* live here:

- BSON record↔model mapping — that's `mapping-reviewer`.
- Filter-expression translation and pipeline-stage construction — that's `search-reviewer`.
- Public DI extension methods — that's `public-api-reviewer`.

### 2. Record↔BSON mapping & model building — `mapping-reviewer`

Files: `MongoMapper.cs`, `MongoDynamicMapper.cs`, `MongoModelBuilder.cs`, `BsonValueFactory.cs`, `IMongoMapper.cs`.

What lives here:

- **`IMongoMapper<TRecord>`** — internal abstraction: `MapFromDataToStorageModel(record, recordIndex, generatedEmbeddings) → BsonDocument` and `MapFromStorageToDataModel(BsonDocument, includeVectors) → TRecord`. The dual mapper indirection exists because `MongoCollection` doesn't know at compile time whether it's wrapping a typed or dynamic mapper.
- **`MongoMapper<TRecord>`** — typed path. Builds on `BsonDocument`-extension `ToBsonDocument()` + `BsonSerializer.Deserialize<TRecord>`. Two things it does that the BSON serializer doesn't do on its own:
  - **Key remap** — if the user's data-model key is not `Id` and not `[BsonId]`-attributed, swap the model-name key with the reserved Mongo `_id` element. `MapFromStorageToDataModel` performs the inverse swap.
  - **Vector-property handling on read.** When `includeVectors` is false, vector properties are removed from the BSON before deserialization (cheap zero-vector elimination at the BSON layer rather than projection at the query layer — TODO in the code calls out a future move to projection). When `includeVectors` is true and the CLR type is `Embedding<float>`, the bare BSON array is wrapped in `{ "Vector": [...] }` so the `Embedding<float>` constructor can deserialize. `ReadOnlyMemory<float>` and `float[]` round-trip natively.
  - A `GuidStandardRepresentationConvention` is registered once per `TRecord` so plain `Guid` properties use `GuidRepresentation.Standard`. **This is a global side effect**: `ConventionRegistry.Register` mutates static state in `MongoDB.Bson.Serialization.Conventions`. Re-registration is harmless (key includes `nameof(MongoMapper<TRecord>)` and a per-type filter), but unregistering would be hard.
- **`MongoDynamicMapper`** — dictionary-based path. Manually walks `model.Properties` to read and write. Switch-on-`Type` ladders for data and vector property types are the canonical list of what the dynamic path supports — they're broader than the typed path because they include explicit nullable variants and `List<T>` / `T[]` of primitives. **Don't drop entries from the ladder** without checking conformance tests that exercise them (`TypeTests/MongoDataTypeTests`).
- **`MongoModelBuilder`** — extends MEVD's `CollectionModelBuilder` with provider-specific rules:
  - `s_validationOptions = { RequiresAtLeastOneVector = false, SupportsMultipleVectors = true, UsesExternalSerializer = true }` — `UsesExternalSerializer = true` is what tells MEVD the provider owns serialization (so it doesn't try to validate against its own type catalog).
  - `ProcessProperty` — reads `[BsonElement("…")]` and overwrites `StorageName` if present, so the BSON-attribute-driven storage name wins over MEVD's defaults.
  - `SupportsKeyAutoGeneration` — only `Guid` and `ObjectId`. (Keep this in sync with the auto-gen switch in `MongoCollection.UpsertCoreAsync`.)
  - `ValidateKeyProperty` — `string`, `int`, `long`, `Guid`, `ObjectId`. **Keep aligned with `MongoCollection.s_validKeyTypes`** (which adds `object` for dynamic).
  - `IsDataPropertyTypeValid` / `IsVectorPropertyTypeValid` — single source of truth for the typed path's accepted types. Vector types: `ReadOnlyMemory<float>` (and `?`), `Embedding<float>`, `float[]` — same set used in `MongoCollection.GetSearchVectorArrayAsync` to short-circuit embedding generation.
- **`BsonValueFactory`** — small static helper: `Create(object?)` → `BsonValue` (via `BsonValue.Create`) with `IEnumerable` short-circuit so collections become `BsonArray`. Used by `MongoFilterTranslator` to coerce constants, by `MongoCollection.GetFilterByIds` when there's no `BsonSerializationInfo` for the key type, and by `MongoDynamicMapper`. Treat it as a tactical wrapper, not a serialization layer.

What does *not* live here:

- Record CRUD and search execution — `collection-reviewer`.
- Filter-tree → BSON document translation — `search-reviewer`.

### 3. LINQ-filter translation & search-pipeline construction — `search-reviewer`

Files: `MongoFilterTranslator.cs`, `MongoCollectionSearchMapping.cs`.

What lives here:

- **`MongoFilterTranslator`** — extends MEVD's `FilterTranslatorBase`. Translates `LambdaExpression`-based filters (`r => r.X == y`, `r => r.List.Contains(x)`, etc.) into `BsonDocument` fragments suitable for `$vectorSearch.filter` and `$match`. Supports `==`/`!=`/`>=`/`>`/`<=`/`<`, `&&`/`||`, `!`, and `Contains` over inline enumerables (driver-side `$in` / `$nin`). **Important constraints** baked in here:
  - Atlas vector-search pre-filters do **not** support null checks, `DateTime`/`DateTimeOffset`/`decimal`/`DateOnly`/`IList` operands, `Contains` over array *fields* (only over inline enumerables), or arbitrary `NOT` (only `!(a==b)` / `!(a!=b)` / `!boolField` / `!(x.Contains(...))` → `$nin`). The translator throws `NotSupportedException` with a clear message in each case — keep these messages clear because they're user-visible at search time.
  - `$and` / `$or` aggressively merge adjacent same-operator nodes into a single flat array — avoid unnecessary nesting.
  - Equality emits `{ field: value }` short-form rather than `{ field: { $eq: value } }`.
  - The `#pragma warning disable MEVD9001` at top of file acknowledges the `FilterTranslatorBase` API is experimental upstream; pin this if you upgrade MEVD.
- **`MongoCollectionSearchMapping`** — static helpers that build Atlas aggregation pipelines:
  - `GetSearchQuery` → single `$vectorSearch` stage (vector, index name, path, limit, numCandidates, optional pre-filter).
  - `GetProjectionQuery` → single `$project` stage mapping `{ scoreName: { $meta: "vectorSearchScore" }, documentName: "$$ROOT" }` so the result documents have a uniform `{ similarityScore, document }` shape that `MongoCollection.EnumerateAndMapSearchResultsAsync` consumes.
  - `GetScoreThresholdMatchQuery` → `$match` on `{ score: { $gte: threshold } }`. Atlas similarity scores are "higher is more similar", so `$gte` is right.
  - `GetHybridSearchPipeline` → the heavyweight one. Computes a vector-search pipeline branch and a full-text-search pipeline branch, weights each with reciprocal-rank `1 / (rank + 60)` scaled by 0.1 (vector) / 0.9 (FTS). The asymmetric weighting is **load-bearing for the upstream `HybridSearchTests<TKey>` suite**, which builds every record with an identical vector — the vector branch can't differentiate, so the keyword match must determine ordering (see `HybridSearchAsync_with_top`, `HybridSearchAsync_with_Skip`, `HybridSearchAsync_with_multiple_keywords_ranks_matched_keywords_higher`). This matches the `IKeywordHybridSearchable` design intent: keyword-primary hybrid search with vector as a re-ranking signal. **The parameter order on `HybridSearchAsync(searchValue, keywords, ...)` is misleading** — vector comes first in the signature but FTS dominates in ranking. If you swap these weights, the upstream tests fail with "Expected: 2, Actual: 1" because the arbitrary vector pick wins over the keyword match. Unit-test `MongoCollectionSearchMappingTests.GetHybridSearchPipelineWeightsFullTextSearchHigherThanVectorSearch` pins the values. Unions them, groups by `_id` with `$max` per branch score, projects a combined score, sorts desc, limits. The full-text branch uses `$search` with the configured `fullTextSearchIndexName`, `matchCriteria: "any"`, and an optional pre-filter `$match` inserted immediately after `$search` (Atlas requires `$search` to be the first stage; the `$match` filter has to come after, not before).
  - **Don't reshape stage order in `GetHybridSearchPipeline` without checking `MongoHybridSearchTests`** — Atlas Search is strict about which stages may precede `$search` / `$vectorSearch`, and the per-branch group/unwind/rank dance is what `EnumerateAndMapSearchResultsAsync` expects to see at the tail.

What does *not* live here:

- Filter usage from inside `MongoCollection` (e.g. `_mongoCollection.FindAsync(filter, ...)`) — that's `collection-reviewer`.
- BSON value coercion of constants used by the filter — that's `BsonValueFactory` in `mapping-reviewer`'s area, but the call site here is fine to discuss in either review.

### 4. Public DI extensions & user-facing options — `public-api-reviewer`

Files: `MongoServiceCollectionExtensions.cs`, `MongoVectorStoreOptions.cs`, `MongoCollectionOptions.cs`.

What lives here:

- **`MongoServiceCollectionExtensions`** — every public DI extension lives in this single static class, namespaced into `Microsoft.Extensions.DependencyInjection` (deliberate — matches the MEVD convention so callers find them off `IServiceCollection`):
  - `AddMongoVectorStore` / `AddKeyedMongoVectorStore` — two flavors each (`IMongoDatabase`-from-DI vs `connectionString` + `databaseName`). Registers both `MongoVectorStore` and the abstract `VectorStore` aliased to it.
  - `AddMongoCollection<TRecord>` / `AddKeyedMongoCollection<TRecord>` — three flavors each (`IMongoDatabase`-from-DI; `connectionString` + `databaseName`; `Func<IServiceProvider, string>` provider pair). Registers `MongoCollection<string, TRecord>`, the abstract `VectorStoreCollection<string, TRecord>`, `IVectorSearchable<TRecord>`, `IKeywordHybridSearchable<TRecord>` — all aliased to the same instance via `GetRequiredKeyedService<MongoCollection<string, TRecord>>(key)`. **Note**: the `TKey` is hardcoded to `string` in the DI overloads. If we ever expose `TKey` through DI it's a major API addition; for now this is by design (MEVD's `AddXxxCollection` shape uses `string`).
  - All overloads carry `[RequiresUnreferencedCode]` + `[RequiresDynamicCode]` — they instantiate `MongoCollection<TKey, TRecord>` via reflection-friendly paths that aren't AOT-safe.
  - **Embedding-generator wiring.** `GetStoreOptions` / `GetCollectionOptions` peek the user's options for an `EmbeddingGenerator`; if absent, they pull `IEmbeddingGenerator` from the service provider and create a *copy* of the options object with that generator injected (via the internal copy ctor on each options type). This is a subtle convenience — preserve the "user-supplied wins, DI fallback" precedence if you touch it.
  - **`MongoClient` settings.** `CreateClientSettings(connectionString)` constructs settings from the string, then sets `LibraryInfo = new("MongoDB.VectorData", <assembly version>)` and `ApplicationName = "MongoDB.VectorData"`. These flow through to Mongo's wire-protocol handshake — don't drop them; they're how telemetry attributes traffic to this provider.
- **`MongoVectorStoreOptions`** — public, sealed. Only public property today: `EmbeddingGenerator`. Has an internal copy ctor used by the DI helpers above.
- **`MongoCollectionOptions`** — public, sealed, extends MEVD's `VectorStoreCollectionOptions` (so `Definition` and `EmbeddingGenerator` come from the base). Adds `VectorIndexName` (default `MongoConstants.DefaultVectorIndexName = "vector_index"`), `FullTextSearchIndexName` (default `MongoConstants.DefaultFullTextSearchIndexName = "full_text_search_index"`), `MaxRetries` (5), `DelayInMilliseconds` (1000), `NumCandidates` (nullable; null means "10× limit" computed in `MongoCollection.SearchAsync`). An internal `Default` singleton exists for the no-options ctor path. Has an internal copy ctor used by DI helpers.

What does *not* live here:

- The internal-only `MongoVectorStore` ctor pair / `MongoCollection` ctor pair — `collection-reviewer`.
- The MEVD-mandated overrides that `MongoVectorStore` and `MongoCollection` carry — those signatures come from MEVD, not from this codebase.

## Boundaries with adjacent code

- **vs MongoDB.Driver.** The provider sits on top of `IMongoDatabase` / `IMongoCollection<BsonDocument>`. It never opens its own `MongoClient` outside of the `connectionString` DI overloads. All pipeline construction is BSON-document-based — the provider does not use the driver's typed `IMongoCollection<TDocument>` LINQ path. **Don't reach into the driver's LINQ provider from this codebase** — Atlas Search and Vector Search are pipeline-stage features, and this provider models them by emitting BSON stages directly via `MongoCollectionSearchMapping`.
- **vs MEVD `Microsoft.Extensions.VectorData.ProviderServices`.** `CollectionModel`, `CollectionModelBuilder`, `PropertyModel` (and its `KeyPropertyModel` / `DataPropertyModel` / `VectorPropertyModel` subclasses), `FilterTranslatorBase`, `FilterPreprocessingOptions` are all upstream types from MEVD. The provider extends them; it does not redefine them. When MEVD ships new versions, expect signatures here to need updating in lockstep.
- **vs `Microsoft.Extensions.AI.IEmbeddingGenerator`.** Used only via `VectorPropertyModel.EmbeddingGenerator` / `GenerateEmbeddingAsync` / `GenerateEmbeddingsAsync` — never instantiated by the provider. Embedding-generator wiring is in the DI helpers and in `MongoCollection.ProcessEmbeddingsAsync` / `GetSearchVectorArrayAsync`.

## Common pitfalls

- **Auto-generated keys.** `MongoCollection.UpsertCoreAsync` only auto-generates for `Guid` and `ObjectId` — and only when the existing value is `Guid.Empty` / `ObjectId.Empty`. Other types throw `UnreachableException` because `MongoModelBuilder.SupportsKeyAutoGeneration` is supposed to gate this. If you broaden the supported types in one place, broaden both.
- **`_id` ↔ data-model-key swap.** Both `MongoMapper` and `MongoDynamicMapper` translate the user-visible key name to/from Mongo's reserved `_id`. The typed path is conditional on the key not already being `Id` and not being `[BsonId]`-attributed. The dynamic path always uses `_id` and looks up the model-key by name. A mismatch produces `KeyNotFoundException` from `BsonDocument["..."]`, not a clean exception — check both directions when changing the mapping rules.
- **Skip is local.** Atlas `$vectorSearch` doesn't support a server-side `skip`, so `MongoCollection.SearchAsync` / `HybridSearchAsync` over-fetch `skip + top` and discard in the shaper. If you change the pipeline shape, keep `EnumerateAndMapSearchResultsAsync`'s `skipCounter` in sync.
- **`numCandidates` defaults are surprising.** `MongoCollectionOptions.NumCandidates` is nullable — null means `limit × MongoConstants.DefaultNumCandidatesRatio (10)`. Conformance tests pin `1_000` explicitly (`MongoTestStore.ConformanceNumCandidates`) because the default is too small for the test corpus.
- **Search-index creation isn't idempotent without the same name.** `MongoCollection.CreateIndexesAsync` lists existing indexes and skips any whose name matches; it does not detect drift between a stale index with the same name and the field set the model now requires. Conformance tests work around this by giving each `WaitForDataAsync` invocation fresh unique names and dropping the old ones — see `MongoTestStore.RebuildSearchIndexesAsync`.
- **Search-index hooks are test-only.** `MongoCollectionTestHook.VectorIndexNameResolver` / `FullTextSearchIndexNameResolver` exist *exclusively* so the conformance fixture can override index names per collection. Don't add production callers.
- **`ConfigureAwait(false)` discipline.** Library code uses `.ConfigureAwait(false)` consistently. Async iterators flow it via `await foreach (... in source.ConfigureAwait(false))`; cancellation tokens flow via `[EnumeratorCancellation]` on the parameter.
- **Trim/AOT attributes are load-bearing.** Every method that goes through `BsonSerializer` for reflection-backed serialization, or that uses `dataModel.ToBsonDocument()`, must carry `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`. The dynamic path is the alternative we point users at in the messages on those attributes.
- **MEVD experimental warnings.** `MEVD9000` (`VectorStoreException` types-for-evaluation) is suppressed inside `VectorStoreErrorHandler`. `MEVD9001` (filter-translation base types) is suppressed at the top of `MongoFilterTranslator`. Both are intentional — re-evaluate if MEVD promotes those types out of experimental.

## How to test

Conformance tests cover most of the surface and are slow (Docker container + Atlas search indexes). Use them when changes touch the runtime path.

```bash
# Fast unit tests (no MongoDB)
dotnet test test/MongoDB.UnitTests

# All conformance tests (Docker required unless MongoDB:ConnectionURL is configured)
dotnet test test/MongoDB.ConformanceTests

# Single test class
dotnet test test/MongoDB.ConformanceTests --filter "FullyQualifiedName~MongoFilterTests"

# Single test method
dotnet test test/MongoDB.ConformanceTests --filter "FullyQualifiedName~MongoFilterTests.Equal"
```

The unit-tests project lives at `test/MongoDB.UnitTests/` and is the right place to land a new test that exercises mapping, filter translation, or DI registration with mocks — anything that doesn't actually need an Atlas-search index to evaluate.
