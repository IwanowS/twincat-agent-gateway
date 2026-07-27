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
    public void HealthAndStatusReadPublishedSnapshot()
    {
        using ServiceFixture fixture = new();
        fixture.Status.Update(status =>
        {
            status.Gateway.State = GatewayState.Disconnected;
            return status;
        });

        HealthResult health = fixture.Service.GetHealth();
        GatewayStatusResult status = fixture.Service.GetStatus();

        Assert.True(health.Ready);
        Assert.Equal(GatewayState.Disconnected, health.State);
        Assert.Equal(GatewayState.Disconnected, status.Gateway.State);
    }

    [Fact]
    public async Task QueuedOperationCanBeCancelledThroughApplicationService()
    {
        using ServiceFixture fixture = new();
        TaskCompletionSource<bool> firstStarted = NewCompletionSource();
        TaskCompletionSource<bool> releaseFirst = NewCompletionSource();
        fixture.Queue.Enqueue(
            OperationKind.Build,
            async cancellationToken =>
            {
                firstStarted.SetResult(true);
                await WaitAsync(releaseFirst.Task, cancellationToken);
                return OperationExecutionResult.Success();
            });
        OperationAccepted queued = fixture.Queue.Enqueue(
            OperationKind.Activate,
            cancellationToken =>
                Task.FromResult(OperationExecutionResult.Success()));
        await firstStarted.Task;

        CancelOperationResult cancellation =
            fixture.Service.CancelOperation(queued.OperationId);
        releaseFirst.SetResult(true);
        await fixture.Queue.StopAsync();

        Assert.True(cancellation.Cancelled);
        Assert.Equal(OperationState.Cancelled, cancellation.State);
        Assert.Equal(
            OperationState.Cancelled,
            fixture.Service.GetOperation(queued.OperationId).Operation.State);
    }

    [Fact]
    public void UnknownOperationReturnsStableError()
    {
        using ServiceFixture fixture = new();

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => fixture.Service.GetOperation("missing"));

        Assert.Equal(ErrorCodes.OperationNotFound, exception.Code);
    }

    [Fact]
    public void ResourceReadsArePagedThroughApplicationService()
    {
        using ServiceFixture fixture = new();
        ResourceReference reference = fixture.Logs.WriteText(
            "operation-1",
            ResourceKind.BuildLog,
            "abcdefghij");

        ResourceContent first =
            fixture.Service.GetResource(reference.Uri, 4, offset: 0);
        ResourceContent second = fixture.Service.GetResource(
            reference.Uri,
            4,
            first.NextOffset!.Value);

        Assert.Equal("abcd", first.Content);
        Assert.Equal("efgh", second.Content);
        Assert.Equal(8, second.NextOffset);
    }

    [Fact]
    public void DiagnosticsMergeRuntimeEvidenceWithCurrentStatus()
    {
        using ServiceFixture fixture = new(
            () => new GatewayDiagnosticsResult
            {
                DteInstances =
                {
                    new DteInstanceInfo
                    {
                        Moniker = "!TcXaeShell.DTE.15.0:1234",
                    },
                },
                Xae = new XaeDiagnostics
                {
                    SysManagerAvailable = true,
                },
                Com = new ComDiagnostics
                {
                    RetryCount = 2,
                },
            });
        fixture.Status.Update(status =>
        {
            status.Gateway.State = GatewayState.Ready;
            return status;
        });

        GatewayDiagnosticsResult diagnostics =
            fixture.Service.GetDiagnostics();

        Assert.Equal(GatewayState.Ready, diagnostics.Status.Gateway.State);
        Assert.Single(diagnostics.DteInstances);
        Assert.True(diagnostics.Xae.SysManagerAvailable);
        Assert.Equal(2, diagnostics.Com.RetryCount);
        Assert.True(diagnostics.Ipc.Healthy);
        Assert.True(diagnostics.LogStore.Healthy);
    }

    private static TaskCompletionSource<bool> NewCompletionSource()
    {
        return new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task WaitAsync(
        Task task,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> cancelled = NewCompletionSource();
        using CancellationTokenRegistration registration =
            cancellationToken.Register(() => cancelled.TrySetCanceled(cancellationToken));
        Task completed = await Task.WhenAny(task, cancelled.Task);
        await completed;
    }

    private sealed class ServiceFixture : IDisposable
    {
        private readonly string _temporaryDirectory;

        public ServiceFixture(
            Func<GatewayDiagnosticsResult>? diagnosticsProvider = null)
        {
            _temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "TwinCatGatewayTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temporaryDirectory);
            Status = new GatewayStatusSnapshotStore(
                GatewayStatusSnapshotStore.CreateInitial("0.1.0"));
            Operations = new OperationStore();
            Logs = new LocalLogStore(_temporaryDirectory);
            Queue = new OperationQueue(Operations);
            Service = new GatewayApplicationService(
                "0.1.0",
                Status,
                Operations,
                Queue,
                Logs,
                diagnosticsProvider);
        }

        public GatewayStatusSnapshotStore Status { get; }

        public OperationStore Operations { get; }

        public LocalLogStore Logs { get; }

        public OperationQueue Queue { get; }

        public GatewayApplicationService Service { get; }

        public void Dispose()
        {
            Queue.Dispose();
            if (Directory.Exists(_temporaryDirectory))
            {
                Directory.Delete(_temporaryDirectory, recursive: true);
            }
        }
    }
}
