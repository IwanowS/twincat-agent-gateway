using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class GatewayApplicationServiceTests
{
    [Fact]
    public void GatewayStateUsesTheObjectSnapshot()
    {
        using ServiceFixture fixture = new();
        fixture.Status.Update(snapshot =>
        {
            snapshot.State = GatewayProcessState.Ready;
            snapshot.ActiveProfile = "fixture";
            return snapshot;
        });

        GatewayStateSnapshot state = fixture.Service.GetGatewayState();

        Assert.Equal(GatewayProcessState.Ready, state.State);
        Assert.Equal("fixture", state.ActiveProfile);
    }

    [Fact]
    public void EventPagingRejectsCursorWithoutJournalIdentity()
    {
        using ServiceFixture fixture = new();

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(() =>
                fixture.Service.GetOperationEvents(
                    new GetDiagnosticsParameters { AfterEventCursor = 1 }));

        Assert.Equal(ErrorCodes.RequestInvalid, exception.Code);
        Assert.Equal("diagnostics.validate", exception.Stage);
    }

    [Fact]
    public async Task PreflightFailureIsJournaledUnderItsExactOperationId()
    {
        using ServiceFixture fixture = new();
        OperationHandle handle = fixture.Service.EnqueuePreflightFailure(
            OperationKind.XaeBuild,
            "fixture",
            new GatewayOperationException(
                ErrorCodes.CapabilityDisabled,
                "Build is disabled.",
                stage: "xae.build.admission",
                component: GatewayComponent.Xae,
                sideEffectsStarted: false));

        OperationResult<object> result =
            await fixture.Service.WaitForOperationAsync<object>(
                handle.OperationId,
                CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(handle.OperationId, result.OperationId);
        Assert.Equal(handle.OperationId, result.Error?.OperationId);
        Assert.Equal(GatewayComponent.Xae, result.Error?.Component);
        Assert.False(result.Error?.SideEffectsStarted);
        GatewayEvent failed = Assert.Single(
            fixture.Service.GetOperationEvents(
                    new GetDiagnosticsParameters
                    {
                        EventStreamId = fixture.Events.JournalId,
                        MaximumEvents = 20,
                    })
                .Events,
            gatewayEvent => gatewayEvent.OperationId == handle.OperationId
                && gatewayEvent.Severity == DiagnosticSeverity.Error);
        Assert.Equal(ErrorCodes.CapabilityDisabled, failed.Code);
    }

    [Fact]
    public async Task OperationSnapshotRetainsTypedResultAndProfile()
    {
        using ServiceFixture fixture = new();
        OperationHandle handle = fixture.Queue.Enqueue(
            OperationKind.XaeBuild,
            _ => Task.FromResult(
                OperationExecutionResult.Success(
                    new XaeBuildResult
                    {
                        Action = BuildAction.Build,
                        Scope = XaeBuildScope.Plc,
                    })),
            profile: "fixture");

        OperationResult<XaeBuildResult> result =
            await fixture.Service.WaitForOperationAsync<XaeBuildResult>(
                handle.OperationId,
                CancellationToken.None);
        OperationSnapshot<object> snapshot =
            fixture.Service.GetOperation(handle.OperationId);

        Assert.True(result.Ok);
        Assert.Equal(BuildAction.Build, result.Result?.Action);
        Assert.Equal("fixture", snapshot.Operation.Profile);
        Assert.Equal(OperationState.Succeeded, snapshot.Operation.State);
    }

    [Fact]
    public async Task QueuedOperationCanBeCancelledByExactId()
    {
        using ServiceFixture fixture = new();
        TaskCompletionSource<bool> started = NewCompletionSource();
        TaskCompletionSource<bool> release = NewCompletionSource();
        fixture.Queue.Enqueue(
            OperationKind.XaeBuild,
            async cancellationToken =>
            {
                started.SetResult(true);
                await WaitAsync(release.Task, cancellationToken);
                return OperationExecutionResult.Success();
            });
        OperationHandle queued = fixture.Queue.Enqueue(
            OperationKind.XaeBuild,
            _ => Task.FromResult(OperationExecutionResult.Success()));

        await started.Task;
        OperationCancellationReceipt receipt =
            fixture.Service.CancelOperation(queued.OperationId);
        release.SetResult(true);

        Assert.Equal(queued.OperationId, receipt.OperationId);
        Assert.True(receipt.CancellationRequested);
        Assert.Equal(OperationState.Cancelled, receipt.State);
    }

    [Theory]
    [InlineData("twincat-operation://missing")]
    [InlineData("twincat-operation://missing/build")]
    public void MissingOperationResourcesDoNotFallBack(string uri)
    {
        using ServiceFixture fixture = new();

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(() =>
                fixture.Service.GetResource(uri, 4096, 0));

        Assert.Equal(ErrorCodes.ResourceNotFound, exception.Code);
    }

    [Fact]
    public async Task XaeMessagesProviderReceivesBoundedRequest()
    {
        GetXaeMessagesParameters? observed = null;
        using ServiceFixture fixture = new(
            xaeMessagesProvider: (parameters, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                observed = parameters;
                return Task.FromResult(new XaeMessagesResult());
            });

        await fixture.Service.GetXaeMessagesAsync(
            new GetXaeMessagesParameters
            {
                MaximumMessages = 17,
            },
            CancellationToken.None);

        Assert.Equal(17, observed?.MaximumMessages);
    }

    [Fact]
    public async Task WaitCancellationTargetsTheExactOperation()
    {
        using ServiceFixture fixture = new();
        TaskCompletionSource<bool> started = NewCompletionSource();
        OperationHandle handle = fixture.Queue.Enqueue(
            OperationKind.XaeBuild,
            async cancellationToken =>
            {
                started.SetResult(true);
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                return OperationExecutionResult.Success();
            });
        await started.Task;
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.WaitForOperationAsync<object>(
                handle.OperationId,
                cancellation.Token));

        await WaitForStateAsync(
            fixture.Operations,
            handle.OperationId,
            OperationState.Cancelled);
    }

    private static TaskCompletionSource<bool> NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitAsync(
        Task task,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> cancelled = NewCompletionSource();
        using CancellationTokenRegistration registration =
            cancellationToken.Register(
                () => cancelled.TrySetCanceled(cancellationToken));
        await await Task.WhenAny(task, cancelled.Task);
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

    private sealed class ServiceFixture : IDisposable
    {
        private readonly string _root;

        public ServiceFixture(
            XaeMessagesProvider? xaeMessagesProvider = null)
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "TwinCatGatewayTests",
                Guid.NewGuid().ToString("N"));
            Status = new GatewayStatusSnapshotStore(
                GatewayStatusSnapshotStore.CreateInitial("test"));
            Operations = new OperationStore();
            Events = new GatewayEventJournal();
            Queue = new OperationQueue(
                Operations,
                gatewayEventSink: Events);
            Service = new GatewayApplicationService(
                "test",
                Status,
                Operations,
                Queue,
                new LocalLogStore(_root),
                Events,
                xaeMessagesProvider: xaeMessagesProvider);
        }

        public GatewayStatusSnapshotStore Status { get; }

        public OperationStore Operations { get; }

        public GatewayEventJournal Events { get; }

        public OperationQueue Queue { get; }

        public GatewayApplicationService Service { get; }

        public void Dispose()
        {
            Queue.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
