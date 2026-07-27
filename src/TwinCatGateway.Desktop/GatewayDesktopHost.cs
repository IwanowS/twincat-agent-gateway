using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Ipc;

namespace TwinCatGateway.Desktop;

public sealed class GatewayDesktopHost : IDisposable
{
    private const string DefaultPipeName = "TwinCatAgentGateway";
    private readonly CancellationTokenSource _shutdown = new();
    private readonly StructuredFileLogger _logger;
    private readonly OperationQueue _queue;
    private readonly NamedPipeGatewayServer _server;
    private readonly GatewayStatusSnapshotStore _status;
    private readonly XaeSessionCoordinator? _xaeCoordinator;
    private Task? _serverTask;
    private Task? _xaeTask;
    private int _started;
    private int _disposed;

    public GatewayDesktopHost(GatewayHostOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        string version = GetVersion();
        HostConfiguration hostConfiguration = LoadConfiguration(options);
        StartupError = hostConfiguration.Error;
        Configuration = hostConfiguration.Configuration;
        ActiveProfile = hostConfiguration.ActiveProfile;
        LogDirectory = ResolveLogDirectory(Configuration);
        _logger = new StructuredFileLogger(LogDirectory);
        LocalLogStore logs = new(
            Path.Combine(LogDirectory, "operations"));
        TryPruneLogs(logs, Configuration.LogRetentionDays);

        _status = new GatewayStatusSnapshotStore(
            GatewayStatusSnapshotStore.CreateInitial(version));
        OperationStore operations = new();
        _queue = new OperationQueue(
            operations,
            exceptionSink: _logger);
        _xaeCoordinator = ActiveProfile is null
            ? null
            : new XaeSessionCoordinator(
                ActiveProfile,
                _status,
                _logger,
                logs);
        Func<GatewayDiagnosticsResult>? diagnosticsProvider =
            _xaeCoordinator is null
                ? null
                : _xaeCoordinator.CreateDiagnostics;
        BuildOperationExecutor? buildExecutor =
            _xaeCoordinator is null
                ? null
                : _xaeCoordinator.ExecuteBuildAsync;
        ApplicationService = new GatewayApplicationService(
            version,
            _status,
            operations,
            _queue,
            logs,
            diagnosticsProvider,
            buildExecutor);
        GatewayRequestDispatcher dispatcher = new(ApplicationService);
        GatewayProtocolHandler protocol = new(
            dispatcher.DispatchAsync,
            (operationId, exception) =>
                _logger.Record(
                    string.IsNullOrWhiteSpace(operationId)
                        ? "ipc"
                        : operationId,
                    exception));
        _server = new NamedPipeGatewayServer(
            hostConfiguration.PipeName,
            protocol,
            (operationId, exception) =>
                _logger.Record(operationId, exception));

        GatewayStatusResult initial = _status.Read();
        initial.Gateway.State = StartupError is null
            ? GatewayState.Disconnected
            : GatewayState.Faulted;
        _status.Replace(initial);
    }

    public GatewayApplicationService ApplicationService { get; }

    public GatewayConfiguration Configuration { get; }

    public ProjectProfile? ActiveProfile { get; }

    public string LogDirectory { get; }

    public string? StartupError { get; }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException(
                "Desktop gateway host has already started.");
        }

        _logger.Write(
            StructuredLogLevel.Information,
            "gateway.start",
            StartupError is null
                ? "Gateway host started."
                : "Gateway host started in faulted configuration state.");
        if (StartupError is not null)
        {
            _logger.Write(
                StructuredLogLevel.Error,
                "configuration.invalid",
                StartupError);
        }

        _serverTask = ObserveServerAsync(_server.RunAsync(_shutdown.Token));
        if (_xaeCoordinator is not null)
        {
            _xaeTask = _xaeCoordinator.RunAsync(_shutdown.Token);
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            if (_serverTask is not null)
            {
                await _serverTask.ConfigureAwait(false);
            }

            if (_xaeTask is not null)
            {
                await _xaeTask.ConfigureAwait(false);
            }

            return;
        }

        _status.Update(status =>
        {
            status.Gateway.State = GatewayState.Stopping;
            return status;
        });
        _shutdown.Cancel();
        if (_serverTask is not null)
        {
            await _serverTask.ConfigureAwait(false);
        }

        if (_xaeTask is not null)
        {
            await _xaeTask.ConfigureAwait(false);
        }

        await _queue.StopAsync().ConfigureAwait(false);
        _xaeCoordinator?.Dispose();
        _server.Dispose();
        _shutdown.Dispose();
        _logger.Write(
            StructuredLogLevel.Information,
            "gateway.stop",
            "Gateway host stopped.");
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    private async Task ObserveServerAsync(Task serverTask)
    {
        try
        {
            await serverTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Record("ipc-server", exception);
            _status.Update(status =>
            {
                status.Gateway.State = GatewayState.Faulted;
                status.UnreadErrors++;
                return status;
            });
        }
    }

    private void TryPruneLogs(LocalLogStore logs, int retentionDays)
    {
        try
        {
            logs.Prune(DateTimeOffset.UtcNow.AddDays(-retentionDays));
        }
        catch (Exception exception)
        {
            _logger.Record("log-retention", exception);
        }
    }

    private static HostConfiguration LoadConfiguration(
        GatewayHostOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConfigurationPath))
        {
            return HostConfiguration.Faulted(
                "No configuration file was found. Pass --config <path> or set "
                + "TWINCAT_GATEWAY_CONFIG.");
        }

        try
        {
            GatewayConfiguration configuration =
                new GatewayConfigurationLoader().Load(options.ConfigurationPath!);
            ConfigurationValidationResult validation =
                GatewayConfigurationValidator.Validate(configuration);
            if (!validation.IsValid)
            {
                string error = string.Join(
                    Environment.NewLine,
                    validation.Issues.Select(
                        issue => $"{issue.Path}: {issue.Message}"));
                return HostConfiguration.Faulted(error);
            }

            ProjectProfileCatalog profiles = new(configuration);
            return new HostConfiguration(
                configuration,
                profiles.GetRequired(null),
                configuration.PipeName,
                error: null);
        }
        catch (Exception exception)
        {
            return HostConfiguration.Faulted(
                $"Configuration could not be loaded: {exception.Message}");
        }
    }

    private static string ResolveLogDirectory(
        GatewayConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration.LogDirectory))
        {
            return Path.GetFullPath(configuration.LogDirectory);
        }

        return Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "TwinCatAgentGateway",
            "Logs");
    }

    private static string GetVersion()
    {
        Version? version = typeof(GatewayDesktopHost)
            .Assembly
            .GetName()
            .Version;
        return version is null
            ? "0.1.0"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private sealed class HostConfiguration
    {
        public HostConfiguration(
            GatewayConfiguration configuration,
            ProjectProfile? activeProfile,
            string pipeName,
            string? error)
        {
            Configuration = configuration;
            ActiveProfile = activeProfile;
            PipeName = pipeName;
            Error = error;
        }

        public GatewayConfiguration Configuration { get; }

        public ProjectProfile? ActiveProfile { get; }

        public string PipeName { get; }

        public string? Error { get; }

        public static HostConfiguration Faulted(string error)
        {
            return new HostConfiguration(
                new GatewayConfiguration
                {
                    PipeName = DefaultPipeName,
                },
                activeProfile: null,
                DefaultPipeName,
                error);
        }
    }
}
