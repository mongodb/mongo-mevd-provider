// Copyright (c) Microsoft. All rights reserved.

using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace MongoDB.VectorData.ConformanceTests.Support;

internal sealed class MongoDbAtlasContainer(ContainerConfiguration configuration) : DockerContainer(configuration)
{
    public string GetConnectionString()
        => new UriBuilder("mongodb", this.Hostname, this.GetMappedPublicPort(MongoDbAtlasBuilder.MongoDbAtlasPort))
        {
            Query = "?directConnection=true"
        }.ToString();
}
