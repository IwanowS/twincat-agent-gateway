using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Client;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Ipc;
using TwinCatGateway.Mcp;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class GatewayMcpRuntimeTests
{
    [Fact]
    public async Task StartIsDisabledByProjectPolicy()
    {
        using TestContext context = new(allowStart: false);

        GatewayLifecycleResult<GatewayStartResult> response =
            await context.Runtime.StartAsync(
                server: null,
                explicitConfigurationPath: null,
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal(
            ErrorCodes.CapabilityDisabled,
            response.Error?.Code);
        Assert.Equal(0, context.Launcher.LaunchCount);
    }

    [Fact]
    public async Task StartIsIdempotentForMatchingLiveGateway()
    {
        using TestContext context = new(allowStart: true);
        context.PublishCurrentProcess();

        GatewayLifecycleResult<GatewayStartResult> response =
            await context.Runtime.StartAsync(
                server: null,
                explicitConfigurationPath: null,
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None);

        Assert.True(response.Ok);
        Assert.True(response.Result?.AlreadyRunning);
        Assert.False(response.Result?.Started);
        Assert.Equal(0, context.Launcher.LaunchCount);
    }

    [Fact]
    public async Task StartTimesOutAfterExactlyOneLaunch()
    {
        using TestContext context = new(allowStart: true);

        GatewayLifecycleResult<GatewayStartResult> response =
            await context.Runtime.StartAsync(
                server: null,
                explicitConfigurationPath: null,
                TimeSpan.FromMilliseconds(40),
                CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal(
            ErrorCodes.GatewayStartTimeout,
            response.Error?.Code);
        Assert.Equal(1, context.Launcher.LaunchCount);
    }

    [Fact]
    public async Task StartReportsUnavailableInteractiveLauncher()
    {
        using TestContext context = new(allowStart: true);
        context.Launcher.Failure =
            new InteractiveGatewayLaunchException(
                "Explorer is unavailable.");

        GatewayLifecycleResult<GatewayStartResult> response =
            await context.Runtime.StartAsync(
                server: null,
                explicitConfigurationPath: null,
                TimeSpan.FromMilliseconds(40),
                CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal(
            ErrorCodes
                .GatewayInteractiveLaunchUnavailable,
            response.Error?.Code);
        Assert.Equal(
            "gateway.start.interactiveShell",
            response.Error?.Stage);
        Assert.Equal(1, context.Launcher.LaunchCount);
    }

    [Fact]
    public void ProcessLauncherDelegatesToExplorerSession()
    {
        using TemporaryDirectory temporary = new();
        string executable = Path.Combine(
            temporary.Path,
            "twincat-gateway.exe");
        string configuration = Path.Combine(
            temporary.Path,
            GatewayConfigurationDiscovery.FileName);
        File.WriteAllBytes(executable, Array.Empty<byte>());
        File.WriteAllText(configuration, "{}");
        RecordingExplorerShellExecutor executor = new();
        GatewayProcessLauncher launcher = new(executor);

        launcher.Launch(
            executable,
            configuration);

        Assert.Equal(1, executor.ExecuteCount);
        Assert.Equal(
            Path.GetFullPath(executable),
            executor.Executable);
        Assert.Equal(
            Path.GetDirectoryName(executable),
            executor.WorkingDirectory);
        Assert.Equal(
            "--config \""
                + Path.GetFullPath(configuration)
                + "\" --launch-source agent",
            executor.Arguments);
    }

    [Fact]
    public void ProcessLauncherDoesNotFallBackWhenExplorerFails()
    {
        using TemporaryDirectory temporary = new();
        string executable = Path.Combine(
            temporary.Path,
            "twincat-gateway.exe");
        string configuration = Path.Combine(
            temporary.Path,
            GatewayConfigurationDiscovery.FileName);
        File.WriteAllBytes(executable, Array.Empty<byte>());
        File.WriteAllText(configuration, "{}");
        RecordingExplorerShellExecutor executor = new()
        {
            Failure =
                new InvalidOperationException(
                    "No Explorer desktop."),
        };
        GatewayProcessLauncher launcher = new(executor);

        InteractiveGatewayLaunchException exception =
            Assert.Throws<
                InteractiveGatewayLaunchException>(
                () => launcher.Launch(
                    executable,
                    configuration));

        Assert.Equal(1, executor.ExecuteCount);
        Assert.Contains(
            "interactive Windows Explorer session",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartRejectsGatewayForAnotherProject()
    {
        using TestContext context = new(allowStart: true);
        context.PublishCurrentProcess(
            configurationPath: Path.Combine(
                context.Temporary.Path,
                "other",
                GatewayConfigurationDiscovery.FileName),
            solutionPath: Path.Combine(
                context.Temporary.Path,
                "other",
                "Other.sln"));

        GatewayLifecycleResult<GatewayStartResult> response =
            await context.Runtime.StartAsync(
                server: null,
                explicitConfigurationPath: null,
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal(
            ErrorCodes
                .GatewayRunningDifferentProject,
            response.Error?.Code);
        Assert.Equal(0, context.Launcher.LaunchCount);
    }

    [Fact]
    public async Task OrdinaryOperationReportsNotRunning()
    {
        using TestContext context = new(allowStart: true);

        ITwinCatGatewayClient client =
            await context.Runtime.ResolveClientAsync(
                server: null,
                CancellationToken.None);
        GatewayClientException exception =
            await Assert.ThrowsAsync<GatewayClientException>(
                () => client.GetGatewayStateAsync());

        Assert.Equal(
            ErrorCodes.GatewayNotRunning,
            exception.Error.Code);
    }

    private sealed class TestContext : IDisposable
    {
        private GatewayInstanceRegistration? _registration;

        public TestContext(bool allowStart)
        {
            Temporary = new TemporaryDirectory();
            ConfigurationPath = Path.Combine(
                Temporary.Path,
                GatewayConfigurationDiscovery.FileName);
            SolutionPath = Path.Combine(
                Temporary.Path,
                "Machine.sln");
            File.WriteAllText(
                ConfigurationPath,
                $$"""
                {
                  "schemaVersion": 2,
                  "defaultProfile": "fixture",
                  "gateway": {
                    "processControl": {
                      "allowStart": {{allowStart.ToString().ToLowerInvariant()}},
                      "allowShutdown": false
                    }
                  },
                  "profiles": [
                    {
                      "name": "fixture",
                      "xae": {
                        "solution": "Machine.sln",
                        "capabilities": {
                          "launch": false,
                          "close": false,
                          "synchronize": false,
                          "discardDirtyDocuments": false,
                          "build": false,
                          "activate": false
                        }
                      }
                    }
                  ]
                }
                """);
            Registry = new GatewayInstanceRegistry(
                Path.Combine(
                    Temporary.Path,
                    "instance.json"));
            Launcher = new RecordingLauncher();
            Client = new StatusGatewayClient(
                ConfigurationPath,
                SolutionPath);
            Runtime = new GatewayMcpRuntime(
                new GatewayMcpOptions
                {
                    ExplicitConfigurationPath =
                        ConfigurationPath,
                    CurrentDirectory = Temporary.Path,
                    GatewayCommand = "test-gateway",
                },
                Registry,
                new StatusClientFactory(Client),
                Launcher,
                TimeSpan.FromMilliseconds(5));
        }

        public TemporaryDirectory Temporary { get; }

        public string ConfigurationPath { get; }

        public string SolutionPath { get; }

        public GatewayInstanceRegistry Registry { get; }

        public RecordingLauncher Launcher { get; }

        public StatusGatewayClient Client { get; }

        public GatewayMcpRuntime Runtime { get; }

        public void PublishCurrentProcess(
            string? configurationPath = null,
            string? solutionPath = null)
        {
            using Process process =
                Process.GetCurrentProcess();
            _registration = Registry.Register(
                new GatewayInstanceRecord
                {
                    ProcessId = process.Id,
                    ProcessStartedAtUtc =
                        process.StartTime.ToUniversalTime(),
                    PipeName = "test-pipe",
                    ConfigurationPath =
                        configurationPath
                        ?? ConfigurationPath,
                    ActiveProfile = "fixture",
                    SolutionPath =
                        solutionPath
                        ?? SolutionPath,
                    LaunchSource =
                        GatewayLaunchSource.Agent,
                    UiMode = GatewayUiMode.Tray,
                });
        }

        public void Dispose()
        {
            _registration?.Dispose();
            Temporary.Dispose();
        }
    }

    private sealed class RecordingLauncher
        : IGatewayProcessLauncher
    {
        public int LaunchCount { get; private set; }

        public Exception? Failure { get; set; }

        public void Launch(
            string command,
            string configurationPath)
        {
            LaunchCount++;
            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }

    private sealed class RecordingExplorerShellExecutor
        : IExplorerShellExecutor
    {
        public int ExecuteCount { get; private set; }

        public string? Executable { get; private set; }

        public string? Arguments { get; private set; }

        public string? WorkingDirectory
        {
            get;
            private set;
        }

        public Exception? Failure { get; set; }

        public void Execute(
            string executable,
            string arguments,
            string workingDirectory)
        {
            ExecuteCount++;
            Executable = executable;
            Arguments = arguments;
            WorkingDirectory = workingDirectory;
            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }

    private sealed class StatusClientFactory
        : IGatewayClientFactory
    {
        private readonly ITwinCatGatewayClient _client;

        public StatusClientFactory(
            ITwinCatGatewayClient client)
        {
            _client = client;
        }

        public ITwinCatGatewayClient Create(
            string pipeName)
        {
            return _client;
        }
    }

    private sealed class StatusGatewayClient
        : GatewayClientStub
    {
        private readonly string _configurationPath;
        public StatusGatewayClient(
            string configurationPath,
            string solutionPath)
        {
            _configurationPath = configurationPath;
            _ = solutionPath;
        }

        public override Task<GatewayStateSnapshot>
            GetGatewayStateAsync(
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new GatewayStateSnapshot
                {
                    State = GatewayProcessState.Ready,
                    ConfigurationPath = _configurationPath,
                    ActiveProfile = "fixture",
                });
        }
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
                Directory.Delete(
                    Path,
                    recursive: true);
            }
        }
    }
}
