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

    [Fact]
    public async Task BuildUsesStableOperationIdAndCapturedPaths()
    {
        string? executorOperationId = null;
        string? changedPath = null;
        using ServiceFixture fixture = new(
            buildExecutor: (
                operationId,
                parameters,
                cancellationToken) =>
            {
                executorOperationId = operationId;
                changedPath = Assert.Single(
                    parameters.ChangedPaths);
                return Task.FromResult(
                    new BuildResult
                    {
                        Ok = true,
                        Action = parameters.Action,
                    });
            });
        fixture.Status.Update(status =>
        {
            status.Xae.Connected = true;
            status.Gateway.State = GatewayState.Ready;
            return status;
        });
        BuildParameters parameters = new()
        {
            Action = BuildAction.Build,
            ChangedPaths =
            {
                "Plc/POUs/MAIN.TcPOU",
            },
        };

        OperationAccepted accepted =
            fixture.Service.StartBuild(parameters);
        parameters.ChangedPaths[0] = "mutated";
        StoredOperation completed = await WaitForStateAsync(
            fixture.Operations,
            accepted.OperationId,
            OperationState.Succeeded);

        Assert.Equal(accepted.OperationId, executorOperationId);
        Assert.Equal("Plc/POUs/MAIN.TcPOU", changedPath);
        BuildResult result =
            Assert.IsType<BuildResult>(completed.Result);
        Assert.Equal(accepted.OperationId, result.OperationId);
        Assert.True(fixture.Service.GetStatus().LastBuild?.Ok);
        Assert.Equal(
            GatewayState.Ready,
            fixture.Service.GetStatus().Gateway.State);
    }

    [Fact]
    public async Task BuildFailurePreservesResultAndMarksOperationFailed()
    {
        using ServiceFixture fixture = new(
            buildExecutor: (
                operationId,
                parameters,
                cancellationToken) =>
                Task.FromResult(
                    new BuildResult
                    {
                        Ok = false,
                        Action = parameters.Action,
                        Counts = new DiagnosticCounts
                        {
                            Errors = 1,
                        },
                    }));
        fixture.Status.Update(status =>
        {
            status.Xae.Connected = true;
            status.Gateway.State = GatewayState.Ready;
            return status;
        });

        OperationAccepted accepted =
            fixture.Service.StartBuild(
                new BuildParameters
                {
                    Action = BuildAction.Rebuild,
                });
        StoredOperation completed = await WaitForStateAsync(
            fixture.Operations,
            accepted.OperationId,
            OperationState.Failed);

        Assert.Equal(
            ErrorCodes.BuildFailed,
            completed.Summary.Error?.Code);
        BuildResult result =
            Assert.IsType<BuildResult>(completed.Result);
        Assert.False(result.Ok);
        Assert.Equal(1, result.Counts.Errors);
        Assert.False(fixture.Service.GetStatus().LastBuild?.Ok);
        Assert.Null(fixture.Service.GetStatus().CurrentOperation);
        Assert.Equal(
            GatewayState.Ready,
            fixture.Service.GetStatus().Gateway.State);
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
            Func<GatewayDiagnosticsResult>? diagnosticsProvider = null,
            BuildOperationExecutor? buildExecutor = null)
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
                diagnosticsProvider,
                buildExecutor);
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

        throw new TimeoutException(
            $"Operation '{operationId}' did not reach {expected}.");
    }
}
