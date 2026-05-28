---
area: Conformance tests & MongoDB-specific test infrastructure
scope: ["test/MongoDB.ConformanceTests/**"]
reviewer-agent: conformance-reviewer
adjacent-areas: [all source areas — every conformance test exercises src/MongoDB/ end-to-end]
---

# MongoDB.ConformanceTests — AGENTS.md

The conformance / functional-test project. Runs MEVD's shared `Microsoft.Extensions.VectorData.ConformanceTests` suite plus MongoDB-specific tests (BSON mapping interplay, hybrid search, key-type matrix, index-kind matrix). Requires a real Atlas-search-capable MongoDB server.

## Scope

In:

- **The MEVD shared suite usage.** Test classes inherit upstream bases from `VectorData.ConformanceTests` (e.g. `FilterTests<TKey>`, `DataTypeTests<TKey>`, `BasicModelTests`, `HybridSearchTests`) and supply a `Fixture` whose `TestStore` is `MongoTestStore.Instance`. Most test classes are 5–30 lines of overrides — the real bodies live upstream. The provider-specific tests live alongside (`MongoBsonMappingTests`, `MongoIndexKindTests`, `MongoDistanceFunctionTests`, `MongoEmbeddingGenerationTests`, `MongoDependencyInjectionTests`, `MongoTestSuiteImplementationTests`).
- **MongoDB-specific test infrastructure** under `Support/`:
  - `MongoTestStore` — the shared `TestStore` instance, single-instance per process. Owns the `IMongoDatabase` (`"VectorSearchTests"`), the lazily-started Atlas Local container (when no `MongoDB:ConnectionURL` is set), and the per-collection-name index-name tracking dictionary that powers the rebuild-on-`WaitForDataAsync` strategy. Overrides `CreateCollection<TKey, TRecord>` / `CreateDynamicCollection` / `WaitForDataAsync` / `GenerateKey<TKey>` from MEVD's base. Installs `MongoCollectionTestHook.VectorIndexNameResolver` / `FullTextSearchIndexNameResolver` in its static ctor so every `MongoCollection` constructed during a test resolves the *current* tracked index name for its collection.
  - `MongoFixture` — `VectorStoreFixture` that points `TestStore` at `MongoTestStore.Instance`. Used by the test-classes' `IClassFixture<...>`.
  - `MongoDbAtlasBuilder` / `MongoDbAtlasContainer` — Testcontainers builder for `mongodb/mongodb-atlas-local:latest`. The `WaitIndicateReadiness` strategy creates a throwaway database, attempts to create a vector-search index on a throwaway collection, lists it back, and only declares the container ready when that succeeds — Atlas Local's search service comes up well after Mongo itself.
  - `MongoTestEnvironment` — reads `MongoDB:ConnectionURL` from `testsettings.json`, `testsettings.development.json`, and environment variables (via `Microsoft.Extensions.Configuration`). Configurable from any of the three — the development JSON file is gitignored for local-cluster use.
- **Per-folder test breakdown:**
  - `ModelTests/` — `BasicModelTests`, `NoDataModelTests`, `MultiVectorModelTests`, `NoVectorConformanceTests`, `DynamicModelTests`. Each is a thin override that supplies the Mongo fixture and (rarely) overrides a specific test.
  - `TypeTests/` — `MongoDataTypeTests`, `MongoKeyTypeTests`, `MongoEmbeddingTypeTests`. Cover the supported-data-type matrix (`MongoModelBuilder.IsDataPropertyTypeValid`), the key-type matrix (`MongoModelBuilder.ValidateKeyProperty`), and the embedding-type matrix (`MongoModelBuilder.IsVectorPropertyTypeValid`).
  - Top-level test classes — `MongoFilterTests` (the largest single override list — see below), `MongoHybridSearchTests`, `MongoCollectionManagementTests`, `MongoIndexKindTests`, `MongoDistanceFunctionTests`, `MongoEmbeddingGenerationTests`, `MongoDependencyInjectionTests`, `MongoBsonMappingTests`, `MongoTestSuiteImplementationTests`.

