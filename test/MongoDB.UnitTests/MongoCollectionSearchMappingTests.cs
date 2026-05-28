// Copyright (c) Microsoft. All rights reserved.

using System.Linq;
using MongoDB.Bson;
using MongoDB.VectorData;
using Xunit;

namespace MongoDB.VectorData.UnitTests;

/// <summary>
/// Unit tests for <see cref="MongoCollectionSearchMapping"/> pipeline construction.
/// </summary>
public sealed class MongoCollectionSearchMappingTests
{
    [Fact]
    public void GetHybridSearchPipelineWeightsFullTextSearchHigherThanVectorSearch()
    {
        // MEVD's IKeywordHybridSearchable treats hybrid search as keyword-primary with vector
        // re-ranking. The upstream HybridSearchTests<TKey> build all test records with an
        // identical vector and assert that the keyword match wins (e.g. HybridSearchAsync_with_top,
        // HybridSearchAsync_with_Skip) — that only holds if the FTS branch's per-rank weight
        // dominates the vector branch's. The weights here pin that contract.
        var pipeline = MongoCollectionSearchMapping.GetHybridSearchPipeline(
            new[] { 0.1f, 0.2f, 0.3f },
            keywords: new[] { "alpha", "beta" },
            collectionName: "hotels",
            vectorIndexName: "vector_index",
            fullTextSearchIndexName: "full_text_search_index",
            vectorPropertyName: "embedding",
            textPropertyName: "description",
            scorePropertyName: "score",
            documentPropertyName: "document",
            limit: 10,
            numCandidates: 100,
            filter: null);

        // The vector-search branch adds the rank-weighted vs_score directly on the outer pipeline,
        // before the $unionWith with the FTS branch.
        var vsWeight = ExtractAddScoreWeight(pipeline, "vs_score");
        var ftsBranch = pipeline.Single(d => d.Contains("$unionWith"))["$unionWith"]["pipeline"].AsBsonArray.Cast<BsonDocument>();
        var ftsWeight = ExtractAddScoreWeight(ftsBranch, "fts_score");

        Assert.Equal(0.1, vsWeight);
        Assert.Equal(0.9, ftsWeight);
    }

    private static double ExtractAddScoreWeight(System.Collections.Generic.IEnumerable<BsonDocument> stages, string scoreField)
    {
        var addFields = stages.Single(d =>
            d.Contains("$addFields") && d["$addFields"].AsBsonDocument.Contains(scoreField));

        // $addFields: { <scoreField>: { $multiply: [ <weight>, { $divide: [1.0, { $add: ["$rank", 60] }] } ] } }
        var multiply = addFields["$addFields"][scoreField]["$multiply"].AsBsonArray;
        return multiply[0].ToDouble();
    }
}
