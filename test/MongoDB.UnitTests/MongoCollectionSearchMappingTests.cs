// Copyright (c) Microsoft. All rights reserved.

using System.Linq;
using MongoDB.VectorData;
using MongoDB.Bson;
using Xunit;

namespace MongoDB.VectorData.UnitTests;

/// <summary>
/// Unit tests for <see cref="MongoCollectionSearchMapping"/>.
/// </summary>
public sealed class MongoCollectionSearchMappingTests
{
    [Fact]
    public void HybridSearchPipelineAppliesFullTextFilterAsMatchStageNotInsideSearch()
    {
        // The full-text $search stage cannot take an MQL filter, so the hybrid pipeline applies the filter as a
        // $match immediately after $search (the vector branch, in contrast, embeds it in $vectorSearch.filter).
        // This locks that behavior so the (unused) filter parameter can be removed from GetFullTextSearchQuery
        // without changing what the pipeline does.
        var filter = new BsonDocument { ["category"] = "books" };
        float[] vector = [1f, 2f, 3f, 4f];

        var pipeline = MongoCollectionSearchMapping.GetHybridSearchPipeline(
            vector,
            ["term"],
            collectionName: "coll",
            vectorIndexName: "vector_index",
            fullTextSearchIndexName: "fts_index",
            vectorPropertyName: "embedding",
            textPropertyName: "text",
            scorePropertyName: "similarityScore",
            documentPropertyName: "document",
            limit: 10,
            numCandidates: 100,
            filter);

        // The full-text branch lives inside the $unionWith stage.
        var ftsPipeline = pipeline.Single(stage => stage.Contains("$unionWith"))["$unionWith"]["pipeline"].AsBsonArray;

        var searchStage = ftsPipeline.Single(s => s.AsBsonDocument.Contains("$search"))["$search"].AsBsonDocument;
        var matchStage = ftsPipeline.Single(s => s.AsBsonDocument.Contains("$match"))["$match"].AsBsonDocument;

        Assert.False(searchStage.Contains("filter"), "The full-text $search stage must not embed the MQL filter.");
        Assert.Equal(filter, matchStage);

        // The vector branch keeps embedding the filter in $vectorSearch.filter.
        var vectorSearchStage = pipeline.Single(stage => stage.Contains("$vectorSearch"))["$vectorSearch"].AsBsonDocument;
        Assert.Equal(filter, vectorSearchStage["filter"].AsBsonDocument);
    }
}
