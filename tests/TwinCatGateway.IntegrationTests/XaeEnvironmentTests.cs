using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Xae;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class XaeEnvironmentTests
{
    [XaeFact]
    public async Task RunningXaeIsDiscoverableWithoutLaunching()
    {
        string solution = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_XAE_SOLUTION")!);
        using XaeSession session = new();

        XaeSessionSnapshot discovery = await session.DiscoverAsync(
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        Assert.Contains(
            discovery.DiscoveredInstances,
            instance => string.Equals(
                instance.Solution,
                solution,
                StringComparison.OrdinalIgnoreCase));
    }

    [XaeFact]
    public async Task RunningXaeCanBeSelectedByExactSolutionPath()
    {
        string solution = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_XAE_SOLUTION")!);
        using XaeSession session = new();
        XaeSessionSnapshot discovery = await session.DiscoverAsync(
            TimeSpan.FromSeconds(10),
            CancellationToken.None);
        string discovered = string.Join(
            Environment.NewLine,
            discovery.DiscoveredInstances.Select(
                instance =>
                    $"{instance.Moniker} | {instance.Solution ?? "<no solution>"}"
                    + (instance.InspectionError is null
                        ? string.Empty
                        : $" | {instance.InspectionError}"
                            + $" HRESULT=0x{instance.InspectionHResult:X8}")));
        XaeSessionSnapshot snapshot = await session.EnsureAttachedAsync(
            solution,
            allowLaunch: true,
            configuredProgId: Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_XAE_PROGID"),
            TimeSpan.FromSeconds(60),
            CancellationToken.None);

        Assert.True(snapshot.Connected);
        Assert.True(snapshot.SysManagerAvailable);
        Assert.Equal(
            solution,
            snapshot.SelectedInstance?.Solution,
            ignoreCase: true);
        Assert.True(snapshot.SelectedInstance?.Selected);
        Assert.True(
            snapshot.LaunchedByGateway
            || discovery.DiscoveredInstances.Any(instance => string.Equals(
                instance.Solution,
                solution,
                StringComparison.OrdinalIgnoreCase)),
            $"Expected solution was neither attached nor launched.{Environment.NewLine}{discovered}");
    }

    [XaeFact]
    public async Task DisposingAttachedSessionDoesNotCloseUserXae()
    {
        string solution = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_XAE_SOLUTION")!);
        int processId;
        using (XaeSession session = new())
        {
            XaeSessionSnapshot snapshot = await session.AttachAsync(
                solution,
                TimeSpan.FromSeconds(10),
                CancellationToken.None);
            processId = Assert.IsType<int>(
                snapshot.SelectedInstance?.ProcessId);
        }

        using Process process = Process.GetProcessById(processId);

        Assert.False(process.HasExited);
    }

    [XaeLaunchFact]
    public async Task GatewayCanLaunchAndOwnNewXae()
    {
        string solution = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_XAE_SOLUTION")!);
        XaeSession session = new();
        try
        {
            XaeSessionSnapshot snapshot = await session.LaunchAsync(
                solution,
                Environment.GetEnvironmentVariable(
                    "TWINCAT_GATEWAY_XAE_PROGID"),
                TimeSpan.FromSeconds(60),
                CancellationToken.None);

            Assert.True(snapshot.Connected);
            Assert.True(snapshot.LaunchedByGateway);
            Assert.Equal(
                solution,
                snapshot.SelectedInstance?.Solution,
                ignoreCase: true);
            Assert.True(await session.CloseGatewayLaunchedAsync(
                TimeSpan.FromSeconds(15),
                CancellationToken.None));
        }
        finally
        {
            session.Dispose();
        }
    }
}
