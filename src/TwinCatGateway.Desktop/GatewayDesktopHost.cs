using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Ipc;

namespace TwinCatGateway.Desktop;

public sealed class GatewayDesktopHost : IDisposable
{
    private const string DefaultPipeName = "TwinCatAgentGateway";
    private readonly CancellationTokenSource _shutdown = new();
    private readonly GatewayLoggingSession _logging;
    private readonly ILogger<GatewayDesktopHost> _logger;
    private readonly OperationQueue _queue;
    private readonly NamedPipeGatewayServer _server;
    private readonly GatewayStatusSnapshotStore _status;
    private readonly GatewayEventJournal _events;
    private readonly OperatorLockStore _operatorLocks;
    private readonly XaeCloseConsentStore _xaeCloseConsent;
    private readonly CapabilityEvaluator _capabilities;
    private readonly CapabilitySnapshotStore _capabilitySnapshots;
    private readonly SourceManifestStore? _sourceManifests;
    private readonly ProfileObservationStore? _observations;
    private readonly AdsRuntimeMonitor? _runtimeMonitor;
    private readonly XaeSessionCoordinator? _xaeCoordinator;
    private readonly OperationCancellationService _operationCancellation;
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
        _operatorLocks = new OperatorLockStore();
        _xaeCloseConsent = new XaeCloseConsentStore();
        _capabilities = new CapabilityEvaluator(
            Configuration,
            _xaeCloseConsent,
            _operatorLocks);
        _capabilitySnapshots = new CapabilitySnapshotStore(
            _capabilities);
        _capabilitySnapshots.RefreshGateway();
        if (ActiveProfile is not null)
        {
            _capabilitySnapshots.RefreshProfile(ActiveProfile);
        }
        _sourceManifests = ActiveProfile is null
            ? null
            : new SourceManifestStore(
                ActiveProfile.Name,
                ActiveProfile.Xae.Solution);
        _observations = ActiveProfile?.Target is null
            ? null
            : new ProfileObservationStore(
                ActiveProfile.Name,
                ActiveProfile.Target.AmsNetId);

        OperationCapabilityPreflight? preflight =
            hostConfiguration.Profiles is null
                ? null
                : new OperationCapabilityPreflight(
                    hostConfiguration.Profiles,
                    _capabilities,
                    ActiveProfile);
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
        _logging = GatewayLoggingSession.Create(
            LogDirectory,
            Configuration);
        _logger = _logging.CreateLogger<GatewayDesktopHost>();
        LocalLogStore logs = new(
            Path.Combine(LogDirectory, "operations"));
        TryPruneLogs(
            logs,
            LogDirectory,
            _logging.SessionBasePath,
            Configuration.Gateway.Logging.RetentionDays);

