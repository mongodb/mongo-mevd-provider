// Copyright (c) Microsoft. All rights reserved.

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.VectorData;
using MongoDB.VectorData;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using VectorData.ConformanceTests.Support;

namespace MongoDB.VectorData.ConformanceTests.Support;

// Force MongoTestStore's static constructor to run as soon as the conformance test assembly loads. The cctor
// installs MongoCollectionTestHook resolvers; without this initializer those resolvers would be null until the
// first member of MongoTestStore is accessed, leaving any MongoCollection constructed earlier (e.g. via DI
// registrations resolving MongoCollection<TKey, TRecord> directly) silently bypassing the test hook.
internal static class MongoTestStoreInitializer
{
    [ModuleInitializer]
    internal static void Init() => RuntimeHelpers.RunClassConstructor(typeof(MongoTestStore).TypeHandle);
}

#pragma warning disable CA1001 // Type owns disposable fields but is not disposable

internal sealed class MongoTestStore : TestStore
{
    public static MongoTestStore Instance { get; } = new();

    private MongoDbContainer? _container;

    private MongoClient? _client { get; set; }
    private IMongoDatabase? _database { get; set; }

    public MongoClient Client => this._client ?? throw new InvalidOperationException("Not initialized");
    public IMongoDatabase Database => this._database ?? throw new InvalidOperationException("Not initialized");

    private const string DefaultVectorIndexName = "vector_index";
    private const string DefaultFullTextSearchIndexName = "full_text_search_index";
    private const int ConformanceNumCandidates = 1_000;

    public MongoVectorStore GetVectorStore(MongoVectorStoreOptions options)
        => new(this.Database, options);

    public override VectorStoreCollection<TKey, TRecord> CreateCollection<TKey, TRecord>(
        string name,
        VectorStoreCollectionDefinition definition)
        => new MongoCollection<TKey, TRecord>(
            this.Database,
            name,
            new()
            {
                Definition = definition,
                NumCandidates = ConformanceNumCandidates
            });

    public override VectorStoreCollection<object, Dictionary<string, object?>> CreateDynamicCollection(
        string name,
        VectorStoreCollectionDefinition definition)
        => new MongoDynamicCollection(
            this.Database,
            name,
            new()
            {
                Definition = definition,
                NumCandidates = ConformanceNumCandidates
            });

    public override async Task WaitForDataAsync<TKey, TRecord>(
        VectorStoreCollection<TKey, TRecord> collection,
        int recordCount,
        Expression<Func<TRecord, bool>>? filter,
        Expression<Func<TRecord, object?>>? vectorProperty,
        int? vectorSize,
        object? dummyVector)
        where TRecord : class
    {
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
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);

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

        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            if (await FullTextSearchDataIsVisibleAsync(mongoCollection, fullTextStorageNames, recordCount).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out waiting for MongoDB full text search data to become visible for conformance tests.");
    }

    private static async Task<bool> FullTextSearchDataIsVisibleAsync(
        IMongoCollection<BsonDocument> mongoCollection,
        IReadOnlyList<string> fullTextStorageNames,
        int recordCount)
    {
        foreach (var fullTextStorageName in fullTextStorageNames)
        {
            PipelineDefinition<BsonDocument, BsonDocument> pipeline = new BsonDocument[]
            {
                new("$search", new BsonDocument
                {
                    { "index", DefaultFullTextSearchIndexName },
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
            indexNames.Add(DefaultVectorIndexName);
        }

        if (GetFullTextStorageNames(collection).Count > 0)
        {
            indexNames.Add(DefaultFullTextSearchIndexName);
        }

        return indexNames;
    }

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
            var useConfiguredMongoDb = MongoTestEnvironment.IsConnectionInfoDefined;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var clientSettings = useConfiguredMongoDb
                        ? MongoClientSettings.FromConnectionString(MongoTestEnvironment.ConnectionUrl)
                        : await this.StartMongoDbContainerAsync().ConfigureAwait(false);

                    this._client = new MongoClient(clientSettings);
                    this._database = this._client.GetDatabase("VectorSearchTests");

                    if (!useConfiguredMongoDb)
                    {
                        await this.WaitForSearchIndexManagementAsync().ConfigureAwait(false);
                    }

                    break;
                }
                catch (MongoException ex) when (!useConfiguredMongoDb && IsMongoShutdownException(ex) && attempt < 2)
                {
                    await this.ResetLocalContainerAsync().ConfigureAwait(false);
                }
            }
        }

