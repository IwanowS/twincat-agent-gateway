using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Client;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Mcp;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class GatewayMcpRuntimeTests
{
    [Fact]
    public async Task StartIsDisabledByProjectPolicy()
    {
        using TestContext context = new(allowStart: false);

        GatewayResponse<GatewayStartResult> response =
            await context.Runtime.StartAsync(
                server: null,
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal(
            ErrorCodes.GatewayStartDisabled,
            response.Error?.Code);
        Assert.Equal(0, context.Launcher.LaunchCount);
    }

    [Fact]
    public async Task StartIsIdempotentForMatchingLiveGateway()
    {
        using TestContext context = new(allowStart: true);
        context.PublishCurrentProcess();

        GatewayResponse<GatewayStartResult> response =
            await context.Runtime.StartAsync(
                server: null,
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

        GatewayResponse<GatewayStartResult> response =
            await context.Runtime.StartAsync(
                server: null,
                TimeSpan.FromMilliseconds(40),
                CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal(
            ErrorCodes.GatewayStartTimeout,
            response.Error?.Code);
        Assert.Equal(1, context.Launcher.LaunchCount);
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

        GatewayResponse<GatewayStartResult> response =
            await context.Runtime.StartAsync(
                server: null,
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
        GatewayResponse<GatewayStatusResult> response =
            await client.GetStatusAsync();

        Assert.False(response.Ok);
        Assert.Equal(
            ErrorCodes.GatewayNotRunning,
            response.Error?.Code);
    }

    [Fact]
    public async Task StatusToolReturnsStructuredNotRunningError()
    {
        using TestContext context = new(allowStart: true);
        TwinCatTools tools = new(context.Runtime);

        string result = await tools.GetStatusAsync();

        using JsonDocument json = JsonDocument.Parse(result);
        Assert.False(
            json.RootElement
                .GetProperty("ok")
                .GetBoolean());
        Assert.Equal(
            ErrorCodes.GatewayNotRunning,
            json.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
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
                  "schemaVersion": 1,
                  "defaultProfile": "fixture",
                  "agentProcessControl": {
                    "allowStart": {{allowStart.ToString().ToLowerInvariant()}},
                    "allowShutdown": false
                  },
                  "profiles": [
                    {
                      "name": "fixture",
                      "solution": "Machine.sln",
                      "allowXaeLaunch": false,
                      "allowActivation": false
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

        public void Launch(
            string command,
            string configurationPath)
        {
            LaunchCount++;
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
        : ITwinCatGatewayClient
    {
        private readonly string _configurationPath;
        private readonly string _solutionPath;

        public StatusGatewayClient(
            string configurationPath,
            string solutionPath)
        {
            _configurationPath = configurationPath;
            _solutionPath = solutionPath;
        }

        public Task<GatewayResponse<GatewayStatusResult>>
            GetStatusAsync(
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new GatewayResponse<GatewayStatusResult>
                {
                    Ok = true,
                    Result = new GatewayStatusResult
                    {
                        Gateway = new GatewayStatus
                        {
                            Ready = true,
                            ConfigurationPath =
                                _configurationPath,
                            ActiveProfile = "fixture",
                            SolutionPath = _solutionPath,
                            LaunchSource =
                                GatewayLaunchSource.Agent,
                            UiMode = GatewayUiMode.Tray,
                        },
                    },
                });
        }

        public Task<GatewayResponse<HealthResult>>
            GetHealthAsync(
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<GatewayDiagnosticsResult>>
            GetDiagnosticsAsync(
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<GatewayDiagnosticsResult>>
            GetDiagnosticsAsync(
                GetDiagnosticsParameters parameters,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<OperationAccepted>>
            StartBuildAsync(
                BuildParameters parameters,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<OperationAccepted>>
            StartActivationAsync(
                ActivateParameters parameters,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<OperationDetails<TResult>>>
            GetOperationAsync<TResult>(
                string operationId,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<
            OperationDetails<TestResult>>>
            GetTestResultsAsync(
                string operationId,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<CancelOperationResult>>
            CancelOperationAsync(
                string operationId,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<ResourceContent>>
            GetResourceAsync(
                string uri,
                int maximumCharacters = 64 * 1024,
                long offset = 0,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
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
