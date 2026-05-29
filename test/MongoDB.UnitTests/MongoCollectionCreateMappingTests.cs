// Copyright (c) Microsoft. All rights reserved.

using System;
using Microsoft.Extensions.VectorData;
using MongoDB.VectorData;
using Xunit;

namespace MongoDB.VectorData.UnitTests;

/// <summary>
/// Unit tests for <see cref="MongoCollectionCreateMapping"/>.
/// </summary>
public sealed class MongoCollectionCreateMappingTests
{
    [Fact]
    public void DefaultDistanceFunctionMapsToSupportedAtlasSimilarity()
    {
        // MongoConstants.DefaultDistanceFunction must be a value the distance-function switch supports;
        // otherwise routing it through GetVectorIndexFields throws NotSupportedException instead of
        // producing the "cosine" similarity the default is meant to represent.
        var definition = new VectorStoreCollectionDefinition
        {
            Properties =
            [
                new VectorStoreKeyProperty("Id", typeof(string)),
                new VectorStoreVectorProperty("Embedding", typeof(ReadOnlyMemory<float>), 4)
                {
                    DistanceFunction = MongoConstants.DefaultDistanceFunction
                }
            ]
        };

        var model = new MongoModelBuilder().Build(typeof(DistanceProbeModel), typeof(string), definition, defaultEmbeddingGenerator: null);

        var fields = MongoCollectionCreateMapping.GetVectorIndexFields(model.VectorProperties);

        Assert.Equal("cosine", fields[0].AsBsonDocument["similarity"].AsString);
    }

    private sealed class DistanceProbeModel
    {
        [VectorStoreKey]
        public string? Id { get; set; }

        [VectorStoreVector(4)]
        public ReadOnlyMemory<float> Embedding { get; set; }
    }
}
