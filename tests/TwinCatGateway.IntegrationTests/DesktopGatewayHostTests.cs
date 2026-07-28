using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Ads;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Desktop;
using TwinCatGateway.Ipc;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class DesktopGatewayHostTests
{
    [Fact]
    public void ManualReconnectIsPublishedWithoutCallingCom()
    {
        using TemporaryDirectory temporary = new();
        string configurationPath = Path.Combine(
            temporary.Path,
            "gateway.json");
        string solutionPath = Path.Combine(
            temporary.Path,
            "missing.sln");
        File.WriteAllText(
            configurationPath,
            $$"""
            {
              "schemaVersion": 1,
              "logDirectory": "{{EscapeJson(temporary.Path)}}",
              "defaultProfile": "fixture",
              "profiles": [
                {
                  "name": "fixture",
                  "solution": "{{EscapeJson(solutionPath)}}",
                  "allowXaeLaunch": false,
                  "allowActivation": false
                }
              ]
            }
            """);
        using GatewayDesktopHost host = new(
            new GatewayHostOptions
            {
                ConfigurationPath = configurationPath,
            });

        host.RequestXaeReconnect();

        Assert.True(host.CanReconnectXae);
        Assert.Contains(
            host.ApplicationService.GetDiagnostics().Events,
            gatewayEvent =>
                gatewayEvent.Type
                    == GatewayEventTypes
                        .XaeReconnectRequested);
    }

    [Fact]
    public void SingleInstanceGuardRejectsSecondOwnerForCurrentUser()
    {
        string name = "TwinCatGatewayTests-" + Guid.NewGuid().ToString("N");

        bool firstAcquired = SingleInstanceGuard.TryAcquire(name, out SingleInstanceGuard? first);
        bool secondAcquired = SingleInstanceGuard.TryAcquire(name, out SingleInstanceGuard? second);

        second?.Dispose();
        first?.Dispose();

        Assert.True(firstAcquired);
        Assert.False(secondAcquired);
    }

    [Fact]
    public async Task DesktopHostServesStatusFromValidatedConfiguration()
    {
        using TemporaryDirectory temporary = new();
        string pipeName = "TwinCatGatewayTests-" + Guid.NewGuid().ToString("N");
        string configurationPath = Path.Combine(temporary.Path, "gateway.json");
        string solutionPath = Path.Combine(
            temporary.Path,
            "missing.sln");
        File.WriteAllText(
            configurationPath,
            $$"""
            {
              "schemaVersion": 1,
              "pipeName": "{{pipeName}}",
              "defaultProfile": "fixture",
              "logDirectory": "{{EscapeJson(temporary.Path)}}",
              "profiles": [
                {
                  "name": "fixture",
                  "solution": "{{EscapeJson(solutionPath)}}",
                  "allowXaeLaunch": false,
                  "allowActivation": false
                }
              ]
            }
            """);
        using GatewayDesktopHost host = new(
            new GatewayHostOptions
            {
                ConfigurationPath = configurationPath,
            });
        host.Start();
        NamedPipeGatewayClient client = new(pipeName);
        await WaitForStateAsync(
            host,
            GatewayState.Disconnected,
            TimeSpan.FromSeconds(10));
        await WaitForErrorCodeAsync(
            host,
            ErrorCodes.XaeNotFound,
            TimeSpan.FromSeconds(10));

        GatewayResponse<GatewayStatusResult> response =
            await client.SendAsync<EmptyParameters, GatewayStatusResult>(
                GatewayMethods.Status,
                new EmptyParameters(),
                wait: true,
                CancellationToken.None);
        GatewayResponse<GatewayDiagnosticsResult> diagnostics =
            await client.SendAsync<
                GetDiagnosticsParameters,
                GatewayDiagnosticsResult>(
                GatewayMethods.GetDiagnostics,
                new GetDiagnosticsParameters
                {
                    AfterEventCursor = 0,
                },
                wait: true,
                CancellationToken.None);

        await host.StopAsync();

        Assert.True(response.Ok);
        Assert.Equal(GatewayState.Disconnected, response.Result?.Gateway.State);
        Assert.Equal("fixture", host.ActiveProfile?.Name);
        Assert.Null(host.StartupError);
        Assert.True(response.Result?.LatestEventCursor > 0);
        Assert.True(diagnostics.Ok);
        Assert.True(
            diagnostics.Result?.LatestEventCursor
                >= response.Result?.LatestEventCursor);
        Assert.Equal(
            diagnostics.Result?.LatestEventCursor,
            diagnostics.Result?.NextScanCursor);
        Assert.Equal(
            GatewayEventTypes.GatewayStarted,
            diagnostics.Result?.Events[0].Type);
        Assert.Contains(
            diagnostics.Result!.Events,
            gatewayEvent =>
                gatewayEvent.Error?.Code
                    == ErrorCodes.XaeNotFound);
    }

    [XaeFact]
    public async Task DesktopHostPublishesConnectedXaeDiagnostics()
    {
        using TemporaryDirectory temporary = new();
        string pipeName = "TwinCatGatewayTests-" + Guid.NewGuid().ToString("N");
        string configurationPath = Path.Combine(
            temporary.Path,
            "gateway.json");
        string solutionPath = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_XAE_SOLUTION")!);
        File.WriteAllText(
            configurationPath,
            $$"""
            {
              "schemaVersion": 1,
              "pipeName": "{{pipeName}}",
              "defaultProfile": "fixture",
              "logDirectory": "{{EscapeJson(temporary.Path)}}",
              "profiles": [
                {
                  "name": "fixture",
                  "solution": "{{EscapeJson(solutionPath)}}",
                  "allowXaeLaunch": false,
                  "allowActivation": false
                }
              ]
            }
            """);
        using GatewayDesktopHost host = new(
            new GatewayHostOptions
            {
                ConfigurationPath = configurationPath,
            });
        host.Start();
        await WaitForStateAsync(
            host,
            GatewayState.Ready,
            TimeSpan.FromSeconds(15));
        NamedPipeGatewayClient client = new(pipeName);

        GatewayResponse<GatewayDiagnosticsResult> response =
            await client.SendAsync<EmptyParameters, GatewayDiagnosticsResult>(
                GatewayMethods.GetDiagnostics,
                new EmptyParameters(),
                wait: true,
                CancellationToken.None);

        await host.StopAsync();

        Assert.True(response.Ok);
        Assert.True(response.Result?.Status.Xae.Connected);
        Assert.True(response.Result?.Xae.SysManagerAvailable);
        Assert.False(
            string.IsNullOrWhiteSpace(
                response.Result?.Xae.ActiveConfiguration));
        Assert.False(
            string.IsNullOrWhiteSpace(
                response.Result?.Xae.ActivePlatform));
        Assert.False(
            string.IsNullOrWhiteSpace(
                response.Result?.Xae.Target?.AmsNetId));
        Assert.Empty(
            response.Result!.Xae.InspectionIssues);
        Assert.Equal(
            AdsRuntimeStatusReader.SystemServicePort,
            response.Result.Runtime.Port);
        Assert.Equal(
            "192.168.3.31.1.1",
            response.Result.Runtime.AmsNetId);
        Assert.Null(response.Result.Runtime.ErrorCode);
        Assert.False(
            string.IsNullOrWhiteSpace(
                response.Result.Runtime.AdsState));
        Assert.NotEqual(
            RuntimeMode.Unknown,
            response.Result.Status.TwinCat.Mode);
        Assert.Contains(
            response.Result.Events,
            gatewayEvent =>
                gatewayEvent.Type
                    == GatewayEventTypes.XaeConnected);
        Assert.Contains(
            response.Result.Events,
            gatewayEvent =>
                gatewayEvent.Type
                    == GatewayEventTypes.RuntimeStateChanged);
        Assert.Contains(
            response.Result!.DteInstances,
            instance => instance.Selected
                && string.Equals(
                    instance.Solution,
                    solutionPath,
                    StringComparison.OrdinalIgnoreCase));
    }

    [XaeFact]
    public async Task DesktopHostBuildCompletesThroughIpc()
    {
        using TemporaryDirectory temporary = new();
        string pipeName =
            "TwinCatGatewayTests-" + Guid.NewGuid().ToString("N");
        string configurationPath = Path.Combine(
            temporary.Path,
            "gateway.json");
        string solutionPath = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_XAE_SOLUTION")!);
        File.WriteAllText(
            configurationPath,
            $$"""
            {
              "schemaVersion": 1,
              "pipeName": "{{pipeName}}",
              "defaultProfile": "fixture",
              "logDirectory": "{{EscapeJson(temporary.Path)}}",
              "profiles": [
                {
                  "name": "fixture",
                  "solution": "{{EscapeJson(solutionPath)}}",
                  "allowXaeLaunch": false,
                  "allowActivation": false
                }
              ]
            }
            """);
        using GatewayDesktopHost host = new(
            new GatewayHostOptions
            {
                ConfigurationPath = configurationPath,
            });
        host.Start();
        await WaitForStateAsync(
            host,
            GatewayState.Ready,
            TimeSpan.FromSeconds(15));
        NamedPipeGatewayClient client = new(pipeName);

        GatewayResponse<OperationAccepted> accepted =
            await client.SendAsync<
                BuildParameters,
                OperationAccepted>(
                GatewayMethods.Build,
                new BuildParameters
                {
                    Profile = "fixture",
                    Action = BuildAction.Build,
                    TimeoutSeconds = 60,
                },
                wait: false,
                CancellationToken.None);
        Assert.True(accepted.Ok);
        Assert.NotNull(accepted.Result);
        OperationDetails<BuildResult> completed =
            await WaitForOperationAsync(
                client,
                accepted.Result!.OperationId,
                TimeSpan.FromSeconds(75));
        Assert.NotNull(completed.Result?.Log);
        GatewayResponse<ResourceContent> log =
            await client.SendAsync<
                GetResourceParameters,
                ResourceContent>(
                GatewayMethods.GetResource,
                new GetResourceParameters
                {
                    Uri = completed.Result!.Log!.Uri,
                    MaximumCharacters = 64 * 1024,
                },
                wait: true,
                CancellationToken.None);
        GatewayDiagnosticsResult diagnostics =
            host.ApplicationService.GetDiagnostics();
        int processId = Assert.IsType<int>(
            Assert.Single(
                diagnostics.DteInstances,
                instance => instance.Selected)
                .ProcessId);

        await host.StopAsync();

        Assert.Equal(
            OperationState.Succeeded,
            completed.Operation.State);
        Assert.True(completed.Result?.Ok);
        Assert.Equal(BuildAction.Build, completed.Result?.Action);
        Assert.Equal(0, completed.Result?.Counts.Errors);
        Assert.Equal(
            ResourceKind.BuildLog,
            Assert.Single(
                completed.Operation.Resources).Kind);
        Assert.True(log.Ok);
        Assert.False(
            string.IsNullOrWhiteSpace(
                log.Result?.Content));
        Assert.Empty(
            XaeWindowProbe.FindModalDialogs(processId));
        Assert.Equal(
            new[]
            {
                GatewayEventTypes.BuildQueued,
                GatewayEventTypes.BuildStarted,
                GatewayEventTypes.BuildSucceeded,
            },
            diagnostics.Events
                .Where(gatewayEvent =>
                    string.Equals(
                        gatewayEvent.OperationId,
                        accepted.Result.OperationId,
                        StringComparison.Ordinal))
                .Select(gatewayEvent => gatewayEvent.Type));
    }

    private static async Task<OperationDetails<BuildResult>>
        WaitForOperationAsync(
            NamedPipeGatewayClient client,
            string operationId,
            TimeSpan timeout)
    {
        DateTimeOffset deadline =
            DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            GatewayResponse<OperationDetails<BuildResult>> response =
                await client.SendAsync<
                    GetOperationParameters,
                    OperationDetails<BuildResult>>(
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

    private static async Task WaitForErrorCodeAsync(
        GatewayDesktopHost host,
        string errorCode,
        TimeSpan timeout)
    {
        DateTimeOffset deadline =
            DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            GatewayDiagnosticsResult diagnostics =
                host.ApplicationService.GetDiagnostics();
            if (diagnostics.Events.Any(
                gatewayEvent =>
                    gatewayEvent.Error?.Code == errorCode))
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(
            $"Gateway did not publish error '{errorCode}'.");
    }

    private static async Task WaitForStateAsync(
        GatewayDesktopHost host,
        GatewayState expected,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (host.ApplicationService.GetStatus().Gateway.State == expected)
            {
                return;
            }

            await Task.Delay(100);
        }

        GatewayStatusResult status =
            host.ApplicationService.GetStatus();
        throw new TimeoutException(
            $"Gateway did not reach {expected}; current state is {status.Gateway.State}.");
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
