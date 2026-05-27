// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.VectorData;
using MongoDB.VectorData;
using MongoDB.Bson;
using MongoDB.Driver;
using VectorData.ConformanceTests.Support;

namespace MongoDB.VectorData.ConformanceTests.Support;

#pragma warning disable CA1001 // Type owns disposable fields but is not disposable

internal sealed class MongoTestStore : TestStore
{
    public static MongoTestStore Instance { get; } = new();

    static MongoTestStore()
    {
        // Route every MongoCollection instance through our per-collection-name tracking dict so the search-index
        // names stay consistent across construction paths (MongoTestStore, VectorStore.GetCollection, DI).
        MongoCollectionTestHook.VectorIndexNameResolver = name =>
            s_indexesByCollectionName.TryGetValue(name, out var n) ? n.VectorIndexName : null;
        MongoCollectionTestHook.FullTextSearchIndexNameResolver = name =>
            s_indexesByCollectionName.TryGetValue(name, out var n) ? n.FullTextSearchIndexName : null;
    }

    private MongoDbAtlasContainer? _container;

    public MongoClient? _client { get; private set; }
    public IMongoDatabase? _database { get; private set; }

    public MongoClient Client => this._client ?? throw new InvalidOperationException("Not initialized");
    public IMongoDatabase Database => this._database ?? throw new InvalidOperationException("Not initialized");

    private const int ConformanceNumCandidates = 1_000;

    public MongoVectorStore GetVectorStore(MongoVectorStoreOptions options)
        => new(this.Database, options);

    public override VectorStoreCollection<TKey, TRecord> CreateCollection<TKey, TRecord>(
        string name,
        VectorStoreCollectionDefinition definition)
    {
        var collection = new MongoCollection<TKey, TRecord>(
            this.Database,
            name,
            new()
            {
                Definition = definition,
                NumCandidates = ConformanceNumCandidates
            });
        collection.DeferSearchIndexCreation = true;
        return collection;
    }

    public override VectorStoreCollection<object, Dictionary<string, object?>> CreateDynamicCollection(
        string name,
        VectorStoreCollectionDefinition definition)
    {
        var collection = new MongoDynamicCollection(
            this.Database,
            name,
            new()
            {
                Definition = definition,
                NumCandidates = ConformanceNumCandidates
            });
        collection.DeferSearchIndexCreation = true;
        return collection;
    }

