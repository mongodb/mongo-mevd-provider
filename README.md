# MongoDB.VectorData

MongoDB provider for [Microsoft.Extensions.VectorData](https://learn.microsoft.com/dotnet/ai/vector-stores/overview).

This package uses MongoDB Atlas Vector Search to store records and perform vector, keyword, and hybrid search through the `Microsoft.Extensions.VectorData` abstractions.

## Install

```bash
dotnet add package MongoDB.VectorData
```

## Quick start

```csharp
using Microsoft.Extensions.VectorData;
using MongoDB.Driver;
using MongoDB.VectorData;

var client = new MongoClient("<connection-string>");
var database = client.GetDatabase("<database-name>");

VectorStore vectorStore = new MongoVectorStore(database);
```

The provider also includes dependency injection extensions in the `Microsoft.Extensions.DependencyInjection` namespace.

## Testing

Unit tests can be run with:

```bash
dotnet test test\MongoDB.UnitTests
```

Conformance tests can run against a local Testcontainers-managed MongoDB Atlas Local container, or against an existing MongoDB connection configured in `testsettings.json`, `testsettings.development.json`, or environment variables:

```json
{
  "MongoDB": {
    "ConnectionURL": "<connection-string>"
  }
}
```

```bash
dotnet test test\MongoDB.ConformanceTests
```
