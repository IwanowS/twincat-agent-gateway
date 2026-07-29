using System;
using System.IO;
using System.Linq;
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
    private readonly GatewayEventJournal _events;
    private readonly AdsRuntimeMonitor? _runtimeMonitor;
    private readonly XaeSessionCoordinator? _xaeCoordinator;
    private Task? _serverTask;
    private Task? _runtimeMonitorTask;
    private Task? _xaeTask;
    private int _shutdownRequested;
    private int _started;
    private int _disposed;

    public GatewayDesktopHost(GatewayHostOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        string version = GatewayProductVersion.Value;
        HostConfiguration hostConfiguration = LoadConfiguration(options);
        StartupError = hostConfiguration.Error;
        Configuration = hostConfiguration.Configuration;
        ActiveProfile = hostConfiguration.ActiveProfile;
        PipeName = hostConfiguration.PipeName;
        ConfigurationPath = string.IsNullOrWhiteSpace(
                options.ConfigurationPath)
            ? null
            : Path.GetFullPath(options.ConfigurationPath!);
        LaunchSource = options.LaunchSource;
        EffectiveUiMode = GatewayUiModeResolver.Resolve(
            options.UiModeOverride
                ?? Configuration.Ui?.Mode
                ?? GatewayUiMode.Auto,
            LaunchSource);
        LogDirectory = ResolveLogDirectory(Configuration);
        _logger = new StructuredFileLogger(LogDirectory);
        LocalLogStore logs = new(
            Path.Combine(LogDirectory, "operations"));
        TryPruneLogs(logs, Configuration.LogRetentionDays);

        _status = new GatewayStatusSnapshotStore(
            GatewayStatusSnapshotStore.CreateInitial(version));
        _events = new GatewayEventJournal(_status);
        XaeErrorListSnapshotStore errorListSnapshots =
            new();
        _runtimeMonitor = ActiveProfile is null
            ? null
            : new AdsRuntimeMonitor(
                _status,
                _logger,
                _events,
                Configuration.RuntimeMonitoring,
                errorListSnapshots: errorListSnapshots);
        OperationStore operations = new();
        _queue = new OperationQueue(
            operations,
            exceptionSink: _logger,
            gatewayEventSink: _events);
        _xaeCoordinator = ActiveProfile is null
            ? null
            : new XaeSessionCoordinator(
                ActiveProfile,
                _status,
                _logger,
                logs,
                _events,
                _runtimeMonitor!,
                errorListSnapshots);
        Func<GatewayDiagnosticsResult>? diagnosticsProvider =
            _xaeCoordinator is null
                ? null
                : _xaeCoordinator.CreateDiagnostics;
        BuildOperationExecutor? buildExecutor =
            _xaeCoordinator is null
                ? null
                : _xaeCoordinator.ExecuteBuildAsync;
        ActivationOperationExecutor? activationExecutor =
            _xaeCoordinator is null
                ? null
                : _xaeCoordinator.ExecuteActivationAsync;
        RecoveryOperationExecutor? recoveryExecutor =
            _xaeCoordinator is null
                ? null
                : _xaeCoordinator.ExecuteRecoverToConfigAsync;
        SynchronizeOperationExecutor? synchronizeExecutor =
            _xaeCoordinator is null
                ? null
                : _xaeCoordinator.ExecuteSynchronizationAsync;
        TcUnitPreparationExecutor?
            tcUnitPreparationExecutor =
                _xaeCoordinator is null
                    ? null
                    : _xaeCoordinator.PrepareTcUnitRun;
        TcUnitOperationExecutor? tcUnitExecutor =
            _xaeCoordinator is null
                ? null
                : _xaeCoordinator.ExecuteTcUnitAsync;
        XaeMessagesProvider? xaeMessagesProvider =
            _xaeCoordinator is null
                ? null
                : _xaeCoordinator.ReadXaeMessagesAsync;
        ApplicationService = new GatewayApplicationService(
            version,
            _status,
            operations,
            _queue,
            logs,
            _events,
            diagnosticsProvider,
            buildExecutor,
            activationExecutor,
            ActiveProfile,
            clock: null,
            tcUnitPreparationExecutor:
                tcUnitPreparationExecutor,
            tcUnitExecutor: tcUnitExecutor,
            synchronizeExecutor: synchronizeExecutor,
            recoveryExecutor: recoveryExecutor,
            xaeMessagesProvider: xaeMessagesProvider);
        GatewayRequestDispatcher dispatcher = new(
            ApplicationService,
            Configuration.AgentProcessControl.AllowShutdown,
            RequestShutdown);
        GatewayProtocolHandler protocol = new(
            dispatcher.DispatchAsync,
            (operationId, exception) =>
                _logger.Record(
                    string.IsNullOrWhiteSpace(operationId)
                        ? "ipc"
                        : operationId,
                    exception));
        _server = new NamedPipeGatewayServer(
            PipeName,
            protocol,
            (operationId, exception) =>
                _logger.Record(operationId, exception));

        GatewayStatusResult initial = _status.Read();
        initial.Gateway.State = StartupError is null
            ? GatewayState.Disconnected
            : GatewayState.Faulted;
        initial.Gateway.Ready = StartupError is null;
        initial.Gateway.ConfigurationPath =
            ConfigurationPath;
        initial.Gateway.ActiveProfile =
            ActiveProfile?.Name;
        initial.Gateway.SolutionPath =
            ActiveProfile?.Solution;
        initial.Gateway.LaunchSource = LaunchSource;
        initial.Gateway.UiMode = EffectiveUiMode;
        _status.Replace(initial);
    }

    public GatewayApplicationService ApplicationService { get; }

    public GatewayConfiguration Configuration { get; }

    public ProjectProfile? ActiveProfile { get; }

    public string? ConfigurationPath { get; }

    public string PipeName { get; }

    public GatewayLaunchSource LaunchSource { get; }

    public GatewayUiMode EffectiveUiMode { get; }

    public string LogDirectory { get; }

    public string? StartupError { get; }

    public bool CanReconnectXae => _xaeCoordinator is not null;

    public event EventHandler? ShutdownRequested;

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
        _events.Record(
            CreateEvent(
                GatewayEventTypes.GatewayStarted,
                "Gateway host started."),
            DateTimeOffset.UtcNow);
        if (StartupError is not null)
        {
            _logger.Write(
                StructuredLogLevel.Error,
                "configuration.invalid",
                StartupError);
            GatewayError error = new()
            {
                Code = ErrorCodes.ProfileInvalid,
                Message = StartupError,
                Stage = "configuration.load",
            };
            _events.Record(
                CreateEvent(
                    GatewayEventTypes.GatewayFaulted,
                    error.Message,
                    DiagnosticSeverity.Error,
                    error),
                DateTimeOffset.UtcNow);
        }

        _serverTask = ObserveServerAsync(_server.RunAsync(_shutdown.Token));
        if (_runtimeMonitor is not null)
        {
            _runtimeMonitorTask =
                _runtimeMonitor.RunAsync(_shutdown.Token);
        }

        if (_xaeCoordinator is not null)
        {
            _xaeTask = _xaeCoordinator.RunAsync(_shutdown.Token);
        }
    }

    public void RequestXaeReconnect()
    {
        XaeSessionCoordinator coordinator =
            _xaeCoordinator
            ?? throw new InvalidOperationException(
                "No valid project profile is configured.");
        coordinator.RequestReconnect();
    }

    private void RequestShutdown()
    {
        if (Interlocked.Exchange(
            ref _shutdownRequested,
            1) != 0)
        {
            return;
        }

        _logger.Write(
            StructuredLogLevel.Information,
            "gateway.shutdown.requested",
            "Gateway shutdown was requested through IPC.");
        ShutdownRequested?.Invoke(this, EventArgs.Empty);
    }

    internal void RecordUiFailure(
        string stage,
        Exception exception)
    {
        _logger.Write(
            StructuredLogLevel.Error,
            "ui.failure",
            $"Desktop UI stage '{stage}' failed.",
            exception: exception);
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

            if (_runtimeMonitorTask is not null)
            {
                await _runtimeMonitorTask.ConfigureAwait(false);
            }

            return;
        }

        _status.Update(status =>
        {
            status.Gateway.State = GatewayState.Stopping;
            status.Gateway.Ready = false;
            return status;
        });
        _events.Record(
            CreateEvent(
                GatewayEventTypes.GatewayStopping,
                "Gateway host is stopping."),
            DateTimeOffset.UtcNow);
        _shutdown.Cancel();
        if (_serverTask is not null)
        {
            await _serverTask.ConfigureAwait(false);
        }

        if (_xaeTask is not null)
        {
            await _xaeTask.ConfigureAwait(false);
        }

        if (_runtimeMonitorTask is not null)
        {
            await _runtimeMonitorTask.ConfigureAwait(false);
        }

        await _queue.StopAsync().ConfigureAwait(false);
        _xaeCoordinator?.Dispose();
        _runtimeMonitor?.Dispose();
        _server.Dispose();
        _shutdown.Dispose();
        _logger.Write(
            StructuredLogLevel.Information,
            "gateway.stop",
            "Gateway host stopped.");
        _events.Record(
            CreateEvent(
                GatewayEventTypes.GatewayStopped,
                "Gateway host stopped."),
            DateTimeOffset.UtcNow);
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
                return status;
            });
            GatewayError error = new()
            {
                Code = ErrorCodes.OperationFailed,
                Message = "The IPC server stopped unexpectedly.",
                Stage = "ipc.server",
            };
            _events.Record(
                CreateEvent(
                    GatewayEventTypes.GatewayFaulted,
                    error.Message,
                    DiagnosticSeverity.Error,
                    error),
                DateTimeOffset.UtcNow);
        }
    }

    private static GatewayEvent CreateEvent(
        string type,
        string message,
        DiagnosticSeverity severity =
            DiagnosticSeverity.Info,
        GatewayError? error = null)
    {
        return new GatewayEvent
        {
            Type = type,
            Severity = severity,
            Stage = error?.Stage,
            Message = message,
            Error = error,
        };
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
                "No configuration file was found. Pass --config <path>.");
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
