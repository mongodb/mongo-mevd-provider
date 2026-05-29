// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.Extensions.VectorData.ProviderServices;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MongoDB.VectorData;

/// <summary>
/// Customized MongoDB model builder that adds specialized configuration of property storage names
/// (Mongo's reserved key property name and [BsonElement]).
/// </summary>
/// <remarks>
/// The provider serializes records with the MongoDB BSON serializer and therefore sets
/// <c>UsesExternalSerializer = true</c>. As a result, MEVD does not apply a storage name set via
/// <c>[VectorStoreData(StorageName = ...)]</c> (or the other <c>VectorStore*</c> attributes / definition properties)
/// to CLR-backed (typed) properties. For a typed record the BSON field name is the <c>[BsonElement]</c> name when
/// present (applied in <see cref="ProcessProperty"/>), otherwise the CLR property name — so use <c>[BsonElement]</c>,
/// not the MEVD <c>StorageName</c>, to customize a typed record's storage name. For dynamic (definition-only) records
/// there is no CLR property, so the definition's <c>StorageName</c> is honored. The key property is always stored
/// under Mongo's reserved <c>_id</c> name (handled by the mapper).
/// </remarks>
internal class MongoModelBuilder() : CollectionModelBuilder(s_validationOptions)
{
    internal const string SupportedVectorTypes = "ReadOnlyMemory<float>, Embedding<float>, float[]";

    private static readonly CollectionModelBuildingOptions s_validationOptions = new()
    {
        RequiresAtLeastOneVector = false,
        SupportsMultipleVectors = true,
        UsesExternalSerializer = true,
    };

    protected override void ProcessProperty(PropertyInfo? clrProperty, VectorStoreProperty? definitionProperty)
    {
        base.ProcessProperty(clrProperty, definitionProperty);

        // For CLR-backed properties the base does not apply a MEVD StorageName (UsesExternalSerializer = true), so the
        // BSON field name comes from [BsonElement] when present; otherwise it falls back to the CLR property name.
        if (clrProperty?.GetCustomAttribute<BsonElementAttribute>() is { } bsonElementAttribute
            && this.PropertyMap.TryGetValue(clrProperty.Name, out var property))
        {
            property.StorageName = bsonElementAttribute.ElementName;
        }
    }

    protected override bool SupportsKeyAutoGeneration(Type keyPropertyType)
        => keyPropertyType == typeof(Guid) || keyPropertyType == typeof(ObjectId);

    protected override void ValidateKeyProperty(KeyPropertyModel keyProperty)
    {
        base.ValidateKeyProperty(keyProperty);

        var type = keyProperty.Type;

        if (type != typeof(string) && type != typeof(int) && type != typeof(long) && type != typeof(Guid) && type != typeof(ObjectId))
        {
            throw new NotSupportedException(
                $"Property '{keyProperty.ModelName}' has unsupported type '{type.Name}'. Key properties must be one of the supported types: string, int, long, Guid, ObjectId.");
        }
    }

    protected override bool IsDataPropertyTypeValid(Type type, [NotNullWhen(false)] out string? supportedTypes)
    {
        supportedTypes = "string, int, long, double, float, bool, decimal, DateTime, DateTimeOffset,"
#if NET
            + " DateOnly,"
#endif
            + " or arrays/lists of these types";

        if (Nullable.GetUnderlyingType(type) is Type underlyingType)
        {
            type = underlyingType;
        }

        return IsValid(type)
            || (type.IsArray && IsValid(type.GetElementType()!))
            || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) && IsValid(type.GenericTypeArguments[0]));

        static bool IsValid(Type type)
            => type == typeof(bool) ||
                type == typeof(string) ||
                type == typeof(int) ||
                type == typeof(long) ||
                type == typeof(float) ||
                type == typeof(double) ||
                type == typeof(decimal) ||
                type == typeof(DateTime) ||
                type == typeof(DateTimeOffset) ||
#if NET
                type == typeof(DateOnly) ||
#endif
                false;
    }

    protected override bool IsVectorPropertyTypeValid(Type type, [NotNullWhen(false)] out string? supportedTypes)
        => IsVectorPropertyTypeValidCore(type, out supportedTypes);

    internal static bool IsVectorPropertyTypeValidCore(Type type, [NotNullWhen(false)] out string? supportedTypes)
    {
        supportedTypes = SupportedVectorTypes;

        return type == typeof(ReadOnlyMemory<float>)
            || type == typeof(ReadOnlyMemory<float>?)
            || type == typeof(Embedding<float>)
            || type == typeof(float[]);
    }
}
