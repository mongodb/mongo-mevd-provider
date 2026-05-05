// Copyright (c) Microsoft. All rights reserved.

using MongoDB.VectorData.ConformanceTests.Support;
using VectorData.ConformanceTests;
using Xunit;

namespace MongoDB.VectorData.ConformanceTests;

public class MongoCollectionManagementTests(MongoFixture fixture)
    : CollectionManagementTests<string>(fixture), IClassFixture<MongoFixture>
{
}
