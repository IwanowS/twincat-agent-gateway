using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Desktop;
using TwinCatGateway.Ipc;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class DesktopGatewayHostTests
{
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

        GatewayResponse<GatewayStatusResult> response =
            await client.SendAsync<EmptyParameters, GatewayStatusResult>(
                GatewayMethods.Status,
                new EmptyParameters(),
                wait: true,
                CancellationToken.None);

        await host.StopAsync();

        Assert.True(response.Ok);
        Assert.Equal(GatewayState.Disconnected, response.Result?.Gateway.State);
        Assert.Equal("fixture", host.ActiveProfile?.Name);
        Assert.Null(host.StartupError);
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
        Assert.Contains(
            response.Result!.DteInstances,
            instance => instance.Selected
                && string.Equals(
                    instance.Solution,
                    solutionPath,
                    StringComparison.OrdinalIgnoreCase));
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
