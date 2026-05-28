// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.VectorData;
using MongoDB.VectorData;
using MongoDB.Driver;
using Moq;
using Xunit;

namespace MongoDB.VectorData.UnitTests;

/// <summary>
/// Unit tests for <see cref="MongoVectorStore"/> class.
/// </summary>
public sealed class MongoVectorStoreTests
{
    private readonly Mock<IMongoDatabase> _mockMongoDatabase = new();

    [Fact]
    public void GetCollectionWithNotSupportedKeyThrowsException()
    {
        // Arrange
        using var sut = new MongoVectorStore(this._mockMongoDatabase.Object);

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => sut.GetCollection<byte[], MongoHotelModel>("collection"));
    }

    [Fact]
    public void GetCollectionWithoutFactoryReturnsDefaultCollection()
    {
        // Arrange
        using var sut = new MongoVectorStore(this._mockMongoDatabase.Object);

        // Act
        var collection = sut.GetCollection<string, MongoHotelModel>("collection");

        // Assert
        Assert.NotNull(collection);
    }

    [Fact]
    public async Task ListCollectionNamesReturnsCollectionNamesAsync()
    {
        // Arrange
        var expectedCollectionNames = new List<string> { "collection-1", "collection-2", "collection-3" };

        var mockCursor = new Mock<IAsyncCursor<string>>();
        mockCursor
            .SetupSequence(l => l.MoveNextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .ReturnsAsync(false);

        mockCursor
            .Setup(l => l.Current)
            .Returns(expectedCollectionNames);

        this._mockMongoDatabase
            .Setup(l => l.ListCollectionNamesAsync(It.IsAny<ListCollectionNamesOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockCursor.Object);

        using var sut = new MongoVectorStore(this._mockMongoDatabase.Object);

        // Act
        var actualCollectionNames = await sut.ListCollectionNamesAsync().ToListAsync();

        // Assert
        Assert.Equal(expectedCollectionNames, actualCollectionNames);
    }

    [Fact]
    public async Task ListCollectionNamesAsyncTranslatesMongoExceptionDuringIteration()
    {
        // Arrange: cursor creation succeeds, but iteration (MoveNextAsync) throws a MongoException —
        // simulating e.g. a connection drop mid-paging.
        var mockCursor = new Mock<IAsyncCursor<string>>();
        mockCursor
            .Setup(l => l.MoveNextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MongoException("simulated connection drop"));

        this._mockMongoDatabase
            .Setup(l => l.ListCollectionNamesAsync(It.IsAny<ListCollectionNamesOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockCursor.Object);

        using var sut = new MongoVectorStore(this._mockMongoDatabase.Object);

        // Act & Assert: every other read path on the provider translates MongoException to
        // VectorStoreException with the operation-name / store-name metadata; enumeration of
        // ListCollectionNamesAsync must honor the same contract.
        var ex = await Assert.ThrowsAsync<VectorStoreException>(async () =>
        {
            await foreach (var _ in sut.ListCollectionNamesAsync())
            {
            }
        });

        Assert.Equal("ListCollectionNames", ex.OperationName);
        Assert.Equal(MongoConstants.VectorStoreSystemName, ex.VectorStoreSystemName);
        Assert.IsType<MongoException>(ex.InnerException);
    }
}
