# MongoDB.VectorData

MongoDB provider for [Microsoft.Extensions.VectorData](https://learn.microsoft.com/dotnet/ai/vector-stores/overview).

This provider uses [MongoDB Atlas Vector Search](https://www.mongodb.com/products/platform/atlas-vector-search) to implement vector, keyword, and hybrid search through the `Microsoft.Extensions.VectorData` abstractions.

## Quick Start

1. Create an [Atlas cluster](https://www.mongodb.com/docs/atlas/getting-started/).

2. Install the NuGet package:

```bash
dotnet add package MongoDB.VectorData
```

3. Create a `MongoVectorStore` from a MongoDB database:

```csharp
using Microsoft.Extensions.VectorData;
using MongoDB.Driver;
using MongoDB.VectorData;

var client = new MongoClient("<connection-string>");
var database = client.GetDatabase("<database-name>");

VectorStore vectorStore = new MongoVectorStore(database);
```

For more information, see the [Microsoft.Extensions.VectorData documentation](https://learn.microsoft.com/dotnet/ai/vector-stores/overview).

> Guide to find the connection string: https://www.mongodb.com/docs/manual/reference/connection-string/
