using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.OperationJournalMigrationTests;

public sealed class OperationJournalTests
{
    [Fact]
    public async Task QueueRetainsProfileAndExactOperationEvents()
    {
        OperationStore store = new();
        GatewayEventJournal journal = new();
        using OperationQueue queue = new(store, gatewayEventSink: journal);

        OperationHandle handle = queue.Enqueue(
            OperationKind.XaeBuild,
            _ => Task.FromResult(OperationExecutionResult.Success(
                diagnostics: new[]
                {
                    new OperationDiagnostic
                    {
                        Code = "BUILD_COMPLETED",
                        Component = GatewayComponent.Xae,
                        Stage = "build",
                        Severity = DiagnosticSeverity.Info,
                        Message = "Build completed.",
                    },
                },
                resources: new[]
                {
                    new ResourceReference
                    {
                        Uri = "twincat-operation://op-placeholder/build",
                        MimeType = "text/plain",
                    },
                })),
            profile: "fixture");

        StoredOperation completed = await WaitForTerminalAsync(
            store,
            handle.OperationId);

        Assert.Equal("fixture", completed.Summary.Profile);
        Assert.Equal(OperationState.Succeeded, completed.Summary.State);
        Assert.NotNull(completed.Summary.DurationMs);
        Assert.Equal("BUILD_COMPLETED", Assert.Single(completed.Summary.Diagnostics).Code);
        Assert.Equal("text/plain", Assert.Single(completed.Summary.Resources).MimeType);

        OperationEventPage events = journal.ReadAfter(
            journal.JournalId,
            0,
            10,
            component: GatewayComponent.Xae,
            profile: "fixture",
            operationId: handle.OperationId);

        Assert.Equal(3, events.Events.Count);
        Assert.All(
            events.Events,
            gatewayEvent =>
            {
                Assert.Equal(handle.OperationId, gatewayEvent.OperationId);
                Assert.Equal("fixture", gatewayEvent.Profile);
                Assert.Equal(GatewayComponent.Xae, gatewayEvent.Component);
                Assert.False(string.IsNullOrWhiteSpace(gatewayEvent.Stage));
                Assert.False(string.IsNullOrWhiteSpace(gatewayEvent.Code));
            });
    }

    [Fact]
    public async Task QueuedCancellationIsTerminalAndJournaled()
    {
        OperationStore store = new();
        GatewayEventJournal journal = new();
        TaskCompletionSource<bool> release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using OperationQueue queue = new(store, gatewayEventSink: journal);

        OperationHandle running = queue.Enqueue(
            OperationKind.XaeBuild,
            async cancellationToken =>
            {
                await release.Task.WaitAsync(cancellationToken);
                return OperationExecutionResult.Success();
            },
            profile: "fixture");
        await WaitForStateAsync(store, running.OperationId, OperationState.Running);

        OperationHandle queued = queue.Enqueue(
            OperationKind.TargetConfig,
            _ => Task.FromResult(OperationExecutionResult.Success()),
            profile: "fixture");

        Assert.Equal(
            OperationCancellationResult.CancelledBeforeStart,
            queue.Cancel(queued.OperationId));
        StoredOperation cancelled = await WaitForTerminalAsync(
            store,
            queued.OperationId);
        Assert.Equal(OperationState.Cancelled, cancelled.Summary.State);

        OperationEventPage events = journal.ReadAfter(
            journal.JournalId,
            0,
            20,
            operationId: queued.OperationId);
        Assert.Equal(2, events.Events.Count);
        Assert.EndsWith("cancelled", events.Events[1].Type, StringComparison.OrdinalIgnoreCase);

        release.SetResult(true);
        await WaitForTerminalAsync(store, running.OperationId);
    }

    [Fact]
    public async Task RunningCancellationIsForwardedOnceAndJournaled()
    {
        OperationStore store = new();
        GatewayEventJournal journal = new();
        using OperationQueue queue = new(store, gatewayEventSink: journal);

        OperationHandle handle = queue.Enqueue(
            OperationKind.TargetStartRestart,
            async cancellationToken =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return OperationExecutionResult.Success();
            },
            profile: "fixture");
        await WaitForStateAsync(store, handle.OperationId, OperationState.Running);

        Assert.Equal(
            OperationCancellationResult.CancellationRequested,
            queue.Cancel(handle.OperationId));
        Assert.Contains(
            queue.Cancel(handle.OperationId),
            new[]
            {
                OperationCancellationResult.CancellationRequested,
                OperationCancellationResult.AlreadyTerminal,
            });

        StoredOperation cancelled = await WaitForTerminalAsync(
            store,
            handle.OperationId);
        Assert.Equal(OperationState.Cancelled, cancelled.Summary.State);

        OperationEventPage events = journal.ReadAfter(
            journal.JournalId,
            0,
            20,
            operationId: handle.OperationId);
        Assert.Equal(3, events.Events.Count);
        Assert.EndsWith("cancelled", events.Events[2].Type, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<StoredOperation> WaitForTerminalAsync(
        OperationStore store,
        string operationId)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            StoredOperation? operation = store.Get(operationId);
            if (operation is not null
                && operation.Summary.State is OperationState.Succeeded
                    or OperationState.Failed
                    or OperationState.Cancelled
                    or OperationState.TimedOut)
            {
                return operation;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Operation did not become terminal.");
    }

    private static async Task WaitForStateAsync(
        OperationStore store,
        string operationId,
        OperationState state)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (store.Get(operationId)?.Summary.State == state)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Operation did not reach the expected state.");
    }
}