Out: per-area `src/` files — those are owned by `collection-reviewer` / `mapping-reviewer` / `search-reviewer` / `public-api-reviewer`. A test that exercises filter translation gets reviewed by both the `search-reviewer` (assertion logic) and the `conformance-reviewer` (fixture discipline / inheritance).

## Test isolation and parallelism

- **Parallelization is off** at the assembly level: `test/MongoDB.ConformanceTests/Properties/AssemblyInfo.cs` has `[assembly: CollectionBehavior(DisableTestParallelization = true)]`. Shared `MongoTestStore.Instance` and the per-collection-name index-name dictionary both depend on serial execution.
- **Database is shared.** All conformance tests use the same `"VectorSearchTests"` database on whichever cluster is configured. Per-test isolation happens at the **collection name** level — MEVD's base helpers and `TestStore.AdjustCollectionName(...)` derive unique collection names per test.
- **Container is shared across fixtures.** `MongoTestStore.StopAsync` deliberately does nothing — restarting the Atlas Local container between fixture lifetimes churns the search service and the replica set. Testcontainers cleans up the container when the test process exits.

## The search-index rebuild dance

Atlas Vector Search index builds are eventually consistent. Atlas Local's incremental indexing is unreliable for *both* inserts and deletes — a stale index can return stale results long after a test has rewritten its corpus. MEVD's base `TestStore.WaitForDataAsync` polls until the data appears via `GetAsync(filter)`, but that's a `Find`-based check; vector / FTS queries need the search index to be queryable and have re-ingested the latest data, which is a separate signal.

Mitigation, all in `MongoTestStore`:

1. **Defer index creation.** `CreateCollection<TKey, TRecord>` / `CreateDynamicCollection` instantiate the `MongoCollection` with `DeferSearchIndexCreation = true`. `EnsureCollectionExistsAsync` then creates the underlying Mongo collection but **not** the search indexes. The test populates the collection first.
2. **Rebuild with fresh names per `WaitForDataAsync`.** `WaitForDataAsync` calls `RebuildSearchIndexesAsync`: drop any indexes left over from a previous call (tracked in `s_indexesByCollectionName`), generate fresh `vector_idx_<guid>` / `fts_idx_<guid>` names, then call `MongoCollection.CreateSearchIndexesAsync()` to build them over the current dataset in one pass. Fresh names per rebuild avoid a known Atlas Search bug where same-name drop/recreate inside a short window leaves the index unqueryable.
3. **Resolve the current name on every read.** `MongoCollection.VectorIndexName` / `FullTextSearchIndexName` go through `MongoCollectionTestHook.VectorIndexNameResolver` / `FullTextSearchIndexNameResolver`. `MongoTestStore`'s static ctor installs resolvers that look up the latest tracked name per collection, so every typed and dynamic `MongoCollection` that exists during the test reads from the freshly-rebuilt index — regardless of whether the test went via `TestStore.CreateCollection`, `VectorStore.GetCollection`, or the DI path.
4. **Poll until queryable.** After the rebuild, `WaitForSearchIndexesAsync` runs `$listSearchIndexes` until every expected index reports `queryable: true` (up to 5 minutes — Atlas Search index builds can genuinely take that long on cold containers). `WaitForFullTextSearchDataAsync` runs a `$search.exists` pipeline until the record count matches (up to 3 minutes).
5. **Fallback retry on the outer `WaitForDataAsync`.** If the base `WaitForDataAsync`'s "data did not appear" exception fires within the 3-minute deadline, the loop sleeps 2s and retries — covering the case where index creation succeeds but the underlying data hasn't been re-ingested yet.

**If you change this dance, expect intermittent conformance failures.** The previous commit history (`b996c99 Rebuild conformance search indexes on every WaitForDataAsync`, `af9a074 Defer search-index creation in conformance tests`, `97199b4 Address review feedback on conformance container startup`, `a4fe2a5 Harden conformance test infrastructure`) is a record of just how easily this surface becomes flaky.

