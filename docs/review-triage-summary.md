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

> Minor wording nit (not addressed): `UnreferencedCodeMessage`'s tail says "…compatible with NativeAOT" where "trimming" would read better. Pre-existing; out of scope for the swap fix.
