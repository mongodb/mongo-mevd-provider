// Copyright (c) Microsoft. All rights reserved.

using System.Reflection;
using MongoDB.Driver;
using MongoDB.Driver.Core.Configuration;

namespace MongoDB.VectorData;

internal static class MongoClientMetadata
{
    /// <summary>The library name reported to the server. Telemetry keys on this exact string.</summary>
    private const string LibraryName = "MongoDB.VectorData";

    public static LibraryInfo LibraryInfo { get; } = CreateLibraryInfo();

    /// <summary>
    /// Declares this provider in the handshake of the client backing <paramref name="mongoDatabase"/>.
    /// Only connections opened after this call are affected, and the driver ignores repeat declarations.
    /// </summary>
    /// <param name="mongoDatabase">The database whose client declares this provider.</param>
    public static void AppendTo(IMongoDatabase mongoDatabase)
    {
        // Databases obtained from a MongoClient report that client here, even when the caller wraps the
        // client, so this only misses databases the caller implemented or wrapped themselves - and test doubles.
        if (mongoDatabase.Client is MongoClient mongoClient)
        {
            mongoClient.AppendMetadata(LibraryInfo);
        }
    }

    private static LibraryInfo CreateLibraryInfo()
    {
        var assembly = typeof(MongoClientMetadata).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString();

        // Source Link appends the commit the assembly was built from, which is not part of the version.
        var separatorIndex = version?.IndexOf('+') ?? -1;
        if (separatorIndex != -1)
        {
            version = version!.Substring(0, separatorIndex);
        }

        return new LibraryInfo(LibraryName, version);
    }
}
