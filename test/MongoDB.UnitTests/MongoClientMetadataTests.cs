// Copyright (c) Microsoft. All rights reserved.

using System.Reflection;
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
}