    public override async Task WaitForDataAsync<TKey, TRecord>(
        VectorStoreCollection<TKey, TRecord> collection,
        int recordCount,
        Expression<Func<TRecord, bool>>? filter,
        Expression<Func<TRecord, object?>>? vectorProperty,
        int? vectorSize,
        object? dummyVector)
        where TRecord : class
    {
        // Rebuild the search indexes now: the test has finished mutating the collection, so the build runs
        // over the current data set in one pass instead of relying on Atlas Local's incremental indexing,
        // which is unreliable for both inserts and deletes. Fresh unique names per rebuild avoid the known
        // same-name drop/recreate flakiness in MongoDB Atlas Search.
        if (collection is MongoCollection<TKey, TRecord> mongoCollection)
        {
            await RebuildSearchIndexesAsync(mongoCollection).ConfigureAwait(false);
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);

        while (true)
        {
            try
            {
                await this.WaitForSearchIndexesAsync(collection, vectorProperty is not null || vectorSize is not null || dummyVector is not null).ConfigureAwait(false);
                await base.WaitForDataAsync(collection, recordCount, filter, vectorProperty, vectorSize, dummyVector).ConfigureAwait(false);
                await this.WaitForFullTextSearchDataAsync(collection, recordCount).ConfigureAwait(false);
                return;
            }
            catch (InvalidOperationException ex) when (ex.Message == "Data did not appear in the collection within the expected time." && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
    }

    // Tracks the most recent vector/full-text search-index names per MongoDB collection name. The next
    // WaitForDataAsync drops these before generating fresh names, and CreateCollection / CreateDynamicCollection
    // apply them to newly-constructed instances so a typed VectorStoreCollection and its dynamic companion always
    // resolve to the same physical indexes.
    private static readonly ConcurrentDictionary<string, IndexNames> s_indexesByCollectionName = new();

    private sealed class IndexNames
    {
        public string? VectorIndexName { get; set; }
        public string? FullTextSearchIndexName { get; set; }
    }

    private static async Task RebuildSearchIndexesAsync<TKey, TRecord>(MongoCollection<TKey, TRecord> mongoCollection)
        where TKey : notnull
        where TRecord : class
    {
        var underlyingCollection =
            mongoCollection.GetService(typeof(IMongoCollection<BsonDocument>)) as IMongoCollection<BsonDocument>
            ?? throw new InvalidOperationException("MongoDB conformance tests require a MongoDB-backed collection.");

        var tracked = s_indexesByCollectionName.GetOrAdd(mongoCollection.Name, _ => new IndexNames());

        // Drop the indexes the previous WaitForDataAsync built for this collection (best-effort).
        if (tracked.VectorIndexName is not null)
        {
            await DropSearchIndexQuietlyAsync(underlyingCollection, tracked.VectorIndexName).ConfigureAwait(false);
        }
        if (tracked.FullTextSearchIndexName is not null && tracked.FullTextSearchIndexName != tracked.VectorIndexName)
        {
            await DropSearchIndexQuietlyAsync(underlyingCollection, tracked.FullTextSearchIndexName).ConfigureAwait(false);
        }

        var suffix = Guid.NewGuid().ToString("N");
        tracked.VectorIndexName = $"vector_idx_{suffix}";
        tracked.FullTextSearchIndexName = $"fts_idx_{suffix}";

        await mongoCollection.CreateSearchIndexesAsync().ConfigureAwait(false);
    }

    private static async Task DropSearchIndexQuietlyAsync(IMongoCollection<BsonDocument> collection, string indexName)
    {
        try
        {
            await collection.SearchIndexes.DropOneAsync(indexName).ConfigureAwait(false);
        }
        catch (MongoCommandException)
        {
            // The index may not exist (e.g. collection was dropped between WaitForDataAsync calls); proceed.
        }
    }

    private async Task WaitForSearchIndexesAsync<TKey, TRecord>(
        VectorStoreCollection<TKey, TRecord> collection,
        bool hasVectorIndex)
        where TKey : notnull
        where TRecord : class
    {
        var indexNames = GetExpectedSearchIndexNames(collection, hasVectorIndex);
        if (indexNames.Count == 0)
        {
            return;
        }

        var mongoCollection =
            collection.GetService(typeof(IMongoCollection<BsonDocument>)) as IMongoCollection<BsonDocument>
            ?? throw new InvalidOperationException("MongoDB conformance tests require a MongoDB-backed collection.");

        Exception? lastException = null;
        // Atlas Search index builds can take several minutes; keep the wait well above the conservative one.
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                PipelineDefinition<BsonDocument, BsonDocument> pipeline = new[]
                {
                    new BsonDocument("$listSearchIndexes", new BsonDocument())
                };

                using var cursor = await mongoCollection.AggregateAsync(pipeline).ConfigureAwait(false);
                var searchIndexes = await cursor.ToListAsync().ConfigureAwait(false);

                if (indexNames.All(indexName => searchIndexes.Any(index =>
                    index.GetValue("name", null)?.AsString == indexName &&
                    index.GetValue("queryable", false).ToBoolean())))
                {
                    return;
                }
            }
            catch (MongoCommandException ex)
            {
                lastException = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out waiting for MongoDB search indexes to become queryable for conformance tests.", lastException);
    }

    private async Task WaitForFullTextSearchDataAsync<TKey, TRecord>(
        VectorStoreCollection<TKey, TRecord> collection,
        int recordCount)
        where TKey : notnull
        where TRecord : class
    {
        var fullTextStorageNames = GetFullTextStorageNames(collection);
        if (fullTextStorageNames.Count == 0)
        {
            return;
        }

        var mongoCollection =
            collection.GetService(typeof(IMongoCollection<BsonDocument>)) as IMongoCollection<BsonDocument>
            ?? throw new InvalidOperationException("MongoDB conformance tests require a MongoDB-backed collection.");

        var fullTextIndexName = GetFullTextSearchIndexName(collection);
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            if (await FullTextSearchDataIsVisibleAsync(mongoCollection, fullTextIndexName, fullTextStorageNames, recordCount).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out waiting for MongoDB full text search data to become visible for conformance tests.");
    }

    private static async Task<bool> FullTextSearchDataIsVisibleAsync(
        IMongoCollection<BsonDocument> mongoCollection,
        string fullTextIndexName,
        IReadOnlyList<string> fullTextStorageNames,
        int recordCount)
    {
        foreach (var fullTextStorageName in fullTextStorageNames)
        {
            PipelineDefinition<BsonDocument, BsonDocument> pipeline = new BsonDocument[]
            {
                new("$search", new BsonDocument
                {
                    { "index", fullTextIndexName },
                    { "exists", new BsonDocument("path", fullTextStorageName) }
                }),
                new("$limit", recordCount == 0 ? 1 : recordCount),
                new("$count", "count")
            };

            using var cursor = await mongoCollection.AggregateAsync(pipeline).ConfigureAwait(false);
            var countDocument = await cursor.FirstOrDefaultAsync().ConfigureAwait(false);
            var count = countDocument?.GetValue("count", 0).ToInt32() ?? 0;

            if (count != recordCount)
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> GetExpectedSearchIndexNames<TKey, TRecord>(
        VectorStoreCollection<TKey, TRecord> collection,
        bool hasVectorIndex)
        where TKey : notnull
        where TRecord : class
    {
        var indexNames = new List<string>();

        if (hasVectorIndex)
        {
            indexNames.Add(GetVectorIndexName(collection));
        }

        if (GetFullTextStorageNames(collection).Count > 0)
        {
            indexNames.Add(GetFullTextSearchIndexName(collection));
        }

        return indexNames;
    }

    private static string GetVectorIndexName<TKey, TRecord>(VectorStoreCollection<TKey, TRecord> collection)
        where TKey : notnull
        where TRecord : class
        => s_indexesByCollectionName.TryGetValue(collection.Name, out var n) && n.VectorIndexName is { } name
            ? name
            : throw new InvalidOperationException($"No tracked vector index name for collection '{collection.Name}'; ensure WaitForDataAsync ran before querying.");

    private static string GetFullTextSearchIndexName<TKey, TRecord>(VectorStoreCollection<TKey, TRecord> collection)
        where TKey : notnull
        where TRecord : class
        => s_indexesByCollectionName.TryGetValue(collection.Name, out var n) && n.FullTextSearchIndexName is { } name
            ? name
            : throw new InvalidOperationException($"No tracked full-text search index name for collection '{collection.Name}'; ensure WaitForDataAsync ran before querying.");

    private static List<string> GetFullTextStorageNames<TKey, TRecord>(VectorStoreCollection<TKey, TRecord> collection)
        where TKey : notnull
        where TRecord : class
    {
        var modelField = GetField(collection.GetType(), "_model")
            ?? throw new InvalidOperationException("MongoDB conformance tests require a MongoDB collection model.");
        var model = modelField.GetValue(collection)
            ?? throw new InvalidOperationException("MongoDB conformance tests require an initialized MongoDB collection model.");
        var dataProperties = model.GetType().GetProperty("DataProperties")?.GetValue(model) as System.Collections.IEnumerable
            ?? throw new InvalidOperationException("MongoDB conformance tests require MongoDB collection data properties.");

        var storageNames = new List<string>();
        foreach (var dataProperty in dataProperties)
        {
            var dataPropertyType = dataProperty.GetType();
            if (dataPropertyType.GetProperty("IsFullTextIndexed")?.GetValue(dataProperty) is true
                && dataPropertyType.GetProperty("StorageName")?.GetValue(dataProperty) is string storageName)
            {
                storageNames.Add(storageName);
            }
        }

        return storageNames;
    }

    private static FieldInfo? GetField(Type type, string name)
    {
        for (var currentType = type; currentType is not null; currentType = currentType.BaseType)
        {
            var field = currentType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is not null)
            {
                return field;
            }
        }

        return null;
    }

    private MongoTestStore()
    {
    }

    protected override async Task StartAsync()
    {
        // Keep Atlas Local alive across conformance fixtures; restarting it causes search-index and replica-set churn.
        if (this._client is null || this._database is null)
        {
            var clientSettings = MongoTestEnvironment.IsConnectionInfoDefined
                ? MongoClientSettings.FromConnectionString(MongoTestEnvironment.ConnectionUrl)
                : await this.StartMongoDbContainerAsync().ConfigureAwait(false);

            this._client = new MongoClient(clientSettings);
            this._database = this._client.GetDatabase("VectorSearchTests");
        }

        // The base TestStore disposes DefaultVectorStore between reference-counted fixture lifetimes.
        this.DefaultVectorStore = new MongoVectorStore(this._database);
    }

    private async Task<MongoClientSettings> StartMongoDbContainerAsync()
    {
        // Dispose any container left over from a prior failed start before allocating a new one.
        if (this._container is not null)
        {
            await this._container.DisposeAsync().ConfigureAwait(false);
            this._container = null;
        }

        var container = new MongoDbAtlasBuilder().Build();
        try
        {
            using CancellationTokenSource cts = new();
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            await container.StartAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            await container.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        this._container = container;
        return MongoClientSettings.FromConnectionString(container.GetConnectionString());
    }

    private static readonly string? s_baseObjectId = ObjectId.GenerateNewId().ToString().Substring(0, 14);

    public override TKey GenerateKey<TKey>(int value)
    {
        if (typeof(TKey) == typeof(ObjectId))
        {
            return (TKey)(object)ObjectId.Parse(s_baseObjectId + value.ToString("0000000000"));
        }

        return base.GenerateKey<TKey>(value);
    }

    protected override async Task StopAsync()
    {
        // Do not stop the shared Atlas Local container between fixtures; Testcontainers cleans it up when the test process exits.
        await Task.CompletedTask;
    }
}