        // The base TestStore disposes DefaultVectorStore between reference-counted fixture lifetimes.
        this.DefaultVectorStore = new MongoVectorStore(this._database);
    }

    private async Task<MongoClientSettings> StartMongoDbContainerAsync()
    {
        this._container = new MongoDbBuilder("mongodb/mongodb-atlas-local:7.0.6")
            .WithWaitStrategy(Wait.ForUnixContainer().AddCustomWaitStrategy(new MongoDbWaitUntil()))
            .Build();

        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMinutes(3));
        await this._container.StartAsync(cts.Token);

        return new MongoClientSettings
        {
            Server = new MongoServerAddress(this._container.Hostname, this._container.GetMappedPublicPort(MongoDbBuilder.MongoDbPort)),
            DirectConnection = true,
            // ReadConcern = ReadConcern.Linearizable,
            // WriteConcern = WriteConcern.WMajority
        };
    }

    private async Task ResetLocalContainerAsync()
    {
        this._client = null;
        this._database = null;

        if (this._container is not null)
        {
            await this._container.DisposeAsync().ConfigureAwait(false);
            this._container = null;
        }
    }

    private async Task WaitForSearchIndexManagementAsync()
    {
        const string ReadinessCollectionName = "__VectorSearchReadiness";
        var collection = this.Database.GetCollection<BsonDocument>(ReadinessCollectionName);

        await collection.InsertOneAsync(new BsonDocument("embedding", new BsonArray([1.0, 0.0, 0.0]))).ConfigureAwait(false);

        Exception? lastException = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await this.Database.RunCommandAsync<BsonDocument>(
                    new BsonDocument
                    {
                        { "createSearchIndexes", ReadinessCollectionName },
                        {
                            "indexes",
                            new BsonArray
                            {
                                new BsonDocument
                                {
                                    { "name", DefaultVectorIndexName },
                                    { "type", "vectorSearch" },
                                    {
                                        "definition",
                                        new BsonDocument
                                        {
                                            {
                                                "fields",
                                                new BsonArray
                                                {
                                                    new BsonDocument
                                                    {
                                                        { "type", "vector" },
                                                        { "path", "embedding" },
                                                        { "numDimensions", 3 },
                                                        { "similarity", "cosine" }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }).ConfigureAwait(false);

                // Leave the readiness collection in place; dropping it immediately after creating a search index can
                // make Atlas Local churn search-index cleanup while the first conformance tests are already running.
                return;
            }
            catch (MongoCommandException ex) when (ex.Message.Contains("Search Index Management service", StringComparison.Ordinal))
            {
                lastException = ex;
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (MongoException ex) when (IsMongoShutdownException(ex))
            {
                throw;
            }
        }

        throw new TimeoutException("Timed out waiting for MongoDB Atlas Local Search Index Management service.", lastException);
    }

    private static bool IsMongoShutdownException(MongoException ex)
        => ex is MongoNodeIsRecoveringException
            || ex is MongoWriteConcernException { CodeName: "InterruptedAtShutdown" }
            || ex is MongoCommandException { CodeName: "InterruptedAtShutdown" }
            || ex.Message.Contains("Replication is being shut down", StringComparison.Ordinal)
            || ex.Message.Contains("DefaultConfigManager is closed", StringComparison.Ordinal)
            || ex.Message.Contains("node is recovering", StringComparison.Ordinal);

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

    private sealed class MongoDbWaitUntil : IWaitUntil
    {
        /// <inheritdoc />
        public async Task<bool> UntilAsync(IContainer container)
        {
            var (stdout, _) = await container.GetLogsAsync(timestampsEnabled: false)
                .ConfigureAwait(false);

            return stdout.Contains("\"msg\":\"Waiting for connections\"");
        }
    }
}
