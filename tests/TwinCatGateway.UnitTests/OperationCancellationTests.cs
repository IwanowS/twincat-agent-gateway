using System;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class OperationCancellationTests
{
    [Fact]
    public async Task QueuedOperationIsCancelledImmediately()
    {
        OperationStore store = new();
        using OperationQueue queue = new(store);
        OperationCancellationService cancellation = new(queue);
        TaskCompletionSource<bool> firstStarted = NewCompletionSource();
        TaskCompletionSource<bool> releaseFirst = NewCompletionSource();
        bool queuedExecuted = false;

        queue.Enqueue(
            OperationKind.XaeBuild,
            async cancellationToken =>
            {
                firstStarted.SetResult(true);
                await releaseFirst.Task;
                return OperationExecutionResult.Success();
            });
        OperationAccepted queued = queue.Enqueue(
            OperationKind.XaeBuild,
            cancellationToken =>
            {
                queuedExecuted = true;
                return Task.FromResult(
                    OperationExecutionResult.Success());
            });

        await firstStarted.Task;
        OperationCancellationResult result =
            cancellation.Cancel(queued.OperationId);

        Assert.Equal(
            OperationCancellationResult.CancelledBeforeStart,
            result);
        Assert.Equal(
            OperationState.Cancelled,
            store.Get(queued.OperationId)?.Summary.State);
        releaseFirst.SetResult(true);
        await queue.StopAsync();
        Assert.False(queuedExecuted);
    }

    [Fact]
    public async Task RunningOperationReceivesCooperativeRequest()
    {
        OperationStore store = new();
        using OperationQueue queue = new(store);
        OperationCancellationService cancellation = new(queue);
        TaskCompletionSource<bool> started = NewCompletionSource();

        OperationAccepted operation = queue.Enqueue(
            OperationKind.XaeBuild,
            async cancellationToken =>
            {
                started.SetResult(true);
                await Task.Delay(
                    TimeSpan.FromSeconds(30),
                    cancellationToken);
                return OperationExecutionResult.Success();
            });

        await started.Task;
        OperationCancellationResult result =
            cancellation.Cancel(operation.OperationId);

        Assert.Equal(
            OperationCancellationResult.CancellationRequested,
            result);
        await WaitForStateAsync(
            store,
            operation.OperationId,
            OperationState.Cancelled);
        await queue.StopAsync();
    }

    [Fact]
    public async Task TerminalAndMissingOperationsRemainUnchanged()
    {
        OperationStore store = new();
        using OperationQueue queue = new(store);
        OperationCancellationService cancellation = new(queue);
        OperationAccepted operation = queue.Enqueue(
            OperationKind.XaeBuild,
            cancellationToken => Task.FromResult(
                OperationExecutionResult.Success()));
        await WaitForStateAsync(
            store,
            operation.OperationId,
            OperationState.Succeeded);

        Assert.Equal(
            OperationCancellationResult.AlreadyTerminal,
            cancellation.Cancel(operation.OperationId));
        Assert.Equal(
            OperationCancellationResult.NotFound,
            cancellation.Cancel("missing-operation"));
        Assert.Equal(
            OperationState.Succeeded,
            store.Get(operation.OperationId)?.Summary.State);
        await queue.StopAsync();
    }

    [Fact]
    public async Task OperatorLockDoesNotRequestCancellation()
    {
        OperationStore store = new();
        using OperationQueue queue = new(store);
        OperatorLockStore locks = new();
        TaskCompletionSource<bool> started = NewCompletionSource();
        TaskCompletionSource<bool> release = NewCompletionSource();
        bool cancellationObserved = false;

        OperationAccepted operation = queue.Enqueue(
            OperationKind.XaeBuild,
            async cancellationToken =>
            {
                started.SetResult(true);
                await release.Task;
                cancellationObserved =
                    cancellationToken.IsCancellationRequested;
                return OperationExecutionResult.Success();
            });

        await started.Task;
        locks.SetLocked(
            "bench",
            OperatorLockKey.XaeSynchronizationBuild,
            locked: true);
        release.SetResult(true);

        await WaitForStateAsync(
            store,
            operation.OperationId,
            OperationState.Succeeded);
        await queue.StopAsync();
        Assert.False(cancellationObserved);
    }

    private static TaskCompletionSource<bool> NewCompletionSource()
    {
        return new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task WaitForStateAsync(
        OperationStore store,
        string operationId,
        OperationState expected)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (store.Get(operationId)?.Summary.State == expected)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException(
            $"Operation '{operationId}' did not reach {expected}.");
    }
}
