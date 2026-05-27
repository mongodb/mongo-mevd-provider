// Copyright (c) Microsoft. All rights reserved.

using MongoDB.VectorData.ConformanceTests.Support;
using VectorData.ConformanceTests.ModelTests;
using VectorData.ConformanceTests.Support;
using Xunit;

namespace MongoDB.VectorData.ConformanceTests.ModelTests;

public class MongoBasicModelTests(MongoBasicModelTests.Fixture fixture)
    : BasicModelTests<string>(fixture), IClassFixture<MongoBasicModelTests.Fixture>
{
    public new class Fixture : BasicModelTests<string>.Fixture
    {
        public override TestStore TestStore => MongoTestStore.Instance;
    }
}
