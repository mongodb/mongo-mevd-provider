// Copyright (c) Microsoft. All rights reserved.

using VectorData.ConformanceTests.Support;

namespace MongoDB.VectorData.ConformanceTests.Support;

public class MongoFixture : VectorStoreFixture
{
    public override TestStore TestStore => MongoTestStore.Instance;
}
