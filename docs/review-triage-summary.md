# Code-review triage summary — MongoDB.VectorData provider

Living record of the code-review findings investigated for the `MongoDB.VectorData`
provider, the verdict for each, and how it was resolved. Each finding was investigated
with systematic debugging (root-cause first) and, where a change was warranted, fixed
test-first (a failing test reproducing the issue, then the fix). Each fix lives on its
own branch off `main` (mirroring the existing `Fix*OnMain` PR branches) so it can be a
focused PR.

> **`main` build caveat:** `main` does not yet carry the transitive `SharpCompress` /
> `Snappier` security pins (those live on the `Infra` branch / PR #2). A plain
> `dotnet restore`/`test` on any branch cut from `main` fails with `NU1902`/`NU1903`
> (vulnerable-package-as-error). All fixes here were verified with `-p:NuGetAudit=false`
> (verification only — no `.csproj` change committed); they build clean and pass once
> `Infra` is merged and the branch is rebased.

## Summary

| # | Finding | Verdict | Resolution | Branch / PR |
|---|---------|---------|------------|-------------|
| 1 | `MongoMapper` ignores `StorageName` for data properties | ❌ False positive | No change | — |
| 2 | `CreateIndexesAsync` queries the wrong API for search indexes | ✅ Real bug | Use `SearchIndexes.ListAsync` | `Fix4OnMain` / PR #6 |
| 3 | CI workflow references non-existent action versions | ❌ False positive | No change | — |
| 4 | `ListCollectionNamesAsync` iterates the raw cursor | ✅ Real bug | (already fixed) | `Fix2OnMain` / PR #5 |
| 5 | `HybridSearchAsync` passes a raw cursor / leaks it | ✅ Real bug | Wrap cursor like `SearchAsync` | `Fix5OnMain` / PR #7 |
| 6 | `MapFromStorageToDataModel` mutates the caller's `BsonDocument` | ⚠️ Latent | Operate on a shallow copy | `FixMapperMutationOnMain` / PR #8 |
| 7 | `GetStorageKey` throws `InvalidCastException` for ObjectId keys | ❌ False positive | No change | — |
| 8 | `MongoMapper` ctor registers global conventions per instance | ✅ Real bug | Register in static ctor | `FixConventionRegistrationOnMain` / PR #9 |
| 9 | `DefaultDistanceFunction` constant inconsistent with mapping | ⚠️ Latent | Set to `CosineSimilarity` | `FixDefaultDistanceFunctionOnMain` (pushed) |
| 10 | Batch `UpsertAsync` issues N round trips | ✅ Real (perf) | Single `BulkWriteAsync` | `FixBatchUpsertBulkWriteOnMain` (pushed) |
| 11 | `GetFullTextSearchQuery` accepts but ignores `filter` | ⚠️ Partly valid | Remove dead parameter | `FixUnusedFtsFilterParamOnMain` (pushed) |
| 12 | Trim/AOT attribute messages swapped on six DI overloads | ✅ Real bug | Swap to correct pairing | `FixSwappedAotMessagesOnMain` (pushed) |
| 13 | `VectorStoreErrorHandler` carries dead SQL-era code + wrong doc | ✅ Real (cleanup) | Remove dead code, fix summary | `FixDeadSqlErrorHandlerCodeOnMain` |
| 14 | `RunOperationWithRetryAsync` unreachable throw + `maxRetries=0` edge | ✅ Real bug | `while (true)`, attempt-once minimum | `FixRetryZeroAndDeadThrowOnMain` |
| 15 | DI collection extensions hard-code `TKey = string` | ⚠️ By design | Document the limitation | `DocumentDiStringKeyOnMain` |
| 16 | `MongoDynamicMapper` stores null vectors as empty arrays | ✅ Real bug | Store `BsonNull.Value` | `FixDynamicNullVectorOnMain` |
| 17 | `MongoConstants.Supported*Types` sets are dead + misleading | ✅ Real (cleanup) | Remove all three sets | `RemoveDeadSupportedTypesConstantsOnMain` |
| 18 | `GetVectorIndexFields` ignores `IndexKind`; `Dimensions` "unvalidated" | ⚠️ Mixed | Dimensions = false positive; document IndexKind | `DocumentVectorIndexKindOnMain` |

Legend: ✅ real bug fixed · ⚠️ latent/partly-valid (hardened or scoped) · ❌ false positive (no change).

---

## Details

### 1. `MongoMapper` ignores `StorageName` for data properties — ❌ False positive
**Claim:** the typed mapper writes data properties under the CLR name, so filters/index (which use `StorageName`) target a non-existent field.
**Finding:** `MongoModelBuilder` sets `UsesExternalSerializer = true`, and MEVD's `CollectionModelBuilder.SetPropertyStorageName` deliberately does **not** apply the MEVD `StorageName` to CLR-backed (typed) properties under that option. So `model.StorageName` for a typed property always equals the BSON element name (`[BsonElement]` name, else CLR name) — exactly what `ToBsonDocument`/`Deserialize` use. Verified empirically: `[VectorStoreData(StorageName="mevd_name")]` yields `model.StorageName == "A"` (CLR name), never `"mevd_name"`. No divergence is possible. **No change.**

### 2. `CreateIndexesAsync` queries the wrong API for Atlas Search indexes — ✅ Real bug
**Root cause:** listed existing indexes via `IMongoCollection.Indexes.ListAsync()` (regular `listIndexes`), which never returns Atlas Search / Vector Search indexes. The `Contains` checks always missed, so `createSearchIndexes` ran on every call; the second `EnsureCollectionExistsAsync` failed with "index already exists" → not idempotent.
**Fix:** query `SearchIndexes.ListAsync()` (`$listSearchIndexes`). Test asserts no `createSearchIndexes` command when both indexes already exist. → `Fix4OnMain` / PR #6.

### 3. CI workflow references non-existent action versions — ❌ False positive
**Claim:** `checkout@v6`, `setup-dotnet@v5`, `upload-artifact@v7` "do not exist — current is v4".
**Finding:** `git ls-remote --tags` shows all three are the **latest** published majors (checkout v6, setup-dotnet v5, upload-artifact v7). The reviewer's "current is v4" was stale; the workflow references valid versions. Downgrading to v4 would be a regression. **No change.**

### 4. `ListCollectionNamesAsync` iterates the raw cursor — ✅ Real bug
**Root cause:** constructed an `ErrorHandlingAsyncCursor<string>` but the loop iterated the raw `cursor`, so `MongoException`s during paging escaped untranslated (not wrapped in `VectorStoreException`).
**Resolution:** already fixed independently in PR #5 (iterate the wrapper). My duplicate local work was reverted. → `Fix2OnMain` / PR #5.

### 5. `HybridSearchAsync` passes a raw cursor into the enumerator — ✅ Real bug
**Root cause:** the retry lambda created the cursor and returned a lazy enumerable over the **raw** cursor: no `ErrorHandlingAsyncCursor` wrap (errors escape raw), no `using` (cursor leak), and the retry covered only cursor creation (so `MaxRetries` had no effect).
**Fix:** mirror `SearchAsync` — retry wraps creation, `using var cursor`, wrap in `using var errorHandlingAsyncCursor`, pass the wrapper to the enumerator. Tests: error-translation + happy-path. → `Fix5OnMain` / PR #7.

### 6. `MapFromStorageToDataModel` mutates the caller's `BsonDocument` — ⚠️ Latent
**Finding:** the mapper mutates its input (key remap, vector wrap/remove). Not an active bug — every call site maps a document once and discards it, the score is read before mapping, the document never escapes to a caller, and the driver materializes each batch document independently. But it is a fragile contract (and asymmetric with `MongoDynamicMapper`, which never mutates).
**Fix (hardening):** operate on a shallow copy (`new BsonDocument(storageModel)`) at method entry. Shallow keeps per-result cost low and fully isolates the caller's document. Tests assert the input is unchanged for both `includeVectors` values. → `FixMapperMutationOnMain` / PR #8.

### 7. `GetStorageKey` throws `InvalidCastException` for ObjectId keys — ❌ False positive
**Claim:** `(TKey)BsonTypeMapper.MapToDotNetValue(...)` fails for ObjectId.
**Finding:** `MapToDotNetValue` returns `AsObjectId` (an `ObjectId`) for `BsonType.ObjectId`, and `ToGuid()` (a `Guid`) for `UuidStandard` binary — which is exactly how the provider stores Guid keys. Verified empirically: upserts with ObjectId (explicit and auto-generated), Guid, int, and long keys all pass through `GetStorageKey` without throwing. **No change.**

### 8. `MongoMapper` ctor registers global conventions per instance — ✅ Real bug
**Finding:** the "race condition" framing is inaccurate (the driver's `ConventionRegistry` locks all access), but the real problem is real: `Register` appends without deduplicating, and `MongoVectorStore.GetCollection` creates a fresh mapper per call, so the process-global list grew unbounded (a leak) and `Lookup` re-appended the same conventions repeatedly.
**Fix:** move registration to a static constructor (runs once per closed generic type, before the first class map is built). Test asserts the registration count does not grow with repeated instantiation. → `FixConventionRegistrationOnMain` / PR #9.

### 9. `DefaultDistanceFunction` constant inconsistent with the mapping — ⚠️ Latent
**Finding:** the constant was `DistanceFunction.CosineDistance`, but the distance-function switch maps only `CosineSimilarity`/`null` to Atlas `"cosine"` (`CosineDistance` would throw `NotSupportedException`). The constant is currently **unused** (the switch is fed `property.DistanceFunction`, whose `null` case already maps to `"cosine"`), so not an active bug — but the value was wrong and a landmine if ever wired up.
**Fix:** set it to `CosineSimilarity` and document the constraint. Test routes the constant through `GetVectorIndexFields` and asserts `"cosine"`. → `FixDefaultDistanceFunctionOnMain` (pushed).

### 10. Batch `UpsertAsync` issues N round trips — ✅ Real (performance)
**Finding:** the batch overload looped `ReplaceOneAsync` per record — N round trips.
**Fix:** build one `ReplaceOneModel<BsonDocument> { IsUpsert = true }` per record and send a single (ordered) `BulkWriteAsync`. Ordered preserves the prior stop-at-first-error semantics; the empty batch stays a no-op (guarded); single-record upsert still uses `ReplaceOneAsync`. Tests: single bulk write of N upsert models + empty-batch no-op. → `FixBatchUpsertBulkWriteOnMain` (pushed).

### 11. `GetFullTextSearchQuery` accepts but ignores `filter` — ⚠️ Partly valid
**Finding:** the parameter is genuinely dead (never referenced); the filter is correctly applied by the caller as a `$match` after `$search`. The reviewer's suggested optimization (embed the filter inside `$search`) is **not** a simple rewire: `$search` does not accept an MQL filter — it would need a dedicated MQL→Atlas-Search-operator translator (a larger, behavior-affecting change). Scoped per decision.
**Fix:** remove the dead parameter and document where the full-text filter is applied. Characterization test locks the behavior (filter via `$match`, not embedded in `$search`; vector branch still embeds in `$vectorSearch.filter`). → `FixUnusedFtsFilterParamOnMain` (pushed).

### 12. Trim/AOT attribute messages swapped on six DI overloads — ✅ Real bug
**Root cause:** six `MongoServiceCollectionExtensions` overloads paired the attribute messages backwards — `[RequiresUnreferencedCode(DynamicCodeMessage)]` / `[RequiresDynamicCode(UnreferencedCodeMessage)]`. So under `PublishTrimmed` the IL2026 warning showed the NativeAOT message and under `PublishAot` the IL3050 warning showed the trimming message, pointing developers at the wrong remediation. The other four overloads were already correct.
**Fix:** swap the six to `[RequiresUnreferencedCode(UnreferencedCodeMessage)]` / `[RequiresDynamicCode(DynamicCodeMessage)]`. Reflection test asserts every annotated method carries the trimming message on `[RequiresUnreferencedCode]` and the NativeAOT message on `[RequiresDynamicCode]`. → `FixSwappedAotMessagesOnMain` (pushed).

**Follow-up (same branch):** unified the remediation tail of both messages from "…in a way that's compatible with NativeAOT" to "…in a way that's compatible with trimming, as needed by NativeAOT" (trimming-compatibility is the real requirement, which NativeAOT also needs). The problem-description leads are unchanged, so the swap test still holds. No other "compatible with …" remediation phrasing exists elsewhere — the `MongoCollection` / `MongoVectorStore` messages use a different, more specific remediation ("instantiate `MongoDynamicCollection`" / "call `GetDynamicCollection()`").

### 13. `VectorStoreErrorHandler` carries dead SQL-era code + wrong doc — ✅ Real (cleanup)
**Finding:** the file contained helpers copied from a relational provider that the MongoDB provider never uses — `ExecuteWithErrorHandlingAsync(this DbConnection, …)` (×2), `ReadWithErrorHandlingAsync(this DbDataReader, …)` (×2), and the `ConfiguredCancelableErrorHandlingAsyncEnumerable<TResult, TException>` struct. Verified zero references outside the file (the provider streams via `ErrorHandlingAsyncCursor`). The file-level XML doc also wrongly described the class as "helpers for reading vector store model properties and their attributes."
**Fix:** remove the dead members (and the now-unused `System.Data.Common` / `System.IO` usings) and rewrite the summary to describe what the class actually does (run operations and translate exceptions to `VectorStoreException`). The retained `RunOperation*` members are unchanged. Verified by the build (nothing referenced the removed code) + green suite. → `FixDeadSqlErrorHandlerCodeOnMain`.

### 14. `RunOperationWithRetryAsync` unreachable throw + `maxRetries=0` edge — ✅ Real bug
**Root cause:** both retry overloads used `while (retries < maxRetries)` with a throw after the loop. For `maxRetries >= 1` that post-loop throw was unreachable dead code (the loop always returns on success or throws once retries are exhausted). For `maxRetries == 0` the loop body never ran and the post-loop throw fired immediately with an *empty* `AggregateException` — the operation was never attempted.
**Fix (chosen: always attempt once):** restructure to `while (true)` so the operation is attempted at least once and the in-catch `if (retries >= maxRetries)` throws once retries are exhausted (carrying the real exceptions). This deletes the unreachable post-loop throw (also required for CS0162 under warnings-as-errors) and makes `maxRetries == 0` mean "try once, no retry" (throwing the actual failure, not an empty aggregate). `maxRetries >= 1` behavior is unchanged. Tests cover both overloads: `maxRetries=0` success/failure, exactly-N attempts, and recovery after transient failures. → `FixRetryZeroAndDeadThrowOnMain`.

### 15. DI collection extensions hard-code `TKey = string` — ⚠️ By design (documented)
**Finding:** `AddMongoCollection` / `AddKeyedMongoCollection` register `MongoCollection<string, TRecord>`; a caller wanting a `Guid`/`ObjectId`/`int`/`long`-keyed collection can't use them. This is intentional (provider AGENTS.md: matches the Microsoft.Extensions.VectorData `AddXxxCollection` convention, and exposing `TKey` through DI is "a major API addition") and has a workaround — `MongoVectorStore.GetCollection<TKey, TRecord>` supports all five key types — but the limitation was undocumented.
**Fix (chosen: document):** add a `<remarks>` to all six collection-registration overloads noting they register a string-keyed collection and pointing callers who need another key type to `GetCollection<TKey, TRecord>`. Documentation only; no API change. (Declined the larger option of adding `TKey` overloads, which would diverge from the MEVD DI convention.) → `DocumentDiStringKeyOnMain`.

### 16. `MongoDynamicMapper` stores null vectors as empty arrays — ✅ Real bug
**Root cause:** `MapFromDataToStorageModel` mapped a null vector to `Array.Empty<object>()` and wrapped the whole switch in `BsonArray.Create`, persisting a null vector as `[]`. On read, `[]` is not BSON null, so it deserialized to a zero-length `ReadOnlyMemory<float>` rather than `null` — silent semantic data loss (null became "empty vector"). The read path already mapped BSON null back to `null`, so only the write side was wrong.
**Fix:** map a null vector to `BsonNull.Value` (moving `BsonArray.Create` into the non-null arms); null vectors now round-trip to `null`. Non-null vectors are unaffected. Tests: updated the existing null-values test (it had asserted the empty-array result, encoding the bug) and added `NullVectorRoundTripsAsNull` (write a null vector, read it back, assert BSON null in storage and `null` on read; failed before). → `FixDynamicNullVectorOnMain`.

### 17. `MongoConstants.Supported*Types` sets are dead and misleading — ✅ Real (cleanup)
**Finding:** `SupportedKeyTypes`, `SupportedDataTypes`, and `SupportedVectorTypes` `HashSet<Type>` constants were never referenced and were stale — e.g. `SupportedKeyTypes` listed only `string`, implying string-only keys, while the live validation (`MongoCollection.s_validKeyTypes` / `MongoModelBuilder.ValidateKeyProperty`) also accepts `Guid`, `ObjectId`, `int`, `long`. The data/vector sets were likewise narrower than `MongoModelBuilder.IsDataPropertyTypeValid` / `IsVectorPropertyTypeValidCore`.
**Fix:** remove all three (and the now-unused `System` / `System.Collections.Generic` usings) rather than "aligning" them — aligning would create a second source of truth prone to drift; `MongoModelBuilder` is authoritative. Distinct from the live `MongoModelBuilder.SupportedVectorTypes` (a `const string` for error messages), which is unchanged. Verified by the build (nothing referenced the removed members) + green suite. → `RemoveDeadSupportedTypesConstantsOnMain`.

### 18. `GetVectorIndexFields` ignores `IndexKind`; `Dimensions` "unvalidated" — ⚠️ Mixed (one false positive, one by-design)
**Dimensions — false positive.** `VectorPropertyModel.Dimensions` is a non-nullable `int`, and MEVD's `CollectionModelBuilder.Validate` throws `InvalidOperationException("…must have a positive number of dimensions.")` when `Dimensions <= 0` at model build (collection construction), before `GetVectorIndexFields` runs. So it can't be null or zero here; there's no opaque server error path. No change.
**IndexKind — real but by design.** `VectorPropertyModel.IndexKind` is never emitted. Atlas Vector Search indexes are always HNSW-based with no per-field index-kind option, so there's nothing to emit, and the conformance `IndexKindTests` (which exercise `Flat`) require the provider to *accept* any index kind — validating/rejecting would break conformance. (`MongoConstants.DefaultIndexKind` is also never applied — another dead constant, akin to #17.)
**Fix:** add a comment to `GetVectorIndexFields` documenting both — that `IndexKind` is intentionally accepted-but-not-emitted (HNSW only) and that `Dimensions` is validated upstream — so the omission is explicit rather than silent. No behavior change. → `DocumentVectorIndexKindOnMain`.
