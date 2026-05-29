// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using Microsoft.Extensions.VectorData;
using Microsoft.Extensions.VectorData.ProviderServices;
using MongoDB.Bson;

namespace MongoDB.VectorData;

/// <summary>
/// Contains mapping helpers to use when creating a collection in MongoDB.
/// </summary>
internal static class MongoCollectionCreateMapping
{
    /// <summary>
    /// Returns an array of indexes to create for vector properties.
    /// </summary>
    /// <param name="vectorProperties">Collection of vector properties for index creation.</param>
    public static BsonArray GetVectorIndexFields(IReadOnlyList<VectorPropertyModel> vectorProperties)
    {
        var indexArray = new BsonArray();

        // Create separate index for each vector property
        foreach (var property in vectorProperties)
        {
            // VectorPropertyModel.IndexKind is intentionally not emitted: Atlas Vector Search indexes are always
            // HNSW-based and expose no per-field index-kind option, so any IndexKind a caller specifies is accepted
            // and served by HNSW (the conformance IndexKindTests rely on this acceptance). Dimensions is not
            // re-validated here either: the MEVD model builder already rejects a non-positive Dimensions when the
            // collection is constructed, before this index definition is built.
            var indexDocument = new BsonDocument
            {
                { "type", "vector" },
                { "numDimensions", property.Dimensions },
                { "path", property.StorageName },
                { "similarity", GetDistanceFunction(property.DistanceFunction, property.ModelName) },
            };

            indexArray.Add(indexDocument);
        }

        return indexArray;
    }

    /// <summary>
    /// Returns an array of indexes to create for filterable data properties.
    /// </summary>
    /// <param name="dataProperties">Collection of data properties for index creation.</param>
    public static BsonArray GetFilterableDataIndexFields(IReadOnlyList<DataPropertyModel> dataProperties)
    {
        var indexArray = new BsonArray();

        // Create separate index for each data property
        foreach (var property in dataProperties)
        {
            if (property.IsIndexed)
            {
                var indexDocument = new BsonDocument
                {
                    { "type", "filter" },
                    { "path", property.StorageName },
                };

                indexArray.Add(indexDocument);
            }
        }

        return indexArray;
    }

    /// <summary>
    /// Returns a list of of fields to index for full text search data properties.
    /// </summary>
    /// <param name="dataProperties">Collection of data properties for index creation.</param>
    public static List<BsonElement> GetFullTextSearchableDataIndexFields(IReadOnlyList<DataPropertyModel> dataProperties)
    {
        var fieldElements = new List<BsonElement>();

        // Create separate index for each data property
        foreach (var property in dataProperties)
        {
            if (property.IsFullTextIndexed)
            {
                fieldElements.Add(new BsonElement(property.StorageName, new BsonArray()
                {
                    new BsonDocument() { { "type", "string" } }
                }));
            }
        }

        return fieldElements;
    }

    /// <summary>
    /// More information about MongoDB distance functions here: <see href="https://www.mongodb.com/docs/atlas/atlas-vector-search/vector-search-type/#atlas-vector-search-index-fields" />.
    /// </summary>
    private static string GetDistanceFunction(string? distanceFunction, string vectorPropertyName)
        => distanceFunction switch
        {
            DistanceFunction.CosineSimilarity or null => "cosine",
            DistanceFunction.DotProductSimilarity => "dotProduct",
            DistanceFunction.EuclideanDistance => "euclidean",

            _ => throw new NotSupportedException($"Distance function '{distanceFunction}' for {nameof(VectorStoreVectorProperty)} '{vectorPropertyName}' is not supported by the MongoDB VectorStore.")
        };
}
