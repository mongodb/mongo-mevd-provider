// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Extensions.VectorData;

#pragma warning disable MEVD9000 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

/// <summary>
/// Runs vector-store operations, translating provider exceptions into <see cref="VectorStoreException"/>
/// (with the operation/store/collection metadata) and providing the retry variants used by the provider.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class VectorStoreErrorHandler
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Task<TResult> RunOperationAsync<TResult, TException>(
        VectorStoreMetadata metadata,
        string operationName,
        Func<Task<TResult>> operation)
        where TException : Exception
    {
        return RunOperationAsync<TResult, TException>(
            new VectorStoreCollectionMetadata()
            {
                CollectionName = null,
                VectorStoreName = metadata.VectorStoreName,
                VectorStoreSystemName = metadata.VectorStoreSystemName,
            },
            operationName,
            operation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async Task<TResult> RunOperationAsync<TResult, TException>(
        VectorStoreCollectionMetadata metadata,
        string operationName,
        Func<Task<TResult>> operation)
        where TException : Exception
    {
        try
        {
            return await operation.Invoke().ConfigureAwait(false);
        }
        catch (AggregateException ex) when (ex.InnerException is TException innerEx)
        {
            throw new VectorStoreException("Call to vector store failed.", ex)
            {
                VectorStoreSystemName = metadata.VectorStoreSystemName,
                VectorStoreName = metadata.VectorStoreName,
                CollectionName = metadata.CollectionName,
                OperationName = operationName
            };
        }
        catch (TException ex)
        {
            throw new VectorStoreException("Call to vector store failed.", ex)
            {
                VectorStoreSystemName = metadata.VectorStoreSystemName,
                VectorStoreName = metadata.VectorStoreName,
                CollectionName = metadata.CollectionName,
                OperationName = operationName
            };
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TResult RunOperation<TResult, TException>(
        VectorStoreMetadata metadata,
        string operationName,
        Func<TResult> operation)
        where TException : Exception
    {
        return RunOperation<TResult, TException>(
            new VectorStoreCollectionMetadata()
            {
                CollectionName = null,
                VectorStoreName = metadata.VectorStoreName,
                VectorStoreSystemName = metadata.VectorStoreSystemName,
            },
            operationName,
            operation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TResult RunOperation<TResult, TException>(
        VectorStoreCollectionMetadata metadata,
        string operationName,
        Func<TResult> operation)
        where TException : Exception
    {
        try
        {
            return operation.Invoke();
        }
        catch (AggregateException ex) when (ex.InnerException is TException innerEx)
        {
            throw new VectorStoreException("Call to vector store failed.", ex)
            {
                VectorStoreSystemName = metadata.VectorStoreSystemName,
                VectorStoreName = metadata.VectorStoreName,
                CollectionName = metadata.CollectionName,
                OperationName = operationName
            };
        }
        catch (TException ex)
        {
            throw new VectorStoreException("Call to vector store failed.", ex)
            {
                VectorStoreSystemName = metadata.VectorStoreSystemName,
                VectorStoreName = metadata.VectorStoreName,
                CollectionName = metadata.CollectionName,
                OperationName = operationName
            };
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async Task<TResult> RunOperationWithRetryAsync<TResult, TException>(
        VectorStoreCollectionMetadata metadata,
        string operationName,
        int maxRetries,
        int delayInMilliseconds,
        Func<Task<TResult>> operation,
        CancellationToken cancellationToken)
        where TException : Exception
    {
        var retries = 0;

        var exceptions = new List<Exception>();

        while (retries < maxRetries)
        {
            try
            {
                return await operation.Invoke().ConfigureAwait(false);
            }
            catch (AggregateException ex) when (ex.InnerException is TException innerEx)
            {
                retries++;
                exceptions.Add(ex);

                if (retries >= maxRetries)
                {
                    throw new VectorStoreException("Call to vector store failed.", new AggregateException(exceptions))
                    {
                        VectorStoreSystemName = metadata.VectorStoreSystemName,
                        VectorStoreName = metadata.VectorStoreName,
                        CollectionName = metadata.CollectionName,
                        OperationName = operationName
                    };
                }

                await Task.Delay(delayInMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (TException ex)
            {
                retries++;
                exceptions.Add(ex);

                if (retries >= maxRetries)
                {
                    throw new VectorStoreException("Call to vector store failed.", new AggregateException(exceptions))
                    {
                        VectorStoreSystemName = metadata.VectorStoreSystemName,
                        VectorStoreName = metadata.VectorStoreName,
                        CollectionName = metadata.CollectionName,
                        OperationName = operationName
                    };
                }

                await Task.Delay(delayInMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new VectorStoreException("Call to vector store failed.", new AggregateException(exceptions))
        {
            VectorStoreSystemName = metadata.VectorStoreSystemName,
            VectorStoreName = metadata.VectorStoreName,
            CollectionName = metadata.CollectionName,
            OperationName = operationName
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async Task RunOperationAsync<TException>(
        VectorStoreCollectionMetadata metadata,
        string operationName,
        Func<Task> operation)
        where TException : Exception
    {
        try
        {
            await operation.Invoke().ConfigureAwait(false);
        }
        catch (AggregateException ex) when (ex.InnerException is TException innerEx)
        {
            throw new VectorStoreException("Call to vector store failed.", ex)
            {
                VectorStoreSystemName = metadata.VectorStoreSystemName,
                VectorStoreName = metadata.VectorStoreName,
                CollectionName = metadata.CollectionName,
                OperationName = operationName
            };
        }
        catch (TException ex)
        {
            throw new VectorStoreException("Call to vector store failed.", ex)
            {
                VectorStoreSystemName = metadata.VectorStoreSystemName,
                VectorStoreName = metadata.VectorStoreName,
                CollectionName = metadata.CollectionName,
                OperationName = operationName
            };
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static async Task RunOperationWithRetryAsync<TException>(
        VectorStoreCollectionMetadata metadata,
        string operationName,
        int maxRetries,
        int delayInMilliseconds,
        Func<Task> operation,
        CancellationToken cancellationToken)
        where TException : Exception
    {
        var retries = 0;

        var exceptions = new List<Exception>();

        while (retries < maxRetries)
        {
            try
            {
                await operation.Invoke().ConfigureAwait(false);
                return;
            }
            catch (AggregateException ex) when (ex.InnerException is TException innerEx)
            {
                retries++;
                exceptions.Add(ex);

                if (retries >= maxRetries)
                {
                    throw new VectorStoreException("Call to vector store failed.", new AggregateException(exceptions))
                    {
                        VectorStoreSystemName = metadata.VectorStoreSystemName,
                        VectorStoreName = metadata.VectorStoreName,
                        CollectionName = metadata.CollectionName,
                        OperationName = operationName
                    };
                }

                await Task.Delay(delayInMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (TException ex)
            {
                retries++;
                exceptions.Add(ex);

                if (retries >= maxRetries)
                {
                    throw new VectorStoreException("Call to vector store failed.", new AggregateException(exceptions))
                    {
                        VectorStoreSystemName = metadata.VectorStoreSystemName,
                        VectorStoreName = metadata.VectorStoreName,
                        CollectionName = metadata.CollectionName,
                        OperationName = operationName
                    };
                }

                await Task.Delay(delayInMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new VectorStoreException("Call to vector store failed.", new AggregateException(exceptions))
        {
            VectorStoreSystemName = metadata.VectorStoreSystemName,
            VectorStoreName = metadata.VectorStoreName,
            CollectionName = metadata.CollectionName,
            OperationName = operationName
        };
    }
}