## Override patterns

MEVD's shared test suite is the source of truth for what the provider must support. The provider-side test class either:

- **Inherits and adds Mongo-specific test methods only** (most common — `MongoBsonMappingTests`, `MongoEmbeddingGenerationTests`).
- **Inherits and overrides individual upstream tests** that are permanently unsupported on MongoDB, replacing them with `Assert.ThrowsAsync<NotSupportedException>(base.UpstreamTest)`. This is the dominant pattern in `MongoFilterTests` — MongoDB's vector-search pre-filter doesn't support null checks, arbitrary `NOT`, `Contains` over array fields, or several `Any(...)` shapes, and each constraint becomes a `ThrowsAsync` override.
- **Inherits, overrides, and replaces** an upstream test with a MongoDB-specific shape (e.g. `[Fact] Not_over_Contains` in `MongoFilterTests` uses the `!new[]{...}.Contains(field)` shape that *does* translate to `$nin`, while the base class's `Not_over_Contains` shape doesn't).

When upstream MEVD adds new tests in a new release, **every provider-side override list needs revisiting** to confirm the new tests pass (and to add `NotSupportedException` overrides for any new constraint mismatch). This is a recurring maintenance cost — call it out in a review when MEVD is upgraded in `Directory.Packages.props`.

## Common pitfalls

- **Don't enable test parallelization.** Shared `MongoTestStore.Instance`, the per-collection-name index-name dictionary, and the shared Mongo database all require serial execution. `[assembly: CollectionBehavior(DisableTestParallelization = true)]` in `Properties/AssemblyInfo.cs` is load-bearing.
- **Don't fetch `IMongoCollection<BsonDocument>` outside the fixture.** `MongoTestStore` reaches the driver collection via `mongoCollection.GetService(typeof(IMongoCollection<BsonDocument>))` because the `MongoCollection` `GetService` hook is the public escape hatch. Adding direct property access would couple test infrastructure to internals.
- **Don't stop the Atlas Local container between fixtures.** `StopAsync` deliberately no-ops. Restarting it churns the search service and replica set; Testcontainers handles process-exit cleanup.
- **Don't add `Skip` for "flaky" tests** without first ruling out the rebuild dance — most flakiness in this suite traces back to Atlas Search index visibility or to skipping a `WaitForDataAsync` rebuild. If a real upstream test is permanently unsupported on MongoDB, mark it with `Assert.ThrowsAsync<NotSupportedException>(base.OriginalTest)` rather than a silent skip — that documents the constraint.
- **Don't hard-code index names.** The fresh-per-rebuild `vector_idx_<guid>` / `fts_idx_<guid>` names are essential. Don't write a test that depends on a specific index name; if you need to assert on the index, route through the tracking dictionary or via `MongoCollectionTestHook`.
- **Configuration precedence: env > development.json > settings.json.** `MongoTestEnvironment.cs` builds its `IConfiguration` in that order. CI sets `MongoDB__ConnectionURL`; developers use `testsettings.development.json` (gitignored); `testsettings.json` (committed) is the documentation-shaped default.
- **`testsettings.development.json` must stay gitignored.** It is the place to put cluster connection strings with credentials. If a credential-shaped string appears in a committed file, that's a security regression.

## How to run

```bash
# Full conformance suite (Docker required unless MongoDB:ConnectionURL is set)
dotnet test test/MongoDB.ConformanceTests

# Use an existing cluster instead of Atlas Local
MongoDB__ConnectionURL="mongodb+srv://..." dotnet test test/MongoDB.ConformanceTests

# Single test class
dotnet test test/MongoDB.ConformanceTests --filter "FullyQualifiedName~MongoFilterTests"

# Skip the slow conformance suite; just unit tests
dotnet test test/MongoDB.UnitTests
```

If a conformance test fails locally but passes in CI (or vice versa), the first thing to check is the Atlas Local container version (`mongodb/mongodb-atlas-local:latest` — the tag floats) and whether `WaitForDataAsync` was actually called between the test's mutation and assertion.
