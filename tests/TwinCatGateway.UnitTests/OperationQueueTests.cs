using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class OperationQueueTests
{
    [Fact]
    public async Task ModifyingOperationsExecuteSequentially()
    {
        OperationStore store = new();
        using OperationQueue queue = new(store);
        TaskCompletionSource<bool> firstStarted = NewCompletionSource();
        TaskCompletionSource<bool> releaseFirst = NewCompletionSource();
        ConcurrentQueue<string> events = new();

        OperationAccepted first = queue.Enqueue(
            OperationKind.Build,
            async cancellationToken =>
            {
                events.Enqueue("first-start");
                firstStarted.SetResult(true);
                await WaitAsync(releaseFirst.Task, cancellationToken);
                events.Enqueue("first-end");
                return OperationExecutionResult.Success();
            });
        OperationAccepted second = queue.Enqueue(
            OperationKind.Activate,
            cancellationToken =>
            {
                events.Enqueue("second-start");
                return Task.FromResult(OperationExecutionResult.Success());
            });

        await firstStarted.Task;
        Assert.Equal(OperationState.Running, store.Get(first.OperationId)?.Summary.State);
        Assert.Equal(OperationState.Queued, store.Get(second.OperationId)?.Summary.State);

        releaseFirst.SetResult(true);
        await WaitForStateAsync(store, second.OperationId, OperationState.Succeeded);
        await queue.StopAsync();

        Assert.Collection(
            events,
            item => Assert.Equal("first-start", item),
            item => Assert.Equal("first-end", item),
            item => Assert.Equal("second-start", item));
    }

    [Fact]
    public async Task QueuedOperationCanBeCancelledBeforeItStarts()
    {
        OperationStore store = new();
        using OperationQueue queue = new(store);
        TaskCompletionSource<bool> firstStarted = NewCompletionSource();
        TaskCompletionSource<bool> releaseFirst = NewCompletionSource();
        bool secondExecuted = false;

        queue.Enqueue(
            OperationKind.Build,
            async cancellationToken =>
            {
                firstStarted.SetResult(true);
                await WaitAsync(releaseFirst.Task, cancellationToken);
                return OperationExecutionResult.Success();
            });
        OperationAccepted second = queue.Enqueue(
            OperationKind.Activate,
            cancellationToken =>
            {
                secondExecuted = true;
                return Task.FromResult(OperationExecutionResult.Success());
            });

        await firstStarted.Task;
        OperationCancellationResult cancellation = queue.CancelBeforeStart(second.OperationId);
        releaseFirst.SetResult(true);

        await WaitForStateAsync(store, second.OperationId, OperationState.Cancelled);
        await queue.StopAsync();

        Assert.Equal(OperationCancellationResult.Cancelled, cancellation);
        Assert.False(secondExecuted);
    }

    [Fact]
    public async Task OperationFailureDoesNotStopTheQueue()
    {
        OperationStore store = new();
        using OperationQueue queue = new(store);

        OperationAccepted failed = queue.Enqueue(
            OperationKind.Build,
            cancellationToken => throw new InvalidOperationException("boom"));
        OperationAccepted succeeded = queue.Enqueue(
            OperationKind.Build,
            cancellationToken =>
                Task.FromResult(OperationExecutionResult.Success("complete")));

        StoredOperation failedOperation =
            await WaitForStateAsync(store, failed.OperationId, OperationState.Failed);
        StoredOperation succeededOperation =
            await WaitForStateAsync(store, succeeded.OperationId, OperationState.Succeeded);
        await queue.StopAsync();

        Assert.Equal(ErrorCodes.OperationFailed, failedOperation.Summary.Error?.Code);
        Assert.Equal("complete", succeededOperation.Result);
    }

    [Fact]
    public async Task DeadlineCancelsCooperativeOperationAndMarksTimeout()
    {
        OperationStore store = new();
        GatewayStatusSnapshotStore status =
            new(GatewayStatusSnapshotStore.CreateInitial("0.1.0"));
        GatewayEventJournal events = new(status);
        using OperationQueue queue = new(
            store,
            gatewayEventSink: events);

        OperationAccepted accepted = queue.Enqueue(
            OperationKind.Build,
            async cancellationToken =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                return OperationExecutionResult.Success();
            },
            TimeSpan.FromMilliseconds(50));

        StoredOperation operation =
            await WaitForStateAsync(store, accepted.OperationId, OperationState.TimedOut);
        await queue.StopAsync();

        Assert.Equal(ErrorCodes.OperationTimeout, operation.Summary.Error?.Code);
        Assert.True(operation.Summary.Error?.Retryable);
        Assert.Equal(1, status.Read().LatestEventCursor);
        Assert.Equal(
            ErrorCodes.OperationTimeout,
            Assert.Single(
                events.ReadAfter(null, 0, 100).Events)
                .Error?.Code);
    }

    [Fact]
    public async Task OperationReceivesItsStableId()
    {
        OperationStore store = new();
        using OperationQueue queue = new(store);
        string? executedId = null;

        OperationAccepted accepted = queue.Enqueue(
            OperationKind.Build,
            (operationId, cancellationToken) =>
            {
                executedId = operationId;
                return Task.FromResult(
                    OperationExecutionResult.Success());
            });

        await WaitForStateAsync(
            store,
            accepted.OperationId,
            OperationState.Succeeded);
        await queue.StopAsync();

        Assert.Equal(accepted.OperationId, executedId);
    }

    private static TaskCompletionSource<bool> NewCompletionSource()
    {
        return new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task WaitAsync(Task task, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> cancelled = NewCompletionSource();
        using CancellationTokenRegistration registration =
            cancellationToken.Register(() => cancelled.TrySetCanceled(cancellationToken));
        Task completed = await Task.WhenAny(task, cancelled.Task);
        await completed;
    }

    private static async Task<StoredOperation> WaitForStateAsync(
        OperationStore store,
        string operationId,
        OperationState expected)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            StoredOperation? operation = store.Get(operationId);
            if (operation?.Summary.State == expected)
            {
                return operation;
            }

            await Task.Delay(10);
        }

        OperationState? actual = store.Get(operationId)?.Summary.State;
        throw new TimeoutException(
            $"Operation '{operationId}' did not reach {expected}; actual: {actual}.");
    }
}
