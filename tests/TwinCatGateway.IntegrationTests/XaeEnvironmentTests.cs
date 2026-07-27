using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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

    [XaeFact]
    public async Task AttachedSessionHealthCheckPreservesExactSelection()
    {
        string solution = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_XAE_SOLUTION")!);
        using XaeSession session = new();
        await session.AttachAsync(
            solution,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        XaeSessionSnapshot snapshot =
            await session.VerifyAttachedAsync(
                solution,
                TimeSpan.FromSeconds(10),
                CancellationToken.None);

        Assert.True(snapshot.Connected);
        Assert.True(snapshot.SysManagerAvailable);
        Assert.Equal(
            solution,
            snapshot.SelectedInstance?.Solution,
            ignoreCase: true);
        Assert.False(
            string.IsNullOrWhiteSpace(
                snapshot.ActiveConfiguration));
        Assert.False(
            string.IsNullOrWhiteSpace(
                snapshot.ActivePlatform));
        Assert.Equal(
            "192.168.3.31.1.1",
            snapshot.TargetAmsNetId);
        Assert.Empty(snapshot.DiagnosticIssues);
        Assert.Empty(XaeWindowProbe.FindModalDialogs(
            Assert.IsType<int>(
                snapshot.SelectedInstance?.ProcessId)));
    }

    [XaeFact]
    public async Task AttachedSessionRestoresUserSilentMode()
    {
        string solution = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_XAE_SOLUTION")!);
        using XaeSession session = new();
        await session.AttachAsync(
            solution,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);
        bool before = await session.ReadSilentModeAsync(
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        await session.VerifyAttachedAsync(
            solution,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        bool after = await session.ReadSilentModeAsync(
            TimeSpan.FromSeconds(10),
            CancellationToken.None);
        Assert.Equal(before, after);
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
            Assert.True(await session.ReadSilentModeAsync(
                TimeSpan.FromSeconds(10),
                CancellationToken.None));
            Assert.Empty(XaeWindowProbe.FindModalDialogs(
                Assert.IsType<int>(
                    snapshot.SelectedInstance?.ProcessId)));
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

internal static class XaeWindowProbe
{
    private const uint GetWindowOwner = 4;

    public static string[] FindModalDialogs(int processId)
    {
        System.Collections.Generic.List<string> dialogs = new();
        EnumWindows(
            (window, parameter) =>
            {
                uint windowThreadId = GetWindowThreadProcessId(
                    window,
                    out uint windowProcessId);
                if (windowThreadId == 0
                    || windowProcessId != processId
                    || !IsWindowVisible(window))
                {
                    return true;
                }

                bool disabledTopLevelWindow =
                    GetWindow(window, GetWindowOwner) == IntPtr.Zero
                    && !IsWindowEnabled(window);
                if (disabledTopLevelWindow)
                {
                    dialogs.Add(
                        $"0x{window.ToInt64():X}");
                }

                return true;
            },
            IntPtr.Zero);
        return dialogs.ToArray();
    }

    private delegate bool EnumWindowsCallback(
        IntPtr window,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(
        IntPtr window,
        uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
