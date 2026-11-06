// Copyright (c) Microsoft. All rights reserved.

using System.Reflection;
using MongoDB.Driver;
using Moq;
using Xunit;

namespace MongoDB.VectorData.UnitTests;

public sealed class MongoClientMetadataTests
{
    [Fact]
    public void LibraryInfoNameIsTheProviderName()
    {
        Assert.Equal("MongoDB.VectorData", MongoClientMetadata.LibraryInfo.Name);
    }

    [Fact]
    public void LibraryInfoVersionIsThePackageVersionWithoutSourceRevision()
    {
        var informationalVersion = typeof(MongoClientMetadata).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        var version = MongoClientMetadata.LibraryInfo.Version;

        Assert.False(string.IsNullOrEmpty(version));
        Assert.DoesNotContain("+", version);
        Assert.Equal(informationalVersion.Split('+')[0], version);
    }

    [Fact]
    public void AppendToIgnoresDatabasesWhoseClientIsNotAMongoClient()
    {
        var mockMongoDatabase = new Mock<IMongoDatabase>();
        mockMongoDatabase.Setup(database => database.Client).Returns(new Mock<IMongoClient>().Object);

        MongoClientMetadata.AppendTo(mockMongoDatabase.Object);
    }
}
