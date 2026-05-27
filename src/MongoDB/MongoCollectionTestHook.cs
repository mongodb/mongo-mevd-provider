// Copyright (c) Microsoft. All rights reserved.

namespace MongoDB.VectorData;

/// <summary>
/// Test-only hook that lets conformance tests override the vector and full-text search-index names every
/// <see cref="MongoCollection{TKey, TRecord}"/> instance sees at query and index-creation time, indexed by the
/// MongoDB collection name. Used by the conformance test infrastructure to coordinate index names across all
/// collection instances that share a logical name (typed + dynamic, fixture-built + DI-resolved, etc.). Production
/// code should never read or write these.
/// </summary>
internal static class MongoCollectionTestHook
{
    internal static Func<string, string?>? VectorIndexNameResolver { get; set; }

    internal static Func<string, string?>? FullTextSearchIndexNameResolver { get; set; }
}