        _status = new GatewayStatusSnapshotStore(
            GatewayStatusSnapshotStore.CreateInitial(version));
        _events = new GatewayEventJournal(_status);
        XaeErrorListSnapshotStore errorListSnapshots =
            new();
        _runtimeMonitor = ActiveProfile?.Target is null
            ? null
            : new AdsRuntimeMonitor(
                _observations!,
                _logging.CreateLogger<AdsRuntimeMonitor>(),
                _events,
                ActiveProfile.Name,
                ActiveProfile.Target.AmsNetId,
                ActiveProfile.Target.PollIntervalMilliseconds,
                ActiveProfile.Target.ReadTimeoutMilliseconds,
                ActiveProfile.Target.TcUnit?.RuntimeId,
                ActiveProfile.Target.TcUnit?.AdsPort);
        OperationStore operations = new();
        _queue = new OperationQueue(
            operations,
            exceptionSink: new OperationExceptionLoggingSink(
                _logging.CreateLogger<OperationQueue>()),
            gatewayEventSink: _events);
        _operationCancellation = new OperationCancellationService(
            _queue);
        _xaeCoordinator = ActiveProfile is null
            ? null
            : new XaeSessionCoordinator(
                ActiveProfile,
                hostConfiguration.Profiles!,
                _capabilities,
                _status,
                _logging.CreateLogger<XaeSessionCoordinator>(),
                _logging.CreateLogger<TcUnitRunExecutor>(),
                logs,
                _events,
                _runtimeMonitor,
                errorListSnapshots,
                _sourceManifests!,
                _xaeCloseConsent,
                _capabilitySnapshots);
        Func<GatewayDiagnosticsResult>? diagnosticsProvider =
            _xaeCoordinator is null
                ? null
                : _xaeCoordinator.CreateDiagnostics;
        XaeBuildOperationExecutor? xaeBuildExecutor =
            _xaeCoordinator is null
                ? null
                : _xaeCoordinator.ExecuteXaeBuildAsync;
        ActivationOperationExecutor? activationExecutor =
            _xaeCoordinator is null
                ? null
                : _xaeCoordinator.ExecuteActivationAsync;
        RecoveryOperationExecutor? recoveryExecutor =
            _xaeCoordinator is null
                ? null
                : _xaeCoordinator.ExecuteRecoverToConfigAsync;
        TargetConfigOperationExecutor? targetConfigExecutor =
            _xaeCoordinator is null
                ? null
                : _xaeCoordinator.ExecuteTargetConfigAsync;
        TargetStartRestartOperationExecutor?
            targetStartRestartExecutor =
                _xaeCoordinator is null
                    ? null
                    : _xaeCoordinator
                        .ExecuteTargetStartRestartAsync;
        SynchronizeOperationExecutor? synchronizeExecutor =
            _xaeCoordinator is null
                ? null
                : _xaeCoordinator.ExecuteSynchronizationAsync;
        CloseXaeOperationExecutor? closeXaeExecutor =
            _xaeCoordinator is null
                ? null
                : _xaeCoordinator.ExecuteCloseXaeAsync;
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
            xaeBuildExecutor,
            activationExecutor,
            ActiveProfile,
            preflight,
            _capabilities,
            clock: null,
            tcUnitPreparationExecutor:
                tcUnitPreparationExecutor,
            tcUnitExecutor: tcUnitExecutor,
            synchronizeExecutor: synchronizeExecutor,
            recoveryExecutor: recoveryExecutor,
            targetConfigExecutor: targetConfigExecutor,
            targetStartRestartExecutor:
                targetStartRestartExecutor,
            xaeMessagesProvider: xaeMessagesProvider,
            currentLogPathProvider: () => _logging.Path,
            closeXaeExecutor: closeXaeExecutor,
            sourceManifests: _sourceManifests,
            xaeProcessIdProvider: ActiveProfile is null
                ? null
                : () => _xaeCloseConsent.ReadProcessId(
                    ActiveProfile!.Name),
            operationCancellation: _operationCancellation);
        GatewayRequestDispatcher dispatcher = new(
            ApplicationService,
            _capabilities,
            RequestShutdown);
        GatewayProtocolHandler protocol = new(
            dispatcher.DispatchAsync,
            (operationId, exception) =>
                _logger.RecordException(
                    string.IsNullOrWhiteSpace(operationId)
                        ? "ipc"
                        : operationId,
                    exception));
        _server = new NamedPipeGatewayServer(
            PipeName,
            protocol,
            (operationId, exception) =>
                _logger.RecordException(operationId, exception));

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
            ActiveProfile?.Xae.Solution;
        initial.Gateway.LaunchSource = LaunchSource;
        initial.Gateway.UiMode = EffectiveUiMode;
        _status.Replace(initial);
    }

    public GatewayApplicationService ApplicationService { get; }

    public GatewayConfiguration Configuration { get; }

    public ResolvedProfile? ActiveProfile { get; }

    public CapabilityEvaluator Capabilities => _capabilities;

    public CapabilitySnapshotStore CapabilitySnapshots =>
        _capabilitySnapshots;

    public OperatorLockStore OperatorLocks => _operatorLocks;

    public XaeCloseConsentStore XaeCloseConsent => _xaeCloseConsent;

    public OperationCancellationService OperationCancellation =>
        _operationCancellation;

    public IReadOnlyList<CapabilityState> SetOperatorLock(
        string profile,
        OperatorLockKey key,
        bool locked)
    {
        ResolvedProfile activeProfile = ResolveActiveProfile(profile);
        _operatorLocks.SetLocked(activeProfile.Name, key, locked);
        return RefreshActiveProfileCapabilities(activeProfile);
    }

    public XaeCloseConsentState SetXaeCloseConsent(
        string profile,
        int processId,
        bool consented)
    {
        ResolvedProfile activeProfile = ResolveActiveProfile(profile);
        XaeCloseConsentState state = _xaeCloseConsent.SetConsent(
            activeProfile.Name,
            processId,
            consented);
        RefreshActiveProfileCapabilities(activeProfile);
        return state;
    }

    public string? ConfigurationPath { get; }

    public string PipeName { get; }

    public GatewayLaunchSource LaunchSource { get; }

    public GatewayUiMode EffectiveUiMode { get; }

    public string LogDirectory { get; }

    public string? StartupError { get; }

    public bool CanReconnectXae => _xaeCoordinator is not null;

    public event EventHandler? ShutdownRequested;

    private ResolvedProfile ResolveActiveProfile(string profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            throw new ArgumentException(
                "Profile name is required.",
                nameof(profile));
        }

        if (ActiveProfile is null
            || !string.Equals(
                ActiveProfile.Name,
                profile,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayOperationException(
                ErrorCodes.ProfileNotFound,
                $"Profile '{profile}' is not the active profile.",
                stage: "operatorSessionState.profile");
        }

        return ActiveProfile;
    }

    private IReadOnlyList<CapabilityState>
        RefreshActiveProfileCapabilities(ResolvedProfile profile)
    {
        return _capabilitySnapshots.RefreshProfile(
            profile,
            new CapabilityEvaluationContext(
                _xaeCloseConsent.ReadProcessId(profile.Name)));
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException(
                "Desktop gateway host has already started.");
        }

        _logger.Write(
            LogLevel.Information,
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
                LogLevel.Error,
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
            LogLevel.Information,
            "gateway.shutdown.requested",
            "Gateway shutdown was requested through IPC.");
        ShutdownRequested?.Invoke(this, EventArgs.Empty);
    }

    internal void RecordUiFailure(
        string stage,
        Exception exception)
    {
        _logger.Write(
            LogLevel.Error,
            "ui.failure",
            $"Desktop UI stage '{stage}' failed.",
            exception: exception);
        GatewayError error = new()
        {
            Code = ErrorCodes.UiFailure,
            Message = exception.Message,
            Details =
                $"{exception.GetType().FullName}: {exception.Message}",
            Retryable = false,
            Stage = stage,
        };
        _events.Record(
            new GatewayEvent
            {
                Type = GatewayEventTypes.UiFailure,
                Severity = DiagnosticSeverity.Error,
                Stage = stage,
                Message = exception.Message,
                Error = error,
                Properties =
                    new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["exceptionType"] =
                            exception.GetType().FullName
                            ?? exception.GetType().Name,
                        ["hresult"] = $"0x{exception.HResult:X8}",
                    },
            },
            DateTimeOffset.UtcNow);
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
        if (_xaeCoordinator is not null)
        {
            await _xaeCoordinator
                .CloseConsentedCleanXaeOnShutdownAsync(
                    TimeSpan.FromSeconds(10),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        _xaeCoordinator?.Dispose();
        _runtimeMonitor?.Dispose();
        _server.Dispose();
        _shutdown.Dispose();
        _logger.Write(
            LogLevel.Information,
            "gateway.stop",
            "Gateway host stopped.");
        _events.Record(
            CreateEvent(
                GatewayEventTypes.GatewayStopped,
                "Gateway host stopped."),
            DateTimeOffset.UtcNow);
        _logging.Dispose();
    }

    internal Task<bool> CloseGatewayLaunchedXaeAsync(
        TimeSpan timeout)
    {
        return _xaeCoordinator is null
            ? Task.FromResult(false)
            : _xaeCoordinator.CloseGatewayLaunchedXaeAsync(
                timeout,
                CancellationToken.None);
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
            _logger.RecordException("ipc-server", exception);
            _status.Update(status =>
            {
                status.Gateway.State = GatewayState.Faulted;
                return status;
            });
            GatewayError error = new()
            {
                Code = ErrorCodes.OperationFailed,
                Message = "The IPC server stopped unexpectedly.",
                Details =
                    $"{exception.GetType().FullName}: {exception.Message}",
                Stage = "ipc.server",
            };
            _events.Record(
                CreateEvent(
                    GatewayEventTypes.GatewayFaulted,
                    error.Message,
                    DiagnosticSeverity.Error,
                    error,
                    new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["exceptionType"] =
                            exception.GetType().FullName
                            ?? exception.GetType().Name,
                        ["hresult"] = $"0x{exception.HResult:X8}",
                    }),
                DateTimeOffset.UtcNow);
        }
    }

    private static GatewayEvent CreateEvent(
        string type,
        string message,
        DiagnosticSeverity severity =
            DiagnosticSeverity.Info,
        GatewayError? error = null,
        System.Collections.Generic.Dictionary<string, string>?
            properties = null)
    {
        return new GatewayEvent
        {
            Type = type,
            Severity = severity,
            Stage = error?.Stage,
            Message = message,
            Error = error,
            Properties = properties
                ?? new System.Collections.Generic.Dictionary<string, string>(),
        };
    }

    private void TryPruneLogs(
        LocalLogStore logs,
        string logDirectory,
        string currentSessionBasePath,
        int retentionDays)
    {
        DateTimeOffset olderThanUtc =
            DateTimeOffset.UtcNow.AddDays(-retentionDays);
        try
        {
            logs.Prune(olderThanUtc);
        }
        catch (Exception exception)
        {
            _logger.RecordException(
                "operation-log-retention",
                exception);
        }

        try
        {
            GatewaySessionLogRetention.Prune(
                logDirectory,
                currentSessionBasePath,
                olderThanUtc);
        }
        catch (Exception exception)
        {
            _logger.RecordException(
                "gateway-log-retention",
                exception);
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

            ProfileResolver profiles = new(configuration);
            return new HostConfiguration(
                configuration,
                profiles,
                profiles.Resolve(null),
                configuration.Gateway.PipeName,
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
        if (!string.IsNullOrWhiteSpace(
            configuration.Gateway.Logging.Directory))
        {
            return Path.GetFullPath(
                configuration.Gateway.Logging.Directory!);
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
            ProfileResolver? profiles,
            ResolvedProfile? activeProfile,
            string pipeName,
            string? error)
        {
            Configuration = configuration;
            Profiles = profiles;
            ActiveProfile = activeProfile;
            PipeName = pipeName;
            Error = error;
        }

        public GatewayConfiguration Configuration { get; }

        public ProfileResolver? Profiles { get; }

        public ResolvedProfile? ActiveProfile { get; }

        public string PipeName { get; }

        public string? Error { get; }

        public static HostConfiguration Faulted(string error)
        {
            return new HostConfiguration(
                new GatewayConfiguration
                {
                    Gateway = new GatewaySettingsConfiguration
                    {
                        PipeName = DefaultPipeName,
                    },
                },
                profiles: null,
                activeProfile: null,
                DefaultPipeName,
                error);
        }
    }

}
