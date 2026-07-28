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
    public async Task ActivationBuildsAndRestartsRemoteTargetThroughIpc()
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
                GatewayEventTypes.ActivationRestartStarted,
                GatewayEventTypes.ActivationRestartRequested,
                GatewayEventTypes.ActivationRuntimeReady,
                GatewayEventTypes.ActivationSucceeded,
            },
            GetOperationEventTypes(
                diagnostics,
                accepted.OperationId));
        Assert.Empty(
            XaeWindowProbe.FindModalDialogs(processId));
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

        await host.StopAsync();

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
        Assert.Empty(
            XaeWindowProbe.FindModalDialogs(processId));
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
            bool waitForTcUnit = false)
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
}
