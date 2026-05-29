// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.VectorData;
using Xunit;

namespace MongoDB.VectorData.UnitTests;

/// <summary>
/// Unit tests for <see cref="VectorStoreErrorHandler"/> retry behavior.
/// </summary>
public sealed class VectorStoreErrorHandlerTests
{
    private static readonly VectorStoreCollectionMetadata Metadata = new() { VectorStoreSystemName = "test" };

    [Fact]
    public async Task RunOperationWithRetryInvokesOperationOnceWhenMaxRetriesIsZero()
    {
        var callCount = 0;

        var result = await VectorStoreErrorHandler.RunOperationWithRetryAsync<int, InvalidOperationException>(
            Metadata, "op", maxRetries: 0, delayInMilliseconds: 0,
            () => { callCount++; return Task.FromResult(42); },
            CancellationToken.None);

        Assert.Equal(1, callCount);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task RunOperationWithRetryWrapsTheRealExceptionWhenMaxRetriesIsZero()
    {
        var callCount = 0;

        var exception = await Assert.ThrowsAsync<VectorStoreException>(() =>
            VectorStoreErrorHandler.RunOperationWithRetryAsync<int, InvalidOperationException>(
                Metadata, "op", maxRetries: 0, delayInMilliseconds: 0,
                () => { callCount++; throw new InvalidOperationException("boom"); },
                CancellationToken.None));

        Assert.Equal(1, callCount);
        var aggregate = Assert.IsType<AggregateException>(exception.InnerException);
        Assert.Single(aggregate.InnerExceptions);
        Assert.IsType<InvalidOperationException>(aggregate.InnerExceptions[0]);
    }

    [Fact]
    public async Task RunOperationWithRetryAttemptsExactlyMaxRetriesTimesThenThrows()
    {
        var callCount = 0;

        await Assert.ThrowsAsync<VectorStoreException>(() =>
            VectorStoreErrorHandler.RunOperationWithRetryAsync<int, InvalidOperationException>(
                Metadata, "op", maxRetries: 3, delayInMilliseconds: 0,
                () => { callCount++; throw new InvalidOperationException("boom"); },
                CancellationToken.None));

        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task RunOperationWithRetryReturnsAfterTransientFailures()
    {
        var callCount = 0;

        var result = await VectorStoreErrorHandler.RunOperationWithRetryAsync<int, InvalidOperationException>(
            Metadata, "op", maxRetries: 3, delayInMilliseconds: 0,
            () =>
            {
                callCount++;
                if (callCount < 3)
                {
                    throw new InvalidOperationException();
                }

                return Task.FromResult(7);
            },
            CancellationToken.None);

        Assert.Equal(3, callCount);
        Assert.Equal(7, result);
    }

    [Fact]
    public async Task RunOperationWithRetryVoidInvokesOperationOnceWhenMaxRetriesIsZero()
    {
        var callCount = 0;

        await VectorStoreErrorHandler.RunOperationWithRetryAsync<InvalidOperationException>(
            Metadata, "op", maxRetries: 0, delayInMilliseconds: 0,
            () => { callCount++; return Task.CompletedTask; },
            CancellationToken.None);

        Assert.Equal(1, callCount);
    }
}
