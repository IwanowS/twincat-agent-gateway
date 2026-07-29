using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Desktop;
using TwinCatGateway.Ipc;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class DesktopGatewayActivationTests
{
    [XaeFact]
    public async Task ActivationRejectsMismatchedTargetThroughIpc()
    {
        using TemporaryDirectory temporary = new();
        string solutionPath = GetSolutionPath();
        string pipeName = CreatePipeName();
        string configurationPath = WriteConfiguration(
            temporary.Path,
            pipeName,
            solutionPath,
            "127.0.0.1.1.1",
            requireRecentBuild: false);
        using GatewayDesktopHost host = StartHost(
            configurationPath);
        await WaitForStateAsync(
            host,
            GatewayState.Ready,
            TimeSpan.FromSeconds(15));
        NamedPipeGatewayClient client = new(pipeName);

        OperationAccepted accepted = await StartActivationAsync(
            client,
            TimeSpan.FromSeconds(30));
        OperationDetails<ActivationResult> completed =
            await WaitForOperationAsync<ActivationResult>(
                client,
                accepted.OperationId,
                TimeSpan.FromSeconds(45));
        GatewayDiagnosticsResult diagnostics =
            host.ApplicationService.GetDiagnostics();
        int processId = GetSelectedProcessId(diagnostics);

        await host.StopAsync();

        Assert.Equal(
            OperationState.Failed,
            completed.Operation.State);
        Assert.Equal(
            ErrorCodes.ActivationTargetMismatch,
            completed.Operation.Error?.Code);
        Assert.Equal(
            new[]
            {
                GatewayEventTypes.ActivationQueued,
                GatewayEventTypes.ActivationStarted,
                GatewayEventTypes.ActivationFailed,
            },
            GetOperationEventTypes(
                diagnostics,
                accepted.OperationId));
        Assert.Empty(
            XaeWindowProbe.FindModalDialogs(processId));
    }

    [RemoteActivationFact]
    public async Task ActivationBuildsAndRunsRemoteTargetThroughIpc()
    {
        using TemporaryDirectory temporary = new();
        string solutionPath = GetSolutionPath();
        string expectedAmsNetId =
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_REMOTE_AMS_NET_ID")!;
        string pipeName = CreatePipeName();
        string configurationPath = WriteConfiguration(
            temporary.Path,
            pipeName,
            solutionPath,
            expectedAmsNetId,
            requireRecentBuild: true);
        using GatewayDesktopHost host = StartHost(
            configurationPath);
        await WaitForStateAsync(
            host,
            GatewayState.Ready,
            TimeSpan.FromSeconds(15));
        NamedPipeGatewayClient client = new(pipeName);
        await SynchronizeDiskAsync(host, client);

        GatewayResponse<OperationAccepted> buildAccepted =
            await client.SendAsync<
                BuildParameters,
                OperationAccepted>(
                GatewayMethods.Build,
                new BuildParameters
                {
                    Profile = "fixture",
                    Action = BuildAction.Build,
                    TimeoutSeconds = 90,
                },
                wait: false,
                CancellationToken.None);
        Assert.True(buildAccepted.Ok);
        Assert.NotNull(buildAccepted.Result);
        OperationDetails<BuildResult> build =
            await WaitForOperationAsync<BuildResult>(
                client,
                buildAccepted.Result!.OperationId,
                TimeSpan.FromSeconds(105));
        Assert.Equal(
            OperationState.Succeeded,
            build.Operation.State);
        Assert.True(build.Result?.Ok);

        OperationAccepted accepted = await StartActivationAsync(
            client,
            TimeSpan.FromSeconds(180));
        OperationDetails<ActivationResult> completed =
            await WaitForOperationAsync<ActivationResult>(
                client,
                accepted.OperationId,
                TimeSpan.FromSeconds(195));
        GatewayDiagnosticsResult diagnostics =
            host.ApplicationService.GetDiagnostics();
        int processId = GetSelectedProcessId(diagnostics);

        await host.StopAsync();

        Assert.Equal(
            OperationState.Succeeded,
            completed.Operation.State);
        Assert.True(completed.Result?.Ok);
        Assert.Equal(
            expectedAmsNetId,
            completed.Result?.Target.AmsNetId);
        Assert.Null(completed.Result?.Target.Name);
        Assert.Equal(
            RuntimeMode.Run,
            diagnostics.Status.TwinCat.Mode);
        Assert.Equal(
            ResourceKind.ActivationLog,
            Assert.Single(
                completed.Result!.Resources).Kind);
        Assert.Equal(
            new[]
            {
                GatewayEventTypes.ActivationQueued,
                GatewayEventTypes.ActivationStarted,
                GatewayEventTypes.ActivationConfigurationStarted,
                GatewayEventTypes.ActivationConfigurationActivated,
                GatewayEventTypes.ActivationRuntimeReady,
                GatewayEventTypes.ActivationSucceeded,
            },
            GetOperationEventTypes(
                diagnostics,
                accepted.OperationId));
        Assert.Empty(
            XaeWindowProbe.FindModalDialogs(processId));
    }

    [RemoteActivationLaunchFact]
    public async Task ActivationBuildsWithoutRunTransitionThroughIpc()
    {
        using TemporaryDirectory temporary = new();
        string solutionPath = GetSolutionPath();
        string expectedAmsNetId =
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_REMOTE_AMS_NET_ID")!;
        string pipeName = CreatePipeName();
        string configurationPath = WriteConfiguration(
            temporary.Path,
            pipeName,
            solutionPath,
            expectedAmsNetId,
            requireRecentBuild: true,
            allowXaeLaunch: true);
        using GatewayDesktopHost host = StartHost(
            configurationPath);
        using GatewayOwnedXaeCleanup cleanup = new(host);
        await WaitForStateAsync(
            host,
            GatewayState.Ready,
            TimeSpan.FromSeconds(60));
        NamedPipeGatewayClient client = new(pipeName);
        await SynchronizeDiskAsync(host, client);

        GatewayResponse<OperationAccepted> buildAccepted =
            await client.SendAsync<
                BuildParameters,
                OperationAccepted>(
                GatewayMethods.Build,
                new BuildParameters
                {
                    Profile = "fixture",
                    Action = BuildAction.Rebuild,
                    TimeoutSeconds = 90,
                },
                wait: false,
                CancellationToken.None);
        Assert.True(buildAccepted.Ok);
        OperationDetails<BuildResult> build =
            await WaitForOperationAsync<BuildResult>(
                client,
                Assert.IsType<OperationAccepted>(
                    buildAccepted.Result).OperationId,
                TimeSpan.FromSeconds(105));
        Assert.Equal(
            OperationState.Succeeded,
            build.Operation.State);
        Assert.True(build.Result?.Ok);

        OperationAccepted accepted = await StartActivationAsync(
            client,
            TimeSpan.FromSeconds(180),
            runAfterActivation: false);
        OperationDetails<ActivationResult> completed =
            await WaitForOperationAsync<ActivationResult>(
                client,
                accepted.OperationId,
                TimeSpan.FromSeconds(195));
        GatewayDiagnosticsResult diagnostics =
            host.ApplicationService.GetDiagnostics();
        int processId = GetSelectedProcessId(diagnostics);
        Assert.Empty(
            XaeWindowProbe.FindModalDialogs(processId));
        await cleanup.CloseAsync();

        Assert.Equal(
            OperationState.Succeeded,
            completed.Operation.State);
        ActivationResult result = Assert.IsType<ActivationResult>(
            completed.Result);
        Assert.True(result.Ok);
        Assert.Equal(expectedAmsNetId, result.Target.AmsNetId);
        Assert.False(result.RunAfterActivation);
        Assert.Equal(
            ActivationCompletion.RestartSkipped,
            result.Completion);
        Assert.False(result.ActiveConfigurationVerified);
        Assert.Contains(
            result.ObservedRuntimeMode,
            new[]
            {
                RuntimeMode.Run,
                RuntimeMode.Config,
            });
        Assert.Null(result.TestOperationId);
        Assert.Equal(
            ResourceKind.ActivationLog,
            Assert.Single(result.Resources).Kind);

        GatewayEvent runDialog = Assert.Single(
            diagnostics.Events,
            gatewayEvent =>
                string.Equals(
                    gatewayEvent.OperationId,
                    accepted.OperationId,
                    StringComparison.Ordinal)
                && string.Equals(
                    gatewayEvent.Type,
                    GatewayEventTypes.XaeDialogObserved,
                    StringComparison.Ordinal)
                && gatewayEvent.Properties.TryGetValue(
                    "kind",
                    out string? kind)
                && string.Equals(
                    kind,
                    "RunConfirmation",
                    StringComparison.Ordinal));
        Assert.Equal(
            "cancel-run",
            runDialog.Properties["action"]);
        Assert.Equal(
            bool.TrueString,
            runDialog.Properties["actionCompleted"]);

        string[] eventTypes = GetOperationEventTypes(
            diagnostics,
            accepted.OperationId);
        Assert.Contains(
            GatewayEventTypes.ActivationConfigurationStarted,
            eventTypes);
        Assert.Contains(
            GatewayEventTypes.ActivationRestartSkipped,
            eventTypes);
        Assert.Contains(
            GatewayEventTypes.ActivationRuntimeReady,
            eventTypes);
        Assert.Contains(
            GatewayEventTypes.ActivationSucceeded,
            eventTypes);
        Assert.DoesNotContain(
            GatewayEventTypes.ActivationConfigurationActivated,
            eventTypes);
        Assert.DoesNotContain(
            GatewayEventTypes.ActivationRestartStarted,
            eventTypes);
        Assert.DoesNotContain(
            GatewayEventTypes.ActivationRestartRequested,
            eventTypes);
    }

    [RemoteFaultRecoveryFact]
    public async Task RuntimeFaultRequiresExplicitRecoveryBeforeHealthyRebuildThroughIpc()
    {
        using TemporaryDirectory temporary = new();
        string solutionPath = GetSolutionPath();
        string faultSourcePath =
            GetFaultInjectionSourcePath(solutionPath);
        byte[] originalSource =
            File.ReadAllBytes(faultSourcePath);
        byte[] faultEnabledSource =
            ReplaceUniqueAsciiToken(
                originalSource,
                "cGatewayInjectPageFault : BOOL := FALSE;",
                "cGatewayInjectPageFault : BOOL := TRUE ;");
        string expectedAmsNetId =
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_REMOTE_AMS_NET_ID")!;
        string pipeName = CreatePipeName();
        string configurationPath = WriteConfiguration(
            temporary.Path,
            pipeName,
            solutionPath,
            expectedAmsNetId,
            requireRecentBuild: true,
            allowXaeLaunch: true);
        using GatewayDesktopHost host = StartHost(
            configurationPath);
        using GatewayOwnedXaeCleanup cleanup = new(host);
        await WaitForStateAsync(
            host,
            GatewayState.Ready,
            TimeSpan.FromSeconds(60));
        NamedPipeGatewayClient client = new(pipeName);
        await SynchronizeDiskAsync(host, client);
        await WaitForSystemRuntimeModeAsync(
            host,
            TimeSpan.FromSeconds(15),
            RuntimeMode.Run,
            RuntimeMode.Config);

        OperationDetails<BuildResult> baselineBuild =
            await RunBuildAsync(
                client,
                BuildAction.Rebuild,
                TimeSpan.FromSeconds(105));
        Assert.Equal(
            OperationState.Succeeded,
            baselineBuild.Operation.State);
        Assert.True(baselineBuild.Result?.Ok);

        try
        {
            File.WriteAllBytes(
                faultSourcePath,
                faultEnabledSource);
            OperationDetails<BuildResult> faultBuild =
                await RunBuildAsync(
                    client,
                    BuildAction.Rebuild,
                    TimeSpan.FromSeconds(105));
            Assert.Equal(
                OperationState.Succeeded,
                faultBuild.Operation.State);
            Assert.True(faultBuild.Result?.Ok);

            OperationAccepted activationAccepted =
                await StartActivationAsync(
                    client,
                    TimeSpan.FromSeconds(180));
            OperationDetails<ActivationResult> activation =
                await WaitForOperationAsync<ActivationResult>(
                    client,
                    activationAccepted.OperationId,
                    TimeSpan.FromSeconds(195));
            if (activation.Operation.State
                == OperationState.Failed)
            {
                GatewayError activationError =
                    Assert.IsType<GatewayError>(
                        activation.Operation.Error);
                if (activationError.Code
                    == ErrorCodes.RuntimeRecoveryRequired)
                {
                    Assert.Equal(
                        "activation.verify",
                        activationError.Stage);
                    AssertFaultDetails(
                        activationError.Details);
                }
                else
                {
                    Assert.Equal(
                        ErrorCodes.XaeDialogReportedFailure,
                        activationError.Code);
                    string activationStage =
                        Assert.IsType<string>(
                            activationError.Stage);
                    Assert.StartsWith(
                        "activation.",
                        activationStage,
                        StringComparison.Ordinal);
                    Assert.Contains(
                        "Target system reports a fatal error",
                        activationError.Message,
                        StringComparison.OrdinalIgnoreCase);
                    Assert.Contains(
                        "AdsError: 1804",
                        activationError.Message,
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            else
            {
                Assert.Equal(
                    OperationState.Succeeded,
                    activation.Operation.State);
                ActivationResult activationResult =
                    Assert.IsType<ActivationResult>(
                        activation.Result);
                Assert.True(activationResult.Ok);
            }

            await WaitForRuntimeModeAsync(
                    host,
                    TimeSpan.FromSeconds(15),
                    RuntimeMode.Exception);
            GatewayStatusResult faultStatus =
                await WaitForRuntimeFaultDetailsAsync(
                    host,
                    TimeSpan.FromSeconds(15));
            RuntimeAlert alert = Assert.IsType<RuntimeAlert>(
                faultStatus.TwinCat.Alert);
            Assert.Equal(
                DiagnosticSeverity.Error,
                alert.Severity);
            AssertFaultDetails(alert.Details);

            OperationDetails<BuildResult> blockedBuild =
                await RunBuildAsync(
                    client,
                    BuildAction.Build,
                    TimeSpan.FromSeconds(45));
            Assert.Equal(
                OperationState.Failed,
                blockedBuild.Operation.State);
            GatewayError buildError =
                Assert.IsType<GatewayError>(
                    blockedBuild.Operation.Error);
            Assert.Equal(
                ErrorCodes.BuildBlockedByRuntimeException,
                buildError.Code);
            Assert.Equal(
                "build.runtimePreflight",
                buildError.Stage);
            AssertFaultDetails(buildError.Details);

            OperationDetails<RecoverToConfigResult> recovery =
                await RunRecoveryAsync(
                    client,
                    TimeSpan.FromSeconds(135));
            Assert.Equal(
                OperationState.Succeeded,
                recovery.Operation.State);
            RecoverToConfigResult recoveryResult =
                Assert.IsType<RecoverToConfigResult>(
                    recovery.Result);
            Assert.True(recoveryResult.Ok);
            Assert.Equal(
                expectedAmsNetId,
                recoveryResult.Target.AmsNetId);
            Assert.Equal(
                RuntimeMode.Exception,
                recoveryResult.InitialRuntimeMode);
            Assert.Equal(
                RuntimeMode.Config,
                recoveryResult.ObservedRuntimeMode);
            Assert.True(recoveryResult.TransitionRequested);

            File.WriteAllBytes(
                faultSourcePath,
                originalSource);
            await SynchronizeDiskAsync(host, client);
            OperationDetails<BuildResult> healthyBuild =
                await RunBuildAsync(
                    client,
                    BuildAction.Rebuild,
                    TimeSpan.FromSeconds(105));
            Assert.Equal(
                OperationState.Succeeded,
                healthyBuild.Operation.State);
            Assert.True(healthyBuild.Result?.Ok);

            File.WriteAllBytes(
                faultSourcePath,
                originalSource);
            await SynchronizeDiskAsync(host, client);
            GatewayStatusResult finalStatus =
                await WaitForSystemRuntimeModeAsync(
                    host,
                    TimeSpan.FromSeconds(15),
                    RuntimeMode.Config);
            Assert.Null(finalStatus.TwinCat.Alert);
            Assert.True(
                File.ReadAllBytes(faultSourcePath)
                    .SequenceEqual(originalSource));

            GatewayDiagnosticsResult diagnostics =
                host.ApplicationService.GetDiagnostics();
            int processId = GetSelectedProcessId(diagnostics);
            Assert.Empty(
                XaeWindowProbe.FindModalDialogs(processId));
        }
        finally
        {
            if (!File.ReadAllBytes(faultSourcePath)
                    .SequenceEqual(originalSource))
            {
                File.WriteAllBytes(
                    faultSourcePath,
                    originalSource);
            }
        }

        await cleanup.CloseAsync();
    }

    [RemoteTcUnitFact]
    public async Task ActivationRunsLinkedTcUnitThroughIpc()
    {
        using TemporaryDirectory temporary = new();
        string solutionPath = GetSolutionPath();
        string expectedAmsNetId =
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_REMOTE_AMS_NET_ID")!;
        string reportPath =
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_TCUNIT_REPORT_PATH")!;
        string pipeName = CreatePipeName();
        string configurationPath = WriteConfiguration(
            temporary.Path,
            pipeName,
            solutionPath,
            expectedAmsNetId,
            requireRecentBuild: true,
            tcUnitReportPath: reportPath,
            allowXaeLaunch: true);
        using GatewayDesktopHost host = StartHost(
            configurationPath);
        using GatewayOwnedXaeCleanup cleanup = new(host);
        await WaitForStateAsync(
            host,
            GatewayState.Ready,
            TimeSpan.FromSeconds(60));
        NamedPipeGatewayClient client = new(pipeName);
        await SynchronizeDiskAsync(host, client);

        GatewayResponse<OperationAccepted> buildAccepted =
            await client.SendAsync<
                BuildParameters,
                OperationAccepted>(
                GatewayMethods.Build,
                new BuildParameters
                {
                    Profile = "fixture",
                    Action = BuildAction.Build,
                    TimeoutSeconds = 90,
                },
                wait: false,
                CancellationToken.None);
        Assert.True(buildAccepted.Ok);
        OperationDetails<BuildResult> build =
            await WaitForOperationAsync<BuildResult>(
                client,
                Assert.IsType<OperationAccepted>(
                    buildAccepted.Result).OperationId,
                TimeSpan.FromSeconds(105));
        Assert.Equal(
            OperationState.Succeeded,
            build.Operation.State);

        OperationAccepted activationAccepted =
            await StartActivationAsync(
                client,
                TimeSpan.FromSeconds(180),
                waitForTcUnit: true);
        OperationDetails<ActivationResult> activation =
            await WaitForOperationAsync<ActivationResult>(
                client,
                activationAccepted.OperationId,
                TimeSpan.FromSeconds(195));
        Assert.Equal(
            OperationState.Succeeded,
            activation.Operation.State);
        string testOperationId = Assert.IsType<string>(
            activation.Result?.TestOperationId);

        OperationDetails<TestResult> test =
            await WaitForOperationAsync<TestResult>(
                client,
                testOperationId,
                TimeSpan.FromSeconds(135));
        GatewayResponse<OperationDetails<TestResult>>
            queried =
                await client.SendAsync<
                    GetTestResultsParameters,
                    OperationDetails<TestResult>>(
                    GatewayMethods.GetTestResults,
                    new GetTestResultsParameters
                    {
                        OperationId = testOperationId,
                    },
                    wait: true,
                    CancellationToken.None);
        GatewayResponse<OperationAccepted>
            postTestBuildAccepted =
                await client.SendAsync<
                    BuildParameters,
                    OperationAccepted>(
                    GatewayMethods.Build,
                    new BuildParameters
                    {
                        Profile = "fixture",
                        Action = BuildAction.Build,
                        TimeoutSeconds = 90,
                    },
                    wait: false,
                    CancellationToken.None);
        Assert.True(postTestBuildAccepted.Ok);
        OperationDetails<BuildResult> postTestBuild =
            await WaitForOperationAsync<BuildResult>(
                client,
                Assert.IsType<OperationAccepted>(
                    postTestBuildAccepted.Result)
                    .OperationId,
                TimeSpan.FromSeconds(105));
        GatewayDiagnosticsResult diagnostics =
            host.ApplicationService.GetDiagnostics();
        int processId = GetSelectedProcessId(diagnostics);
        Assert.Empty(
            XaeWindowProbe.FindModalDialogs(processId));
        await cleanup.CloseAsync();

        Assert.Equal(
            OperationState.Succeeded,
            test.Operation.State);
        Assert.True(test.Result?.Ok);
        Assert.Equal(1, test.Result?.Counts.Suites);
        Assert.Equal(1, test.Result?.Counts.Tests);
        Assert.Equal(1, test.Result?.Counts.Passed);
        Assert.Equal(0, test.Result?.Counts.Failed);
        Assert.Equal(1, test.Result?.InitializedSuites);
        Assert.Equal(
            ResourceKind.TestReport,
            test.Result?.Report?.Kind);
        Assert.True(queried.Ok);
        Assert.True(queried.Result?.Result?.Ok);
        Assert.Equal(
            OperationState.Succeeded,
            postTestBuild.Operation.State);
        Assert.True(postTestBuild.Result?.Ok);
        Assert.Equal(
            0,
            postTestBuild.Result?.Counts.Errors);
        Assert.Equal(
            new[]
            {
                GatewayEventTypes.TcUnitQueued,
                GatewayEventTypes.TcUnitStarted,
                GatewayEventTypes.TcUnitCompletionObserved,
                GatewayEventTypes.TcUnitReportProduced,
                GatewayEventTypes.TcUnitSucceeded,
            },
            GetOperationEventTypes(
                diagnostics,
                testOperationId));
    }

    private static GatewayDesktopHost StartHost(
        string configurationPath)
    {
        GatewayDesktopHost host = new(
            new GatewayHostOptions
            {
                ConfigurationPath = configurationPath,
            });
        host.Start();
        return host;
    }

    private static async Task<OperationAccepted>
        StartActivationAsync(
            NamedPipeGatewayClient client,
            TimeSpan timeout,
            bool waitForTcUnit = false,
            bool runAfterActivation = true)
    {
        GatewayResponse<OperationAccepted> response =
            await client.SendAsync<
                ActivateParameters,
                OperationAccepted>(
                GatewayMethods.Activate,
                new ActivateParameters
                {
                    Profile = "fixture",
                    WaitForTcUnit = waitForTcUnit,
                    RunAfterActivation = runAfterActivation,
                    TimeoutSeconds =
                        checked((int)timeout.TotalSeconds),
                },
                wait: false,
                CancellationToken.None);
        Assert.True(response.Ok);
        return Assert.IsType<OperationAccepted>(
            response.Result);
    }

    private static async Task<OperationDetails<TResult>>
        WaitForOperationAsync<TResult>(
            NamedPipeGatewayClient client,
            string operationId,
            TimeSpan timeout)
    {
        DateTimeOffset deadline =
            DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            GatewayResponse<OperationDetails<TResult>> response =
                await client.SendAsync<
                    GetOperationParameters,
                    OperationDetails<TResult>>(
                    GatewayMethods.GetOperation,
                    new GetOperationParameters
                    {
                        OperationId = operationId,
                    },
                    wait: true,
                    CancellationToken.None);
            Assert.True(response.Ok);
            Assert.NotNull(response.Result);
            switch (response.Result!.Operation.State)
            {
                case OperationState.Queued:
                case OperationState.Running:
                    await Task.Delay(100);
                    continue;
                default:
                    return response.Result;
            }
        }

        throw new TimeoutException(
            $"Operation '{operationId}' did not complete.");
    }

    private static async Task<OperationDetails<BuildResult>>
        RunBuildAsync(
            NamedPipeGatewayClient client,
            BuildAction action,
            TimeSpan timeout)
    {
        GatewayResponse<OperationAccepted> response =
            await client.SendAsync<
                BuildParameters,
                OperationAccepted>(
                GatewayMethods.Build,
                new BuildParameters
                {
                    Profile = "fixture",
                    Action = action,
                    TimeoutSeconds =
                        checked((int)Math.Max(
                            1,
                            timeout.TotalSeconds - 15)),
                },
                wait: false,
                CancellationToken.None);
        Assert.True(response.Ok);
        return await WaitForOperationAsync<BuildResult>(
            client,
            Assert.IsType<OperationAccepted>(
                response.Result).OperationId,
            timeout);
    }

    private static async Task<
        OperationDetails<RecoverToConfigResult>>
        RunRecoveryAsync(
            NamedPipeGatewayClient client,
            TimeSpan timeout)
    {
        GatewayResponse<OperationAccepted> response =
            await client.SendAsync<
                RecoverToConfigParameters,
                OperationAccepted>(
                GatewayMethods.RecoverToConfig,
                new RecoverToConfigParameters
                {
                    Profile = "fixture",
                    TimeoutSeconds =
                        checked((int)Math.Max(
                            1,
                            timeout.TotalSeconds - 15)),
                },
                wait: false,
                CancellationToken.None);
        Assert.True(response.Ok);
        return await WaitForOperationAsync<
            RecoverToConfigResult>(
                client,
                Assert.IsType<OperationAccepted>(
                    response.Result).OperationId,
                timeout);
    }

    private static async Task SynchronizeDiskAsync(
        GatewayDesktopHost host,
        NamedPipeGatewayClient client)
    {
        OperationAccepted accepted =
            host.ApplicationService.StartSynchronization(
                new SynchronizeParameters
                {
                    Profile = "fixture",
                    TimeoutSeconds = 60,
                },
                agentRequest: false);
        OperationDetails<SynchronizeResult> completed =
            await WaitForOperationAsync<SynchronizeResult>(
                client,
                accepted.OperationId,
                TimeSpan.FromSeconds(75));
        Assert.Equal(
            OperationState.Succeeded,
            completed.Operation.State);
    }

    private static async Task<GatewayStatusResult>
        WaitForRuntimeModeAsync(
            GatewayDesktopHost host,
            TimeSpan timeout,
            params RuntimeMode[] expectedModes)
    {
        DateTimeOffset deadline =
            DateTimeOffset.UtcNow.Add(timeout);
        GatewayStatusResult status =
            host.ApplicationService.GetStatus();
        while (DateTimeOffset.UtcNow < deadline)
        {
            status = host.ApplicationService.GetStatus();
            if (expectedModes.Contains(
                    status.TwinCat.Mode))
            {
                return status;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            "TwinCAT runtime did not reach one of the expected "
                + $"modes ({string.Join(", ", expectedModes)}); "
                + $"current mode is {status.TwinCat.Mode}.");
    }

    private static async Task<GatewayStatusResult>
        WaitForSystemRuntimeModeAsync(
            GatewayDesktopHost host,
            TimeSpan timeout,
            params RuntimeMode[] expectedModes)
    {
        DateTimeOffset deadline =
            DateTimeOffset.UtcNow.Add(timeout);
        GatewayStatusResult status =
            host.ApplicationService.GetStatus();
        while (DateTimeOffset.UtcNow < deadline)
        {
            status = host.ApplicationService.GetStatus();
            if (expectedModes.Contains(
                    status.TwinCat.SystemMode))
            {
                return status;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            "TwinCAT system runtime did not reach one of the "
                + $"expected modes ({string.Join(", ", expectedModes)}); "
                + "current system mode is "
                + $"{status.TwinCat.SystemMode}.");
    }

    private static async Task<GatewayStatusResult>
        WaitForRuntimeFaultDetailsAsync(
            GatewayDesktopHost host,
            TimeSpan timeout)
    {
        DateTimeOffset deadline =
            DateTimeOffset.UtcNow.Add(timeout);
        GatewayStatusResult status =
            host.ApplicationService.GetStatus();
        while (DateTimeOffset.UtcNow < deadline)
        {
            status = host.ApplicationService.GetStatus();
            string? details =
                status.TwinCat.Alert?.Details;
            if (details is not null
                && details.IndexOf(
                    "Page Fault",
                    StringComparison.OrdinalIgnoreCase)
                    >= 0
                && details.IndexOf(
                    "0xc0000005",
                    StringComparison.OrdinalIgnoreCase)
                    >= 0)
            {
                return status;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            "TwinCAT runtime fault details were not retained; "
                + "current details are "
                + $"'{status.TwinCat.Alert?.Details ?? "<none>"}'.");
    }

    private static async Task WaitForStateAsync(
        GatewayDesktopHost host,
        GatewayState expected,
        TimeSpan timeout)
    {
        DateTimeOffset deadline =
            DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (host.ApplicationService
                .GetStatus().Gateway.State == expected)
            {
                return;
            }

            await Task.Delay(100);
        }

        GatewayStatusResult status =
            host.ApplicationService.GetStatus();
        throw new TimeoutException(
            $"Gateway did not reach {expected}; current state is "
            + $"{status.Gateway.State}.");
    }

    private static int GetSelectedProcessId(
        GatewayDiagnosticsResult diagnostics)
    {
        return Assert.IsType<int>(
            Assert.Single(
                diagnostics.DteInstances,
                instance => instance.Selected)
                .ProcessId);
    }

    private static string[] GetOperationEventTypes(
        GatewayDiagnosticsResult diagnostics,
        string operationId)
    {
        return diagnostics.Events
            .Where(gatewayEvent =>
                string.Equals(
                    gatewayEvent.OperationId,
                    operationId,
                    StringComparison.Ordinal))
            .Select(gatewayEvent => gatewayEvent.Type)
            .ToArray();
    }

    private static string WriteConfiguration(
        string directory,
        string pipeName,
        string solutionPath,
        string expectedAmsNetId,
        bool requireRecentBuild,
        string? tcUnitReportPath = null,
        bool allowXaeLaunch = false)
    {
        string path = Path.Combine(
            directory,
            "gateway.json");
        string tcUnitJson = tcUnitReportPath is null
            ? string.Empty
            : $$"""
            ,
                  "tcUnit": {
                    "adsPort": 851,
                    "finishedSymbol": "GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished",
                    "suiteCountSymbol": "GVL_TcUnit.NumberOfInitializedTestSuites",
                    "reportPath": "{{EscapeJson(tcUnitReportPath)}}",
                    "completionTimeoutSeconds": 120
                  }
            """;
        File.WriteAllText(
            path,
            $$"""
            {
              "schemaVersion": 1,
              "pipeName": "{{pipeName}}",
              "defaultProfile": "fixture",
              "logDirectory": "{{EscapeJson(directory)}}",
              "profiles": [
                {
                  "name": "fixture",
                  "solution": "{{EscapeJson(solutionPath)}}",
                  "allowXaeLaunch": {{allowXaeLaunch.ToString().ToLowerInvariant()}},
                  "allowActivation": true,
                  "expectedTarget": {
                    "amsNetId": "{{expectedAmsNetId}}"
                  },
                  "requireRecentSuccessfulBuild": {{requireRecentBuild.ToString().ToLowerInvariant()}},
                  "autoWaitForTcUnit": false{{tcUnitJson}}
                }
              ]
            }
            """);
        return path;
    }

    private static string GetSolutionPath()
    {
        return Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_XAE_SOLUTION")!);
    }

    private static string GetFaultInjectionSourcePath(
        string solutionPath)
    {
        string solutionDirectory =
            Assert.IsType<string>(
                Path.GetDirectoryName(solutionPath));
        string path = Path.GetFullPath(
            Path.Combine(
                solutionDirectory,
                "TC3_SimpleProject",
                "PlcProject1",
                "POUs",
                "MAIN.TcPOU"));
        Assert.True(
            File.Exists(path),
            $"The controlled fault source was not found at '{path}'.");
        return path;
    }

    private static byte[] ReplaceUniqueAsciiToken(
        byte[] source,
        string before,
        string after)
    {
        if (before.Length != after.Length)
        {
            throw new InvalidOperationException(
                "Fault injection tokens must have equal length.");
        }

        byte[] beforeBytes =
            System.Text.Encoding.ASCII.GetBytes(before);
        byte[] afterBytes =
            System.Text.Encoding.ASCII.GetBytes(after);
        int match = -1;
        for (int index = 0;
             index <= source.Length - beforeBytes.Length;
             index++)
        {
            bool equal = true;
            for (int offset = 0;
                 offset < beforeBytes.Length;
                 offset++)
            {
                if (source[index + offset]
                    == beforeBytes[offset])
                {
                    continue;
                }

                equal = false;
                break;
            }

            if (!equal)
            {
                continue;
            }

            if (match >= 0)
            {
                throw new InvalidOperationException(
                    "The controlled fault source contains more than "
                        + "one disabled injection token.");
            }

            match = index;
        }

        if (match < 0)
        {
            throw new InvalidOperationException(
                "The controlled fault source does not contain the "
                    + "disabled injection token.");
        }

        byte[] result = (byte[])source.Clone();
        Buffer.BlockCopy(
            afterBytes,
            0,
            result,
            match,
            afterBytes.Length);
        return result;
    }

    private static void AssertFaultDetails(
        string? details)
    {
        string value = Assert.IsType<string>(details);
        Assert.Contains(
            "Page Fault",
            value,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "0xc0000005",
            value,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string CreatePipeName()
    {
        return "TwinCatGatewayTests-"
            + Guid.NewGuid().ToString("N");
    }

    private static string EscapeJson(string value)
    {
        return value.Replace(@"\", @"\\").Replace(@"""", @"\""");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "TwinCatGatewayTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class GatewayOwnedXaeCleanup : IDisposable
    {
        private readonly GatewayDesktopHost _host;
        private bool _closed;

        public GatewayOwnedXaeCleanup(
            GatewayDesktopHost host)
        {
            _host = host;
        }

        public async Task CloseAsync()
        {
            if (_closed)
            {
                return;
            }

            bool xaeClosed = false;
            try
            {
                xaeClosed =
                    await _host.CloseGatewayLaunchedXaeAsync(
                        TimeSpan.FromSeconds(15));
            }
            finally
            {
                await _host.StopAsync();
                _closed = true;
            }

            Assert.True(
                xaeClosed,
                "The gateway-owned XAE instance did not close.");
        }

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            try
            {
                _host.CloseGatewayLaunchedXaeAsync(
                        TimeSpan.FromSeconds(15))
                    .GetAwaiter()
                    .GetResult();
            }
            finally
            {
                _host.StopAsync()
                    .GetAwaiter()
                    .GetResult();
                _closed = true;
            }
        }
    }
}
