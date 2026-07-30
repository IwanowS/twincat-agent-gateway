using System;
using System.IO;
using System.Linq;
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
    public void CurrentGatewayLogResourceReturnsTrackedAbsolutePath()
    {
        const string currentPath =
            @"C:\GatewayLogs\gateway-20260729T063245123Z-p1234_001.ndjson";
        using ServiceFixture fixture = new(
            currentLogPathProvider: () => currentPath);

        ResourceContent resource = fixture.Service.GetResource(
            GatewayResourceUris.CurrentGatewayLog,
            1024,
            offset: 0);

        Assert.Equal(
            GatewayResourceUris.CurrentGatewayLog,
            resource.Uri);
        Assert.Equal("text/plain", resource.ContentType);
        Assert.Equal(currentPath, resource.Content);
        Assert.Equal(0, resource.Offset);
        Assert.Null(resource.NextOffset);
        Assert.False(resource.Truncated);
    }

    [Fact]
    public void CurrentGatewayLogResourceFailsWhenTrackerIsUnavailable()
    {
        using ServiceFixture fixture = new();

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => fixture.Service.GetResource(
                    GatewayResourceUris.CurrentGatewayLog,
                    1024,
                    offset: 0));

        Assert.Equal(ErrorCodes.GatewayNotRunning, exception.Code);
    }

    [Fact]
    public void CurrentGatewayLogResourceDoesNotCacheRolloverPath()
    {
        string currentPath =
            @"C:\GatewayLogs\gateway-20260729T063245123Z-p1234.ndjson";
        using ServiceFixture fixture = new(
            currentLogPathProvider: () => currentPath);

        ResourceContent initial = fixture.Service.GetResource(
            GatewayResourceUris.CurrentGatewayLog,
            1024,
            offset: 0);
        currentPath =
            @"C:\GatewayLogs\gateway-20260729T063245123Z-p1234_001.ndjson";
        ResourceContent rolled = fixture.Service.GetResource(
            GatewayResourceUris.CurrentGatewayLog,
            1024,
            offset: 0);

        Assert.EndsWith(
            "-p1234.ndjson",
            initial.Content,
            StringComparison.Ordinal);
        Assert.EndsWith(
            "-p1234_001.ndjson",
            rolled.Content,
            StringComparison.Ordinal);
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

    [Theory]
    [InlineData(-1, 50)]
    [InlineData(0, 0)]
    [InlineData(0, 201)]
    public void DiagnosticsRejectInvalidEventCursorRequests(
        long afterEventCursor,
        int maximumEvents)
    {
        using ServiceFixture fixture = new();

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => fixture.Service.GetDiagnostics(
                    new GetDiagnosticsParameters
                    {
                        AfterEventCursor = afterEventCursor,
                        MaximumEvents = maximumEvents,
                    }));

        Assert.Equal(ErrorCodes.RequestInvalid, exception.Code);
        Assert.Equal("diagnostics.validate", exception.Stage);
    }

    [Fact]
    public void DiagnosticsRequireStreamIdWhenContinuingFromCursor()
    {
        using ServiceFixture fixture = new();

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => fixture.Service.GetDiagnostics(
                    new GetDiagnosticsParameters
                    {
                        AfterEventCursor = 1,
                    }));

        Assert.Equal(ErrorCodes.RequestInvalid, exception.Code);
        Assert.Equal("diagnostics.validate", exception.Stage);
    }

    [Fact]
    public async Task XaeMessagesUsesBoundedProvider()
    {
        GetXaeMessagesParameters? captured = null;
        using ServiceFixture fixture = new(
            xaeMessagesProvider: (
                parameters,
                cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                captured = parameters;
                return Task.FromResult(
                    new XaeMessagesResult
                    {
                        Solution =
                            @"C:\Project\Fixture.sln",
                    });
            });

        XaeMessagesResult result =
            await fixture.Service.GetXaeMessagesAsync(
                new GetXaeMessagesParameters
                {
                    MaximumMessages = 7,
                },
                CancellationToken.None);

        Assert.Equal(7, captured?.MaximumMessages);
        Assert.Equal(
            @"C:\Project\Fixture.sln",
            result.Solution);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task XaeMessagesRejectsInvalidLimit(
        int maximumMessages)
    {
        using ServiceFixture fixture = new(
            xaeMessagesProvider: (
                parameters,
                cancellationToken) =>
                    Task.FromResult(
                        new XaeMessagesResult()));

        GatewayOperationException exception =
            await Assert.ThrowsAsync<
                GatewayOperationException>(
                () => fixture.Service.GetXaeMessagesAsync(
                    new GetXaeMessagesParameters
                    {
                        MaximumMessages =
                            maximumMessages,
                    },
                    CancellationToken.None));

        Assert.Equal(
            ErrorCodes.RequestInvalid,
            exception.Code);
        Assert.Equal(
            "xae.errorList.validate",
            exception.Stage);
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
                        Log = new ResourceReference
                        {
                            Uri = "twincat-log://operation-placeholder/build",
                            OperationId = "operation-placeholder",
                            Kind = ResourceKind.BuildLog,
                        },
                        ExpectedProjectNoise =
                        {
                            new ProjectChangeSummary
                            {
                                File = "Machine.tsproj",
                                Classification =
                                    ProjectChangeClassification
                                        .ExpectedReorderOnly,
                                DoNotInspectFullFile = true,
                                Details = new ResourceReference
                                {
                                    Uri = "twincat-diff://"
                                        + "operation-placeholder/"
                                        + "project-noise",
                                    OperationId =
                                        "operation-placeholder",
                                    Kind = ResourceKind.ProjectNoise,
                                },
                            },
                        },
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
        Assert.Equal(
            new[]
            {
                ResourceKind.BuildLog,
                ResourceKind.ProjectNoise,
            },
            completed.Summary.Resources.Select(
                resource => resource.Kind));
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
                        Log = new ResourceReference
                        {
                            Uri = "twincat-log://operation-placeholder/build",
                            OperationId = "operation-placeholder",
                            Kind = ResourceKind.BuildLog,
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
        Assert.Equal(
            "twincat-log://operation-placeholder/build",
            completed.Summary.Error?.RawLogRef);
        Assert.Equal(
            ResourceKind.BuildLog,
            Assert.Single(completed.Summary.Resources).Kind);
        BuildResult result =
            Assert.IsType<BuildResult>(completed.Result);
        Assert.False(result.Ok);
        Assert.Equal(1, result.Counts.Errors);
        Assert.False(fixture.Service.GetStatus().LastBuild?.Ok);
        Assert.Null(fixture.Service.GetStatus().CurrentOperation);
        Assert.Equal(
            3,
            fixture.Service.GetStatus().LatestEventCursor);
        GatewayEvent[] lifecycle = fixture.Service.GetDiagnostics(
            new GetDiagnosticsParameters
            {
                AfterEventCursor = 0,
            }).Events.ToArray();
        Assert.Equal(
            new[]
            {
                GatewayEventTypes.BuildQueued,
                GatewayEventTypes.BuildStarted,
                GatewayEventTypes.BuildFailed,
            },
            lifecycle.Select(gatewayEvent =>
                gatewayEvent.Type));
        GatewayEvent gatewayEvent = lifecycle[2];
        Assert.Equal(
            ErrorCodes.BuildFailed,
            gatewayEvent.Error?.Code);
        Assert.Equal(
            accepted.OperationId,
            gatewayEvent.Error?.OperationId);
        Assert.Equal(
            GatewayState.Ready,
            fixture.Service.GetStatus().Gateway.State);
    }

    [Fact]
    public void AgentSynchronizationRequiresProfilePermission()
    {
        using ServiceFixture fixture = new(
            activeProfile: new ProjectProfile
            {
                Name = "fixture",
                AllowForceSynchronization = false,
            },
            synchronizeExecutor: (
                operationId,
                parameters,
                cancellationToken) =>
                Task.FromResult(new SynchronizeResult
                {
                    Ok = true,
                }));

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => fixture.Service.StartSynchronization(
                    new SynchronizeParameters
                    {
                        Profile = "fixture",
                    },
                    agentRequest: true));

        Assert.Equal(
            ErrorCodes.ForceSynchronizationNotAllowed,
            exception.Code);
    }

    [Fact]
    public async Task UiSynchronizationCanConfirmBaseline()
    {
        using ServiceFixture fixture = new(
            activeProfile: new ProjectProfile
            {
                Name = "fixture",
            },
            synchronizeExecutor: (
                operationId,
                parameters,
                cancellationToken) =>
                Task.FromResult(new SynchronizeResult
                {
                    Ok = true,
                    Scope = SynchronizationScope.TwinCatProject,
                }));

        OperationAccepted accepted =
            fixture.Service.StartSynchronization(
                new SynchronizeParameters
                {
                    Profile = "fixture",
                },
                agentRequest: false);
        StoredOperation completed = await WaitForStateAsync(
            fixture.Operations,
            accepted.OperationId,
            OperationState.Succeeded);

        Assert.IsType<SynchronizeResult>(completed.Result);
        Assert.Equal(
            SynchronizationState.Confirmed,
            fixture.Service.GetStatus()
                .Xae.SynchronizationState);
    }

    [Fact]
    public void CloseXaeRequiresProfilePermission()
    {
        using ServiceFixture fixture = new(
            activeProfile: new ProjectProfile
            {
                Name = "fixture",
                AllowCloseXae = false,
            },
            closeXaeExecutor: SuccessfulCloseXae);

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => fixture.Service.StartCloseXae(
                    new CloseXaeParameters
                    {
                        SaveMode = XaeSaveMode.Prompt,
                    }));

        Assert.Equal(
            ErrorCodes.XaeCloseNotAllowed,
            exception.Code);
    }

    [Fact]
    public void CloseXaeDiscardRequiresDiscardPermission()
    {
        using ServiceFixture fixture = new(
            activeProfile: new ProjectProfile
            {
                Name = "fixture",
                AllowCloseXae = true,
                AllowDirtyDocumentDiscard = false,
            },
            closeXaeExecutor: SuccessfulCloseXae);

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => fixture.Service.StartCloseXae(
                    new CloseXaeParameters
                    {
                        SaveMode = XaeSaveMode.Discard,
                    }));

        Assert.Equal(
            ErrorCodes.XaeCloseDiscardNotAllowed,
            exception.Code);
    }

    [Fact]
    public async Task CloseXaePublishesTypedLifecycle()
    {
        using ServiceFixture fixture = new(
            activeProfile: new ProjectProfile
            {
                Name = "fixture",
                AllowCloseXae = true,
            },
            closeXaeExecutor: SuccessfulCloseXae);
        fixture.Status.Update(status =>
        {
            status.Gateway.State = GatewayState.Ready;
            status.Xae.Connected = true;
            return status;
        });

        OperationAccepted accepted =
            fixture.Service.StartCloseXae(
                new CloseXaeParameters
                {
                    SaveMode = XaeSaveMode.Save,
                });
        StoredOperation completed = await WaitForStateAsync(
            fixture.Operations,
            accepted.OperationId,
            OperationState.Succeeded);

        CloseXaeResult result =
            Assert.IsType<CloseXaeResult>(completed.Result);
        Assert.True(result.Ok);
        Assert.Equal(accepted.OperationId, result.OperationId);
        Assert.Equal(
            new[]
            {
                GatewayEventTypes.XaeCloseQueued,
                GatewayEventTypes.XaeCloseStarted,
                GatewayEventTypes.XaeCloseSucceeded,
            },
            fixture.Service.GetDiagnostics(
                    new GetDiagnosticsParameters())
                .Events.Select(gatewayEvent =>
                    gatewayEvent.Type));
    }

    [Fact]
    public void ActivationFailsClosedWhenProfileDisablesIt()
    {
        ProjectProfile profile = CreateActivationProfile();
        profile.AllowActivation = false;
        using ServiceFixture fixture = new(
            activationExecutor: SuccessfulActivation,
            activeProfile: profile);

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => fixture.Service.StartActivation(
                    new ActivateParameters
                    {
                        Profile = profile.Name,
                    }));

        Assert.Equal(
            ErrorCodes.ActivationNotAllowed,
            exception.Code);
        Assert.Empty(fixture.Operations.GetRecent(10));
    }

    [Fact]
    public void RuntimeExceptionPrecedesRecentBuildValidation()
    {
        ProjectProfile profile = CreateActivationProfile();
        using ServiceFixture fixture = new(
            activationExecutor: SuccessfulActivation,
            activeProfile: profile);
        fixture.Status.Update(status =>
        {
            status.TwinCat.Mode = RuntimeMode.Exception;
            status.TwinCat.SystemMode = RuntimeMode.Exception;
            return status;
        });

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => fixture.Service.StartActivation(
                    new ActivateParameters
                    {
                        Profile = profile.Name,
                    }));

        Assert.Equal(
            ErrorCodes.RuntimeRecoveryRequired,
            exception.Code);
        Assert.Equal(
            "activation.runtimePreflight",
            exception.Stage);
        Assert.Empty(fixture.Operations.GetRecent(10));
    }

    [Fact]
    public async Task RecoveryIsExplicitAndDoesNotRequireRecentBuild()
    {
        ProjectProfile profile = CreateActivationProfile();
        using ServiceFixture fixture = new(
            activeProfile: profile,
            recoveryExecutor: (
                operationId,
                parameters,
                cancellationToken) =>
                Task.FromResult(
                    new RecoverToConfigResult
                    {
                        Ok = true,
                        Profile = parameters.Profile,
                        InitialRuntimeMode =
                            RuntimeMode.Exception,
                        ObservedRuntimeMode =
                            RuntimeMode.Config,
                        TransitionRequested = true,
                    }));
        fixture.Status.Update(status =>
        {
            status.Xae.Connected = true;
            status.Gateway.State = GatewayState.Ready;
            return status;
        });

        OperationAccepted accepted =
            fixture.Service.StartRecoverToConfig(
                new RecoverToConfigParameters
                {
                    Profile = profile.Name,
                    TimeoutSeconds = 10,
                });
        StoredOperation completed = await WaitForStateAsync(
            fixture.Operations,
            accepted.OperationId,
            OperationState.Succeeded);

        RecoverToConfigResult result =
            Assert.IsType<RecoverToConfigResult>(
                completed.Result);
        Assert.True(result.TransitionRequested);
        Assert.Equal(
            RuntimeMode.Config,
            result.ObservedRuntimeMode);
        Assert.Equal(
            new[]
            {
                GatewayEventTypes.RecoveryQueued,
                GatewayEventTypes.RecoveryStarted,
                GatewayEventTypes.RecoverySucceeded,
            },
            fixture.Events
                .ReadAfter(null, 0, 100)
                .Events
                .Where(gatewayEvent =>
                    gatewayEvent.OperationId
                    == accepted.OperationId)
                .Select(gatewayEvent =>
                    gatewayEvent.Type));
        Assert.Equal(
            GatewayState.Ready,
            fixture.Service.GetStatus().Gateway.State);
    }

    [Fact]
    public void RecoveryFailsClosedWhenProfileDisablesActivation()
    {
        ProjectProfile profile = CreateActivationProfile();
        profile.AllowActivation = false;
        using ServiceFixture fixture = new(
            activeProfile: profile,
            recoveryExecutor: (
                operationId,
                parameters,
                cancellationToken) =>
                Task.FromResult(
                    new RecoverToConfigResult
                    {
                        Ok = true,
                    }));

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => fixture.Service.StartRecoverToConfig(
                    new RecoverToConfigParameters
                    {
                        Profile = profile.Name,
                    }));

        Assert.Equal(
            ErrorCodes.ActivationNotAllowed,
            exception.Code);
        Assert.Empty(fixture.Operations.GetRecent(10));
    }

    [Fact]
    public async Task RecoveryTimeoutUsesRecoveryLifecycle()
    {
        ProjectProfile profile = CreateActivationProfile();
        using ServiceFixture fixture = new(
            activeProfile: profile,
            recoveryExecutor: async (
                operationId,
                parameters,
                cancellationToken) =>
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
                return new RecoverToConfigResult
                {
                    Ok = true,
                };
            });

        OperationAccepted accepted =
            fixture.Service.StartRecoverToConfig(
                new RecoverToConfigParameters
                {
                    Profile = profile.Name,
                    TimeoutSeconds = 1,
                });
        StoredOperation completed = await WaitForStateAsync(
            fixture.Operations,
            accepted.OperationId,
            OperationState.TimedOut);

        Assert.Equal(
            ErrorCodes.OperationTimeout,
            completed.Summary.Error?.Code);
        Assert.Equal(
            GatewayEventTypes.RecoveryTimedOut,
            fixture.Events
                .ReadAfter(null, 0, 100)
                .Events
                .Last(gatewayEvent =>
                    gatewayEvent.OperationId
                    == accepted.OperationId)
                .Type);
    }

    [Fact]
    public void ActivationRequiresLatestBuildToBeRecentAndUsable()
    {
        DateTimeOffset now = new(
            2026,
            7,
            28,
            2,
            0,
            0,
            TimeSpan.Zero);
        ProjectProfile profile = CreateActivationProfile();
        using ServiceFixture fixture = new(
            activationExecutor: SuccessfulActivation,
            activeProfile: profile,
            clock: new TestClock(now));
        SeedBuild(
            fixture.Operations,
            "build-success",
            BuildAction.Build,
            now.AddMinutes(-1));
        SeedBuild(
            fixture.Operations,
            "clean-latest",
            BuildAction.Clean,
            now.AddSeconds(-10));

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => fixture.Service.StartActivation(
                    new ActivateParameters
                    {
                        Profile = profile.Name,
                    }));

        Assert.Equal(
            ErrorCodes.RecentBuildRequired,
            exception.Code);
    }

    [Fact]
    public async Task ActivationCompileFailureIsNotActivated()
    {
        DateTimeOffset now = new(
            2026,
            7,
            28,
            2,
            0,
            0,
            TimeSpan.Zero);
        ProjectProfile profile = CreateActivationProfile();
        const string buildLog =
            "twincat-log://operation-placeholder/build";
        using ServiceFixture fixture = new(
            activationExecutor: (
                operationId,
                parameters,
                cancellationToken) =>
                Task.FromResult(
                    new ActivationResult
                    {
                        Ok = false,
                        OperationId = operationId,
                        Profile = parameters.Profile,
                        Completion = ActivationCompletion.Unknown,
                        ActiveConfigurationVerified = false,
                        Compile = new ActivationCompileResult
                        {
                            Completed = true,
                            Ok = false,
                            FailedProjects = 1,
                            Counts = new DiagnosticCounts
                            {
                                Errors = 1,
                            },
                            Diagnostics =
                            {
                                new BuildDiagnostic
                                {
                                    Severity =
                                        DiagnosticSeverity.Error,
                                    Code = "C0001",
                                    Message = "Expected ';'.",
                                },
                            },
                            Log = new ResourceReference
                            {
                                Uri = buildLog,
                                OperationId = operationId,
                                Kind = ResourceKind.BuildLog,
                            },
                        },
                        Resources =
                        {
                            new ResourceReference
                            {
                                Uri = buildLog,
                                OperationId = operationId,
                                Kind = ResourceKind.BuildLog,
                            },
                        },
                    }),
            activeProfile: profile,
            clock: new TestClock(now));
        SeedBuild(
            fixture.Operations,
            "build-success",
            BuildAction.Build,
            now.AddMinutes(-1));
        fixture.Status.Update(status =>
        {
            status.Xae.Connected = true;
            status.Gateway.State = GatewayState.Ready;
            return status;
        });

        OperationAccepted accepted =
            fixture.Service.StartActivation(
                new ActivateParameters
                {
                    Profile = profile.Name,
                });
        StoredOperation completed = await WaitForStateAsync(
            fixture.Operations,
            accepted.OperationId,
            OperationState.Failed);

        Assert.Equal(
            ErrorCodes.BuildFailed,
            completed.Summary.Error?.Code);
        Assert.Equal(
            "activation.compile",
            completed.Summary.Error?.Stage);
        Assert.Equal(
            buildLog,
            completed.Summary.Error?.RawLogRef);
        ActivationResult result =
            Assert.IsType<ActivationResult>(completed.Result);
        Assert.False(result.Ok);
        Assert.Equal(
            ActivationCompletion.Unknown,
            result.Completion);
        Assert.False(result.ActiveConfigurationVerified);
        Assert.False(result.Compile?.Ok);
        Assert.Equal(
            GatewayEventTypes.ActivationFailed,
            fixture.Events
                .ReadAfter(null, 0, 100)
                .Events
                .Last(gatewayEvent =>
                    gatewayEvent.OperationId
                    == accepted.OperationId)
                .Type);
    }

    [Fact]
    public async Task ActivationPublishesLifecycleAndSummary()
    {
        DateTimeOffset now = new(
            2026,
            7,
            28,
            2,
            0,
            0,
            TimeSpan.Zero);
        ProjectProfile profile = CreateActivationProfile();
        using ServiceFixture fixture = new(
            activationExecutor: SuccessfulActivation,
            activeProfile: profile,
            clock: new TestClock(now));
        SeedBuild(
            fixture.Operations,
            "build-success",
            BuildAction.Build,
            now.AddMinutes(-1));
        fixture.Status.Update(status =>
        {
            status.Xae.Connected = true;
            status.Gateway.State = GatewayState.Ready;
            return status;
        });

        OperationAccepted accepted =
            fixture.Service.StartActivation(
                new ActivateParameters
                {
                    Profile = profile.Name,
                    RunAfterActivation = false,
                });
        StoredOperation completed = await WaitForStateAsync(
            fixture.Operations,
            accepted.OperationId,
            OperationState.Succeeded);

        ActivationResult result =
            Assert.IsType<ActivationResult>(completed.Result);
        Assert.True(result.Ok);
        Assert.False(result.RunAfterActivation);
        Assert.Equal(
            ActivationCompletion.RestartSkipped,
            result.Completion);
        Assert.False(result.ActiveConfigurationVerified);
        Assert.Equal(
            accepted.OperationId,
            fixture.Service.GetStatus()
                .LastActivation?.OperationId);
        Assert.Equal(
            "192.168.3.31.1.1",
            fixture.Service.GetStatus()
                .LastActivation?.Target.AmsNetId);
        Assert.Equal(
            new[]
            {
                GatewayEventTypes.ActivationQueued,
                GatewayEventTypes.ActivationStarted,
                GatewayEventTypes.ActivationSucceeded,
            },
            fixture.Events
                .ReadAfter(null, 0, 100)
                .Events
                .Where(gatewayEvent =>
                    gatewayEvent.OperationId
                        == accepted.OperationId)
                .Select(gatewayEvent =>
                    gatewayEvent.Type));
    }

    [Fact]
    public async Task ActivationLinksASeparateTcUnitOperation()
    {
        DateTimeOffset now = new(
            2026,
            7,
            28,
            3,
            0,
            0,
            TimeSpan.Zero);
        ProjectProfile profile = CreateActivationProfile();
        profile.AutoWaitForTcUnit = true;
        profile.TcUnit = new TcUnitProfile
        {
            ReportPath = @"C:\Reports\tcunit.xml",
            CompletionTimeoutSeconds = 30,
        };
        string? preparedFor = null;
        string? executedFor = null;
        using ServiceFixture fixture = new(
            activationExecutor: SuccessfulActivation,
            activeProfile: profile,
            clock: new TestClock(now),
            tcUnitPreparationExecutor:
                activationOperationId =>
                {
                    preparedFor = activationOperationId;
                    return new TcUnitRunPreparation
                    {
                        ActivationOperationId =
                            activationOperationId,
                        ExpectedAmsNetId =
                            "192.168.3.31.1.1",
                        PreparedAtUtc = now,
                        ReportBaseline =
                            new TcUnitReportBaseline
                            {
                                Path =
                                    @"C:\Reports\tcunit.xml",
                            },
                    };
                },
            tcUnitExecutor:
                (
                    testOperationId,
                    activationOperationId,
                    preparation,
                    cancellationToken) =>
                {
                    executedFor = activationOperationId;
                    return Task.FromResult(
                        new TestResult
                        {
                            Ok = true,
                            Counts = new TestCounts
                            {
                                Suites = 1,
                                Tests = 2,
                                Passed = 2,
                            },
                            InitializedSuites = 1,
                        });
                });
        SeedBuild(
            fixture.Operations,
            "build-success",
            BuildAction.Build,
            now.AddMinutes(-1));
        fixture.Status.Update(status =>
        {
            status.Xae.Connected = true;
            status.Gateway.State = GatewayState.Ready;
            return status;
        });

        OperationAccepted accepted =
            fixture.Service.StartActivation(
                new ActivateParameters
                {
                    Profile = profile.Name,
                });
        StoredOperation activation =
            await WaitForStateAsync(
                fixture.Operations,
                accepted.OperationId,
                OperationState.Succeeded);
        ActivationResult activationResult =
            Assert.IsType<ActivationResult>(
                activation.Result);
        Assert.False(
            string.IsNullOrWhiteSpace(
                activationResult.TestOperationId));
        StoredOperation test =
            await WaitForStateAsync(
                fixture.Operations,
                activationResult.TestOperationId!,
                OperationState.Succeeded);

        TestResult result =
            Assert.IsType<TestResult>(test.Result);
        Assert.Equal(
            accepted.OperationId,
            preparedFor);
        Assert.Equal(
            accepted.OperationId,
            executedFor);
        Assert.Equal(
            accepted.OperationId,
            result.ActivationOperationId);
        Assert.Equal(
            activationResult.TestOperationId,
            result.OperationId);
        Assert.Equal(
            activationResult.TestOperationId,
            fixture.Service.GetStatus()
                .LastTest?.OperationId);
        Assert.Equal(
            new[]
            {
                GatewayEventTypes.TcUnitQueued,
                GatewayEventTypes.TcUnitStarted,
                GatewayEventTypes.TcUnitSucceeded,
            },
            fixture.Events.ReadAfter(null, 0, 100)
                .Events
                .Where(gatewayEvent =>
                    gatewayEvent.OperationId
                        == activationResult
                            .TestOperationId)
                .Select(gatewayEvent =>
                    gatewayEvent.Type));
        OperationDetails<TestResult> queried =
            fixture.Service.GetTestResults(
                activationResult.TestOperationId!);
        Assert.True(queried.Result?.Ok);
    }

    [Fact]
    public void ActivationWithoutRunRejectsTcUnitWaiting()
    {
        ProjectProfile profile = CreateActivationProfile();
        profile.AutoWaitForTcUnit = true;
        profile.TcUnit = new TcUnitProfile
        {
            ReportPath = @"C:\Reports\tcunit.xml",
        };
        using ServiceFixture fixture = new(
            activationExecutor: SuccessfulActivation,
            activeProfile: profile);

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => fixture.Service.StartActivation(
                    new ActivateParameters
                    {
                        Profile = profile.Name,
                        RunAfterActivation = false,
                    }));

        Assert.Equal(ErrorCodes.RequestInvalid, exception.Code);
        Assert.Equal("activation.validate", exception.Stage);
    }

    [Fact]
    public void LinkedTcUnitFailsClosedWhenExecutorUnavailable()
    {
        DateTimeOffset now = new(
            2026,
            7,
            28,
            3,
            0,
            0,
            TimeSpan.Zero);
        ProjectProfile profile = CreateActivationProfile();
        profile.AutoWaitForTcUnit = true;
        profile.TcUnit = new TcUnitProfile
        {
            ReportPath = @"C:\Reports\tcunit.xml",
        };
        using ServiceFixture fixture = new(
            activationExecutor: SuccessfulActivation,
            activeProfile: profile,
            clock: new TestClock(now));
        SeedBuild(
            fixture.Operations,
            "build-success",
            BuildAction.Build,
            now.AddMinutes(-1));

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => fixture.Service.StartActivation(
                    new ActivateParameters
                    {
                        Profile = profile.Name,
                    }));

        Assert.Equal(
            ErrorCodes.GatewayNotReady,
            exception.Code);
        Assert.Equal(
            "activation.tcunit",
            exception.Stage);
        Assert.DoesNotContain(
            fixture.Operations.GetRecent(10),
            operation =>
                operation.Summary.Kind
                    == OperationKind.Activate);
    }

    private static Task<ActivationResult> SuccessfulActivation(
        string operationId,
        ActivateParameters parameters,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(
            new ActivationResult
            {
                Ok = true,
                OperationId = operationId,
                Profile = parameters.Profile,
                RunAfterActivation =
                    parameters.RunAfterActivation,
                Completion = parameters.RunAfterActivation
                    ? ActivationCompletion.AppliedAndRunning
                    : ActivationCompletion.RestartSkipped,
                ActiveConfigurationVerified =
                    parameters.RunAfterActivation,
                ObservedRuntimeMode = parameters.RunAfterActivation
                    ? RuntimeMode.Run
                    : RuntimeMode.Unknown,
                Solution =
                    @"C:\Projects\Machine\Machine.sln",
                Target = new TargetIdentity
                {
                    Name = "WIN-T077ADA",
                    AmsNetId = "192.168.3.31.1.1",
                },
            });
    }

    private static ProjectProfile CreateActivationProfile()
    {
        return new ProjectProfile
        {
            Name = "bench",
            Solution = @"C:\Projects\Machine\Machine.sln",
            AllowActivation = true,
            ExpectedTarget = new TargetIdentity
            {
                Name = "WIN-T077ADA",
                AmsNetId = "192.168.3.31.1.1",
            },
            RequireRecentSuccessfulBuild = true,
            RecentBuildMaxAgeSeconds = 600,
        };
    }

    private static void SeedBuild(
        OperationStore operations,
        string operationId,
        BuildAction action,
        DateTimeOffset completedAtUtc)
    {
        operations.AddQueued(
            operationId,
            OperationKind.Build,
            completedAtUtc.AddSeconds(-2));
        operations.TryMarkRunning(
            operationId,
            completedAtUtc.AddSeconds(-1));
        operations.TryComplete(
            operationId,
            OperationState.Succeeded,
            completedAtUtc,
            new BuildResult
            {
                Ok = true,
                OperationId = operationId,
                Action = action,
            });
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

    private static Task<CloseXaeResult> SuccessfulCloseXae(
        string operationId,
        CloseXaeParameters parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            new CloseXaeResult
            {
                Ok = true,
                OperationId = operationId,
                SaveMode = parameters.SaveMode,
                ProcessExited = true,
            });
    }

    private sealed class ServiceFixture : IDisposable
    {
        private readonly string _temporaryDirectory;

        public ServiceFixture(
            Func<GatewayDiagnosticsResult>? diagnosticsProvider = null,
            BuildOperationExecutor? buildExecutor = null,
            ActivationOperationExecutor? activationExecutor = null,
            ProjectProfile? activeProfile = null,
            IClock? clock = null,
            TcUnitPreparationExecutor?
                tcUnitPreparationExecutor = null,
            TcUnitOperationExecutor? tcUnitExecutor = null,
            SynchronizeOperationExecutor? synchronizeExecutor = null,
            RecoveryOperationExecutor? recoveryExecutor = null,
            XaeMessagesProvider? xaeMessagesProvider = null,
            Func<string?>? currentLogPathProvider = null,
            CloseXaeOperationExecutor? closeXaeExecutor = null)
        {
            _temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                "TwinCatGatewayTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temporaryDirectory);
            Status = new GatewayStatusSnapshotStore(
                GatewayStatusSnapshotStore.CreateInitial("0.1.0"));
            Events = new GatewayEventJournal(Status);
            Operations = new OperationStore();
            Logs = new LocalLogStore(_temporaryDirectory);
            Queue = new OperationQueue(
                Operations,
                clock: clock,
                gatewayEventSink: Events);
            Service = new GatewayApplicationService(
                "0.1.0",
                Status,
                Operations,
                Queue,
                Logs,
                Events,
                diagnosticsProvider,
                buildExecutor,
                activationExecutor,
                activeProfile,
                clock,
                tcUnitPreparationExecutor,
                tcUnitExecutor,
                synchronizeExecutor,
                recoveryExecutor,
                xaeMessagesProvider,
                currentLogPathProvider,
                closeXaeExecutor);
        }

        public GatewayStatusSnapshotStore Status { get; }

        public OperationStore Operations { get; }

        public GatewayEventJournal Events { get; }

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

    private sealed class TestClock : IClock
    {
        public TestClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
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
