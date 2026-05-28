# AGENTS.md — MongoDB.VectorData provider

## Overview
The MongoDB provider for [Microsoft.Extensions.VectorData](https://learn.microsoft.com/dotnet/ai/vector-stores/overview) (MEVD). Bridges MEVD's `VectorStore` / `VectorStoreCollection<TKey, TRecord>` abstractions onto MongoDB Atlas Vector Search and Atlas Search, using the official [MongoDB C# driver](https://github.com/mongodb/mongo-csharp-driver). Records are stored as BSON documents and queried via the Atlas `$vectorSearch` / `$search` aggregation stages.

## Tech Stack
- Single source project (`src/MongoDB/`) packaged as `MongoDB.VectorData` on NuGet.
- **Multi-target**: `net10.0`, `net8.0`, `netstandard2.1`, `net472`. AOT-compatible on `net8.0`+ (`IsAotCompatible=true`), guarded with `[RequiresDynamicCode]` / `[RequiresUnreferencedCode]` on the non-dynamic paths. `netstandard2.1` and `net472` rely on shims under `src/LegacySupport/` for `[RequiresDynamicCode]`, `[CallerArgumentExpression]`, `System.Index/Range`, `UnreachableException`, `IsExternalInit`.
- **MEVD and AI abstractions** are pinned in `Directory.Packages.props`: `Microsoft.Extensions.VectorData.Abstractions`, `Microsoft.Extensions.AI.Abstractions`, `Microsoft.Extensions.VectorData.ConformanceTests` (test-only). The C# driver is pinned via `MongoDB.Driver`. Transitive pins `SharpCompress` / `Snappier` override vulnerable versions the driver brings in.
- `<Nullable>enable</Nullable>` everywhere via `Directory.Build.props`. `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` — every analyzer warning fails the build.
- `<ImplicitUsings>enable</ImplicitUsings>` — don't add redundant `using System;` lines to new files in `src/`.
- xUnit + Moq for tests. Conformance tests inherit from MEVD's shared test suite (`Microsoft.Extensions.VectorData.ConformanceTests`).
- `[assembly: CollectionBehavior(DisableTestParallelization = true)]` in `test/MongoDB.ConformanceTests/Properties/AssemblyInfo.cs` — conformance tests run serially (shared Atlas Local container + shared search indexes).

## Project Structure
- `src/MongoDB/` — the provider. **Flat** (no subdirectories) — area boundaries are file-based, not folder-based; see per-file groupings in `src/MongoDB/AGENTS.md`.
- `src/LegacySupport/` — compiler-attribute / BCL shims so `netstandard2.1` and `net472` can compile annotations and types added in later runtimes.
- `test/MongoDB.UnitTests/` — fast unit tests that don't touch MongoDB (mapping, filter translation, DI registration).
- `test/MongoDB.ConformanceTests/` — MEVD's standard provider-conformance suite plus MongoDB-specific tests (BSON mapping, hybrid search, key types). Requires a real MongoDB Atlas-search-capable server (Atlas Local container by default).

## Editing
- All `src/` code obeys `<Nullable>enable</Nullable>` — annotate new types accordingly.
- `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`: a new analyzer warning fails the build. If you must suppress, scope the suppression as narrowly as possible (file-level `#pragma warning disable X` with a matching `restore`, or a `[SuppressMessage]` with justification).
- Conditional code across target frameworks uses the BCL-defined `NET` symbol (i.e. `#if NET` for `net8.0`/`net10.0`, the implicit `else` covers `netstandard2.1` and `net472`). Patterns to watch for: `DateOnly` is gated `#if NET`; `connection.DisposeAsync` vs `connection.Dispose` likewise; `[ModuleInitializer]` is shimmed on the legacy targets via `src/LegacySupport/`.
- AOT-compat: methods that use reflection-driven BSON serialization (`MongoCollection<TKey, TRecord>` non-dynamic ctor, `MongoVectorStore.GetCollection<TKey, TRecord>`, all `AddMongoCollection<TRecord>` and `AddMongoVectorStore` overloads, `MongoServiceCollectionExtensions` extension methods registering them) carry `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`. The dynamic path (`MongoDynamicCollection` / `GetDynamicCollection`) is the trim-/AOT-safe alternative — do not add reflection without the attributes, and don't drop the attributes from existing methods.
- Preserve any file BOM you find. New files don't need one.

## Versioning conventions
This is a **preview** package (`<Version>1.0.0-preview.1</Version>` in `src/MongoDB/MongoDB.csproj`). API surface is allowed to change, but every break should still be conscious — particularly anything in MEVD-abstraction overrides (whose shapes are dictated by MEVD itself), DI-extension signatures, public option-class properties / defaults, and observable BSON shape (storage names, key element name `_id`, vector serialization). Treat as breaks:

- Public API signature, default, or visibility changes on `MongoVectorStore`, `MongoCollection<TKey, TRecord>`, `MongoDynamicCollection`, `MongoVectorStoreOptions`, `MongoCollectionOptions`, `MongoServiceCollectionExtensions`.
- Behavior changes that alter persisted-document shape: storage-name policy, the `_id` ↔ data-model-key mapping in `MongoMapper`, Guid representation (standard via the convention in `MongoMapper`), vector serialization shape.
- Default-value changes in `MongoCollectionOptions` (`VectorIndexName`, `FullTextSearchIndexName`, `MaxRetries`, `DelayInMilliseconds`, `NumCandidates`).
- `MongoConstants` values that escape into stored data or indexes — `DefaultVectorIndexName`, `DefaultFullTextSearchIndexName`, `MongoReservedKeyPropertyName`, `DefaultDistanceFunction`, `DefaultIndexKind`.

## Async conventions
The provider implements MEVD's async surface (every `VectorStoreCollection<TKey, TRecord>` override is async). Library code uses `ConfigureAwait(false)` consistently — both for `Task`/`Task<T>` await and for `IAsyncEnumerable` (`await foreach (... in source.ConfigureAwait(false))`). `CancellationToken` flows from MEVD callers through to driver calls unchanged; new async methods must take a `CancellationToken` (annotated `[EnumeratorCancellation]` on async iterators) and pass it on.

## Commands
- Build everything: `dotnet build MongoDB.VectorData.slnx -c Release`.
- Run unit tests (no MongoDB needed): `dotnet test test/MongoDB.UnitTests`.
- Run conformance tests against a local Atlas-search container (Docker required): `dotnet test test/MongoDB.ConformanceTests`. The fixture boots `mongodb/mongodb-atlas-local:latest` via Testcontainers if no `MongoDB:ConnectionURL` is configured.
- Run conformance tests against a specific cluster: either set `MongoDB__ConnectionURL=<conn-string>` in the environment, or create `test/MongoDB.ConformanceTests/testsettings.development.json` with `{ "MongoDB": { "ConnectionURL": "<conn-string>" } }` (gitignored). See `test/MongoDB.ConformanceTests/Support/MongoTestEnvironment.cs`.
- Run one conformance test class: `dotnet test test/MongoDB.ConformanceTests --filter "FullyQualifiedName~MongoFilterTests"`.

## Testing
- **Unit tests** mock `IMongoCollection<BsonDocument>` / `IMongoDatabase` via Moq and assert mapper output, filter-translator BSON output, and DI-registration shape. No MongoDB connection required.
- **Conformance tests** require an Atlas-search-capable cluster. `MongoTestEnvironment` (`test/MongoDB.ConformanceTests/Support/`) reads `MongoDB:ConnectionURL` from `testsettings.json` / `testsettings.development.json` / environment variables. If none is set, `MongoTestStore.StartAsync()` boots a `mongodb/mongodb-atlas-local:latest` container via `MongoDbAtlasBuilder` and reuses it for the test-process lifetime.
- Atlas Search index builds are eventually consistent and Atlas Local's incremental indexing is unreliable. `MongoTestStore.WaitForDataAsync` drops + recreates the search indexes with fresh unique names per call, then polls `$listSearchIndexes` until queryable. See the conformance-tests `AGENTS.md` for the full flakiness mitigation story.
- The `Microsoft.Extensions.VectorData.ConformanceTests` package supplies most test classes; the project mostly provides MongoDB-specific fixture classes and a handful of MongoDB-only tests (`MongoBsonMappingTests`, `MongoIndexKindTests`, etc.).

| Configuration | Environment variable / setting |
|---|---|
| Connect to specific MongoDB cluster | `MongoDB__ConnectionURL` env var **or** `MongoDB:ConnectionURL` in `testsettings(.development)?.json` |
| Fall back to local Atlas container | (unset both above) — `MongoDbAtlasBuilder` spins `mongodb/mongodb-atlas-local:latest` |

## Commit and PR conventions
- The `AssemblyName` and `RootNamespace` are both `MongoDB.VectorData` — keep `using` directives consistent.
- This repo does not have a JIRA-prefix convention baked into commit messages (no equivalent of the EF-core provider's `EF-NNNN:` prefix). Match the existing branch naming if there is one on `main`; otherwise use a concise descriptive subject.

## Functional areas

The source tree is flat, so area boundaries are by file group rather than by directory. Each area has its own section in `src/MongoDB/AGENTS.md` and a corresponding read-only reviewer sub-agent in `.claude/agents/`.

| Area | Files | Reviewer |
|---|---|---|
| Collection lifecycle, CRUD, search execution, retry & error handling | `MongoCollection.cs`, `MongoDynamicCollection.cs`, `MongoVectorStore.cs`, `MongoCollectionCreateMapping.cs`, `MongoCollectionTestHook.cs`, `MongoConstants.cs`, `VectorStoreErrorHandler.cs`, `ErrorHandlingAsyncCursor.cs`, `Throw.cs` | `collection-reviewer` |
| Record↔BSON mapping & model building | `MongoMapper.cs`, `MongoDynamicMapper.cs`, `MongoModelBuilder.cs`, `BsonValueFactory.cs`, `IMongoMapper.cs` | `mapping-reviewer` |
| LINQ-filter translation & search-pipeline construction | `MongoFilterTranslator.cs`, `MongoCollectionSearchMapping.cs` | `search-reviewer` |
| Public DI extensions & user-facing options | `MongoServiceCollectionExtensions.cs`, `MongoVectorStoreOptions.cs`, `MongoCollectionOptions.cs` | `public-api-reviewer` |
| Conformance-test suite & MongoDB-specific test infra | `test/MongoDB.ConformanceTests/**` | `conformance-reviewer` |

## Cross-cutting reviewers

These reviewers have no per-area `AGENTS.md`; they apply a single lens across the whole diff and run on **every** invocation of `/review-mevd-provider`.

| Concern | Reviewer |
|---|---|
| Public API / breaking changes (signatures, defaults, visibility, behavior of unchanged signatures, persisted-document shape) | `api-stability-reviewer` |
| MEVD abstraction compliance (correct override shape on `VectorStore` / `VectorStoreCollection<TKey, TRecord>` / `IKeywordHybridSearchable<TRecord>`); multi-target hygiene (`net10`/`net8`/`netstandard2.1`/`net472`, AOT/trim attributes); driver-version compatibility | `mevd-conformance-reviewer` |
| Security — credential redaction in connection strings, no hardcoded credentials, TLS surfaces, no exception messages that leak connection strings or KMS material | `security-reviewer` |

## PR-summary reviewer (external PR mode only)

When `/review-mevd-provider` is invoked with a PR number, one additional reviewer runs:

| Concern | Reviewer |
|---|---|
| Holistic "what does this PR do, and is it a good change?" — reads the PR body and the full diff | `pr-summary-reviewer` |

## External references

- [Microsoft.Extensions.VectorData docs](https://learn.microsoft.com/dotnet/ai/vector-stores/overview) — authoritative for the MEVD abstractions (`VectorStore`, `VectorStoreCollection<TKey, TRecord>`, `IVectorSearchable<TRecord>`, `IKeywordHybridSearchable<TRecord>`, `VectorStoreCollectionDefinition`, `VectorStoreProperty` subtypes).
- [Microsoft.Extensions.VectorData source](https://github.com/dotnet/extensions/tree/main/src/Libraries/Microsoft.Extensions.VectorData.Abstractions) — for `ProviderServices` internals (`CollectionModel`, `CollectionModelBuilder`, `FilterTranslatorBase`, `PropertyModel` subtypes) that the provider extends.
- [MongoDB.Driver](https://github.com/mongodb/mongo-csharp-driver) — every BSON serializer, `IMongoCollection<BsonDocument>` call, and pipeline stage the provider emits lives here.
- [Atlas Vector Search docs](https://www.mongodb.com/docs/atlas/atlas-vector-search/vector-search-stage/) — `$vectorSearch` semantics, `numCandidates`, similarity functions, pre-filter constraints.
- [Atlas Search docs](https://www.mongodb.com/docs/atlas/atlas-search/) — `$search`, full-text index definitions, `text` operator semantics.
- [Atlas Local](https://www.mongodb.com/docs/atlas/cli/current/atlas-cli-deploy-local/) — the `mongodb/mongodb-atlas-local` Docker image used by the conformance Testcontainers fixture.
