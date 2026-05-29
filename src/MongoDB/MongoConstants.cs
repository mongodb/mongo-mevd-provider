// Copyright (c) Microsoft. All rights reserved.

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
    internal const string DefaultDistanceFunction = DistanceFunction.CosineDistance;

    /// <summary>Reserved key property name in MongoDB.</summary>
    internal const string MongoReservedKeyPropertyName = "_id";

    /// <summary>Reserved key property name in data model.</summary>
    internal const string DataModelReservedKeyPropertyName = "Id";
}
