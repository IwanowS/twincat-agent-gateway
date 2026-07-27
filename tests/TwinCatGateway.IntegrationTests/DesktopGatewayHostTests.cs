using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
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
        string solutionPath = Path.GetFullPath(
            Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "fixtures",
                "TC3_SimpleProject",
                "TC3_SimpleProject.sln"));
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
