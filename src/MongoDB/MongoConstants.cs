// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.VectorData;

namespace MongoDB.VectorData;

/// <summary>
/// Constants for MongoDB vector store implementation.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class MongoConstants
{
    internal const string VectorStoreSystemName = "mongodb";

    /// <summary>Default ratio of number of nearest neighbors to number of documents to return.</summary>
    internal const int DefaultNumCandidatesRatio = 10;

    /// <summary>Default vector index name.</summary>
    internal const string DefaultVectorIndexName = "vector_index";

    /// <summary>Default full text search index name.</summary>
    internal const string DefaultFullTextSearchIndexName = "full_text_search_index";

    /// <summary>Default index kind for vector search.</summary>
    internal const string DefaultIndexKind = IndexKind.IvfFlat;

    /// <summary>Default distance function for vector search.</summary>
    /// <remarks>
    /// Must be one of the values handled by <see cref="MongoCollectionCreateMapping"/>'s distance-function
    /// mapping (<see cref="DistanceFunction.CosineSimilarity"/>, <see cref="DistanceFunction.DotProductSimilarity"/>,
    /// <see cref="DistanceFunction.EuclideanDistance"/>); the unspecified default maps to Atlas "cosine".
    /// </remarks>
    internal const string DefaultDistanceFunction = DistanceFunction.CosineSimilarity;

    /// <summary>Reserved key property name in MongoDB.</summary>
    internal const string MongoReservedKeyPropertyName = "_id";

    /// <summary>Reserved key property name in data model.</summary>
    internal const string DataModelReservedKeyPropertyName = "Id";

    /// <summary>A <see cref="HashSet{Type}"/> containing the supported key types.</summary>
    internal static readonly HashSet<Type> SupportedKeyTypes =
    [
        typeof(string)
    ];

    /// <summary>A <see cref="HashSet{Type}"/> containing the supported data property types.</summary>
    internal static readonly HashSet<Type> SupportedDataTypes =
    [
        typeof(bool),
        typeof(string),
        typeof(int),
        typeof(long),
        typeof(float),
        typeof(double),
        typeof(decimal),
        typeof(DateTime),
    ];

    /// <summary>A <see cref="HashSet{Type}"/> containing the supported vector types.</summary>
    internal static readonly HashSet<Type> SupportedVectorTypes =
    [
        typeof(ReadOnlyMemory<float>),
        typeof(ReadOnlyMemory<float>?)
    ];
}
