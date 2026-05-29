// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.VectorData;
using MongoDB.VectorData;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;
using Xunit;

namespace MongoDB.VectorData.UnitTests;

/// <summary>
/// Unit tests for <see cref="MongoMapper{TRecord}"/> class.
/// </summary>
public sealed class MongoMapperTests
{
    private readonly MongoMapper<MongoHotelModel> _sut;

    public MongoMapperTests()
    {
        var keyProperty = new VectorStoreKeyProperty("HotelId", typeof(string));

        var definition = new VectorStoreCollectionDefinition
        {
            Properties =
            [
                keyProperty,
                new VectorStoreDataProperty("HotelName", typeof(string)),
                new VectorStoreDataProperty("Tags", typeof(List<string>)),
                new VectorStoreDataProperty("ParkingIncluded", typeof(bool)),
                new VectorStoreVectorProperty("DescriptionEmbedding", typeof(ReadOnlyMemory<float>?), 10)
            ]
        };

        this._sut = new(new MongoModelBuilder().Build(typeof(MongoHotelModel), typeof(string), definition, defaultEmbeddingGenerator: null));
    }

    [Fact]
    public void MapFromDataToStorageModelReturnsValidObject()
    {
        // Arrange
        var hotel = new MongoHotelModel("key")
        {
            HotelName = "Test Name",
            Tags = ["tag1", "tag2"],
            ParkingIncluded = true,
            DescriptionEmbedding = new ReadOnlyMemory<float>([1f, 2f, 3f])
        };

        // Act
        var document = this._sut.MapFromDataToStorageModel(hotel, recordIndex: 0, generatedEmbeddings: null);

        // Assert
        Assert.NotNull(document);

        Assert.Equal("key", document["_id"]);
        Assert.Equal("Test Name", document["HotelName"]);
        Assert.Equal(["tag1", "tag2"], document["Tags"].AsBsonArray);
        Assert.True(document["parking_is_included"].AsBoolean);
        Assert.Equal([1f, 2f, 3f], document["DescriptionEmbedding"].AsBsonArray);
    }

    [Fact]
    public void MapFromStorageToDataModelReturnsValidObject()
    {
        // Arrange
        var document = new BsonDocument
        {
            ["_id"] = "key",
            ["HotelName"] = "Test Name",
            ["Tags"] = BsonArray.Create(new List<string> { "tag1", "tag2" }),
            ["parking_is_included"] = BsonValue.Create(true),
            ["DescriptionEmbedding"] = BsonArray.Create(new List<float> { 1f, 2f, 3f })
        };

        // Act
        var hotel = this._sut.MapFromStorageToDataModel(document, includeVectors: true);

        // Assert
        Assert.NotNull(hotel);

        Assert.Equal("key", hotel.HotelId);
        Assert.Equal("Test Name", hotel.HotelName);
        Assert.Equal(["tag1", "tag2"], hotel.Tags);
        Assert.True(hotel.ParkingIncluded);
        Assert.True(new ReadOnlyMemory<float>([1f, 2f, 3f]).Span.SequenceEqual(hotel.DescriptionEmbedding!.Value.Span));
    }

    [Fact]
    public void ConstructingMapperRegistersConventionsOnlyOncePerRecordType()
    {
        // ConventionRegistry is a process-wide global, and the driver's Register appends a pack every time
        // (it never deduplicates by name). Registering from the instance constructor therefore leaks a new
        // entry on each MongoMapper<TRecord> creation (and MongoVectorStore creates collections per call).
        // Registration must happen once per record type.
        var model = new MongoModelBuilder().Build(typeof(ConventionProbeModel), typeof(string), definition: null, defaultEmbeddingGenerator: null);

        _ = new MongoMapper<ConventionProbeModel>(model);
        var registrationsAfterFirst = CountRegisteredIgnoreExtraElementsConventions(typeof(ConventionProbeModel));

        for (var i = 0; i < 5; i++)
        {
            _ = new MongoMapper<ConventionProbeModel>(model);
        }
        var registrationsAfterMany = CountRegisteredIgnoreExtraElementsConventions(typeof(ConventionProbeModel));

        Assert.Equal(registrationsAfterFirst, registrationsAfterMany);
    }

    private static int CountRegisteredIgnoreExtraElementsConventions(Type recordType)
        => ConventionRegistry.Lookup(recordType).Conventions.OfType<IgnoreExtraElementsConvention>().Count();

    private sealed class ConventionProbeModel
    {
        [VectorStoreKey]
        public string? Id { get; set; }

        [VectorStoreData]
        public string? Name { get; set; }
    }
}
