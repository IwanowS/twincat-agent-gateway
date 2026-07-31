using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public delegate Task<XaeBuildResult> XaeBuildOperationExecutor(
    string operationId,
    XaeBuildParameters parameters,
    CancellationToken cancellationToken);

public delegate Task<ActivationResult> ActivationOperationExecutor(
    string operationId,
    ActivateParameters parameters,
    CancellationToken cancellationToken);

public delegate Task<TargetConfigResult> TargetConfigOperationExecutor(
    string operationId,
    TargetConfigParameters parameters,
    CancellationToken cancellationToken);

public delegate Task<TargetStartRestartResult>
    TargetStartRestartOperationExecutor(
        string operationId,
        TargetStartRestartParameters parameters,
        CancellationToken cancellationToken);

public delegate Task<SynchronizeResult> SynchronizeOperationExecutor(
    string operationId,
    SynchronizeParameters parameters,
    CancellationToken cancellationToken);

public delegate Task<CloseXaeResult> CloseXaeOperationExecutor(
    string operationId,
    CloseXaeParameters parameters,
    CancellationToken cancellationToken);

public delegate Task<XaeMessagesResult> XaeMessagesProvider(
    GetXaeMessagesParameters parameters,
    CancellationToken cancellationToken);

public sealed class GatewayApplicationService
{
    private const int MaximumResourceCharacters = 1024 * 1024;
    private readonly string _version;
    private readonly GatewayStatusSnapshotStore _status;
    private readonly OperationStore _operations;
    private readonly OperationQueue _queue;
    private readonly LocalLogStore _logs;
    private readonly Func<GatewayDiagnosticsResult>? _diagnosticsProvider;
    private readonly XaeBuildOperationExecutor? _xaeBuildExecutor;
    private readonly ActivationOperationExecutor? _activationExecutor;
    private readonly SynchronizeOperationExecutor? _synchronizeExecutor;
    private readonly CloseXaeOperationExecutor? _closeXaeExecutor;
    private readonly TargetConfigOperationExecutor? _targetConfigExecutor;
    private readonly TargetStartRestartOperationExecutor?
        _targetStartRestartExecutor;
    private readonly XaeMessagesProvider? _xaeMessagesProvider;
    private readonly TcUnitPreparationExecutor?
        _tcUnitPreparationExecutor;
    private readonly TcUnitOperationExecutor?
        _tcUnitExecutor;
    private readonly ResolvedProfile? _activeProfile;
    private readonly CapabilityEvaluator? _capabilities;
    private readonly OperationCapabilityPreflight? _preflight;
    private readonly IClock _clock;
    private readonly GatewayEventJournal _eventJournal;
    private readonly Func<string?>? _currentLogPathProvider;
    private readonly SourceManifestResourceReader?
        _sourceManifestReader;
    private readonly Func<int?>? _xaeProcessIdProvider;
    private readonly OperationCancellationService
        _operationCancellation;

    public GatewayApplicationService(
        string version,
        GatewayStatusSnapshotStore status,
        OperationStore operations,
        OperationQueue queue,
        LocalLogStore logs,
        GatewayEventJournal eventJournal,
        Func<GatewayDiagnosticsResult>? diagnosticsProvider = null,
        XaeBuildOperationExecutor? xaeBuildExecutor = null,
        ActivationOperationExecutor? activationExecutor = null,
        ResolvedProfile? activeProfile = null,
        OperationCapabilityPreflight? preflight = null,
        CapabilityEvaluator? capabilities = null,
        IClock? clock = null,
        TcUnitPreparationExecutor?
            tcUnitPreparationExecutor = null,
        TcUnitOperationExecutor? tcUnitExecutor = null,
        SynchronizeOperationExecutor? synchronizeExecutor = null,
        TargetConfigOperationExecutor? targetConfigExecutor = null,
        TargetStartRestartOperationExecutor?
            targetStartRestartExecutor = null,
        XaeMessagesProvider? xaeMessagesProvider = null,
        Func<string?>? currentLogPathProvider = null,
        CloseXaeOperationExecutor? closeXaeExecutor = null,
        SourceManifestStore? sourceManifests = null,
        Func<int?>? xaeProcessIdProvider = null,
        OperationCancellationService? operationCancellation = null)
    {
        _version = version
            ?? throw new ArgumentNullException(nameof(version));
        _status = status
            ?? throw new ArgumentNullException(nameof(status));
        _operations = operations
            ?? throw new ArgumentNullException(nameof(operations));
        _queue = queue
            ?? throw new ArgumentNullException(nameof(queue));
        _operationCancellation = operationCancellation
            ?? new OperationCancellationService(_queue);
        _logs = logs
            ?? throw new ArgumentNullException(nameof(logs));
        _diagnosticsProvider = diagnosticsProvider;
        _xaeBuildExecutor = xaeBuildExecutor;
        _activationExecutor = activationExecutor;
        _synchronizeExecutor = synchronizeExecutor;
        _closeXaeExecutor = closeXaeExecutor;
        _targetConfigExecutor = targetConfigExecutor;
        _targetStartRestartExecutor = targetStartRestartExecutor;
        _xaeMessagesProvider = xaeMessagesProvider;
        _tcUnitPreparationExecutor =
            tcUnitPreparationExecutor;
        _tcUnitExecutor = tcUnitExecutor;
        _activeProfile = activeProfile;
        _preflight = preflight;
        _capabilities = capabilities;
        _xaeProcessIdProvider = xaeProcessIdProvider;
        _clock = clock ?? SystemClock.Instance;
        _eventJournal = eventJournal
            ?? throw new ArgumentNullException(
                nameof(eventJournal));
        _currentLogPathProvider = currentLogPathProvider;
        _sourceManifestReader = sourceManifests is null
            ? null
            : new SourceManifestResourceReader(sourceManifests);
    }

    public HealthResult GetHealth()
    {
        GatewayStatusResult status = _status.Read();
        GatewayState state = status.Gateway.State;
        return new HealthResult
        {
            Version = _version,
            State = state,
            Ready = state != GatewayState.Starting
                && state != GatewayState.Stopping
                && state != GatewayState.Faulted,
        };
    }

    public GatewayStatusResult GetStatus()
    {
        return _status.Read();
    }

    public GatewayDiagnosticsResult GetDiagnostics(
        GetDiagnosticsParameters? parameters = null)
    {
        parameters ??= new GetDiagnosticsParameters();
        if (parameters.AfterEventCursor < 0)
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "The event cursor cannot be negative.",
                stage: "diagnostics.validate");
        }

        if (parameters.AfterEventCursor > 0
            && string.IsNullOrWhiteSpace(
                parameters.EventStreamId))
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "The event stream ID is required when continuing from a cursor.",
                stage: "diagnostics.validate");
        }

        if (parameters.EventStreamId is not null
            && string.IsNullOrWhiteSpace(
                parameters.EventStreamId))
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "The event stream ID cannot be empty.",
                stage: "diagnostics.validate");
        }

        if (parameters.MaximumEvents <= 0
            || parameters.MaximumEvents > 200)
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Maximum events must be between 1 and 200.",
                stage: "diagnostics.validate");
        }

        if (parameters.MinimumSeverity.HasValue
            && !Enum.IsDefined(
                typeof(DiagnosticSeverity),
                parameters.MinimumSeverity.Value))
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Minimum event severity is not supported.",
                stage: "diagnostics.validate");
        }

        GatewayEventPage events = _eventJournal.ReadAfter(
            parameters.EventStreamId,
            parameters.AfterEventCursor,
            parameters.MaximumEvents,
            parameters.MinimumSeverity);
        GatewayDiagnosticsResult result =
            _diagnosticsProvider?.Invoke()
            ?? new GatewayDiagnosticsResult();
        result.Status = _status.Read();
        result.Ipc = new ComponentHealth
        {
            Healthy = true,
        };
        result.LogStore = new ComponentHealth
        {
            Healthy = true,
            Message = _logs.RootDirectory,
        };
        result.Events = events.Events.ToList();
        result.EventStreamId = events.EventStreamId;
        result.NextScanCursor = events.NextScanCursor;
        result.LatestEventCursor = events.LatestCursor;
        result.MoreMatchingEventsAvailable =
            events.MoreMatchingEventsAvailable;
        result.EventHistoryTruncated =
            events.HistoryTruncated;
        return result;
    }

    public Task<XaeMessagesResult> GetXaeMessagesAsync(
        GetXaeMessagesParameters parameters,
        CancellationToken cancellationToken)
    {
        if (parameters is null)
        {
            throw new ArgumentNullException(
                nameof(parameters));
        }

        if (parameters.MaximumMessages <= 0
            || parameters.MaximumMessages > 200)
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Maximum XAE messages must be between 1 and 200.",
                stage: "xae.errorList.validate");
        }

        XaeMessagesProvider provider =
            _xaeMessagesProvider
            ?? throw new GatewayOperationException(
                ErrorCodes.GatewayNotReady,
                "The XAE Error List reader is unavailable.",
                retryable: true,
                stage: "xae.errorList.read");
        return provider(parameters, cancellationToken);
    }

    public OperationAccepted StartXaeBuild(XaeBuildParameters parameters)
    {
        if (parameters is null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        if (_xaeBuildExecutor is null)
        {
            throw new GatewayOperationException(
                ErrorCodes.GatewayNotReady,
                "The XAE build executor is unavailable.",
                retryable: true,
                stage: "xae.build.enqueue");
        }

        OperationCapabilityPreflight preflight =
            RequirePreflight("xae.build.admission");
        ResolvedProfile profile = preflight.EnsureAllowed(
            parameters.Profile,
            CapabilityKey.XaeBuild,
            "xae.build.admission");
        OperationCapabilityGuard buildGuard = new(
            RequireCapabilities("xae.build.admission"),
            profile,
            CapabilityKey.XaeBuild);
        if (!Enum.IsDefined(
            typeof(BuildAction),
            parameters.Action))
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Build action is not supported.",
                stage: "xae.build.validate");
        }

        if (!Enum.IsDefined(
            typeof(XaeBuildScope),
            parameters.Scope))
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "XAE build scope is not supported.",
                stage: "xae.build.validate");
        }

        if (parameters.Scope == XaeBuildScope.Solution
            && !string.IsNullOrWhiteSpace(parameters.Project))
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "A project cannot be specified for solution-scoped build.",
                stage: "xae.build.validate");
        }

        XaeBuildParameters captured =
            CloneXaeBuildParameters(parameters);
        TimeSpan timeout = TimeSpan.FromSeconds(120);
        return _queue.Enqueue(
            OperationKind.XaeBuild,
            (operationId, cancellationToken) =>
                ExecuteXaeBuildAsync(
                    operationId,
                    captured,
                    buildGuard,
                    cancellationToken),
            timeout);
    }

    public OperationAccepted StartActivation(
        ActivateParameters parameters)
    {
        if (parameters is null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        if (_activationExecutor is null)
        {
            throw new GatewayOperationException(
                ErrorCodes.GatewayNotReady,
                "The XAE activation executor is unavailable.",
                retryable: true,
                stage: "activation.enqueue");
        }

        if (string.IsNullOrWhiteSpace(parameters.Profile))
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Activation requires an explicit profile name.",
                stage: "activation.validate");
        }

        OperationCapabilityPreflight preflight =
            RequirePreflight("activation.admission");
        ResolvedProfile profile = preflight.EnsureAllowed(
            parameters.Profile,
            CapabilityKey.XaeActivate,
            "activation.admission",
            requireTarget: true);
        OperationCapabilityGuard activationGuard = new(
            _capabilities!,
            profile,
            CapabilityKey.XaeActivate);

        if (parameters.TimeoutSeconds.HasValue
            && parameters.TimeoutSeconds.Value <= 0)
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Activation timeout must be positive.",
                stage: "activation.validate");
        }

        bool verifyWithTcUnit =
            parameters.Verification == VerificationMode.TcUnit;
        if (verifyWithTcUnit
            && parameters.FinalTargetMode
                != ActivationFinalTargetMode.Run)
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "TcUnit verification requires finalTargetMode=run.",
                stage: "activation.validate");
        }

        if (verifyWithTcUnit
            && profile.Target?.TcUnit is null)
        {
            throw new GatewayOperationException(
                ErrorCodes.ProfileInvalid,
                "TcUnit verification is requested but no TcUnit profile is configured.",
                stage: "activation.validate");
        }

        if (verifyWithTcUnit
            && (_tcUnitPreparationExecutor is null
                || _tcUnitExecutor is null))
        {
            throw new GatewayOperationException(
                ErrorCodes.GatewayNotReady,
                "The linked TcUnit executor is unavailable.",
                retryable: true,
                stage: "activation.tcunit");
        }

        OperationCapabilityGuard? verificationGuard = null;
        if (verifyWithTcUnit)
        {
            preflight.EnsureAllowed(
                profile.Name,
                CapabilityKey.TargetTcUnitVerification,
                "activation.tcunit.admission",
                requireTarget: true);
            verificationGuard = new OperationCapabilityGuard(
                _capabilities!,
                profile,
                CapabilityKey.TargetTcUnitVerification);
        }

        ActivateParameters captured =
            CloneActivateParameters(parameters);
        int timeoutSeconds = captured.TimeoutSeconds ?? 120;
        if (verifyWithTcUnit)
        {
            timeoutSeconds +=
                profile.Target!.TcUnit!.CompletionTimeoutSeconds + 5;
        }
        TimeSpan timeout = TimeSpan.FromSeconds(timeoutSeconds);
        return _queue.Enqueue(
            OperationKind.Activate,
            (operationId, cancellationToken) =>
                ExecuteActivationAsync(
                    operationId,
                    captured,
                    activationGuard,
                    verificationGuard,
                    cancellationToken),
            timeout);
    }

    public OperationAccepted StartTargetConfig(
        TargetConfigParameters parameters)
    {
        if (parameters is null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        if (_targetConfigExecutor is null)
        {
            throw new GatewayOperationException(
                ErrorCodes.GatewayNotReady,
                "The Target Config executor is unavailable.",
                retryable: true,
                stage: "target.config.enqueue",
                component: GatewayComponent.Target,
                sideEffectsStarted: false);
        }

        ResolvedProfile profile =
            RequirePreflight("target.config.admission").EnsureAllowed(
                parameters.Profile,
                CapabilityKey.TargetConfig,
                "target.config.admission",
                requireTarget: true);
        OperationCapabilityGuard capabilityGuard = new(
            RequireCapabilities("target.config.admission"),
            profile,
            CapabilityKey.TargetConfig);
        TargetConfigParameters captured = new()
        {
            Profile = parameters.Profile,
        };
        return _queue.Enqueue(
            OperationKind.TargetConfig,
            (operationId, cancellationToken) =>
                ExecuteTargetConfigAsync(
                    operationId,
                    captured,
                    capabilityGuard,
                    cancellationToken),
            TimeSpan.FromSeconds(120));
    }

    public OperationAccepted StartTargetStartRestart(
        TargetStartRestartParameters parameters)
    {
        if (parameters is null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        if (_targetStartRestartExecutor is null)
        {
            throw new GatewayOperationException(
                ErrorCodes.GatewayNotReady,
                "The Target start/restart executor is unavailable.",
                retryable: true,
                stage: "target.startRestart.enqueue",
                component: GatewayComponent.Target,
                sideEffectsStarted: false);
        }

        ResolvedProfile profile = RequirePreflight(
            "target.startRestart.admission").EnsureAllowed(
                parameters.Profile,
                CapabilityKey.TargetStartRestart,
                "target.startRestart.admission",
                requireTarget: true);
        OperationCapabilityGuard capabilityGuard = new(
            RequireCapabilities("target.startRestart.admission"),
            profile,
            CapabilityKey.TargetStartRestart);
        bool verifyWithTcUnit =
            parameters.Verification == VerificationMode.TcUnit;
        OperationCapabilityGuard? verificationGuard = null;
        if (verifyWithTcUnit)
        {
            if (profile.Target?.TcUnit is null)
            {
                throw new GatewayOperationException(
                    ErrorCodes.ProfileInvalid,
                    "TcUnit verification is requested but no TcUnit profile is configured.",
                    stage: "target.startRestart.validate");
            }
            if (_tcUnitPreparationExecutor is null
                || _tcUnitExecutor is null)
            {
                throw new GatewayOperationException(
                    ErrorCodes.GatewayNotReady,
                    "The linked TcUnit executor is unavailable.",
                    retryable: true,
                    stage: "target.startRestart.verification");
            }
            RequirePreflight(
                "target.startRestart.verification.admission").EnsureAllowed(
                    profile.Name,
                    CapabilityKey.TargetTcUnitVerification,
                    "target.startRestart.verification.admission",
                    requireTarget: true);
            verificationGuard = new OperationCapabilityGuard(
                RequireCapabilities(
                    "target.startRestart.verification.admission"),
                profile,
                CapabilityKey.TargetTcUnitVerification);
        }
        TargetStartRestartParameters captured = new()
        {
            Profile = parameters.Profile,
            Verification = parameters.Verification,
        };
        return _queue.Enqueue(
            OperationKind.TargetStartRestart,
            (operationId, cancellationToken) =>
                ExecuteTargetStartRestartAsync(
                    operationId,
                    captured,
                    capabilityGuard,
                    verificationGuard,
                    cancellationToken),
            TimeSpan.FromSeconds(
                120 + (verifyWithTcUnit
                    ? profile.Target!.TcUnit!.CompletionTimeoutSeconds + 5
                    : 0)));
    }

    public OperationAccepted StartSynchronization(
        SynchronizeParameters parameters,
        bool agentRequest)
    {
        if (parameters is null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        if (_synchronizeExecutor is null)
        {
            throw new GatewayOperationException(
                ErrorCodes.GatewayNotReady,
                "The XAE synchronization executor is unavailable.",
                retryable: true,
                stage: "synchronize.enqueue");
        }

        OperationCapabilityPreflight preflight =
            RequirePreflight("synchronize.admission");
        ResolvedProfile profile = preflight.EnsureAllowed(
            parameters.Profile,
            CapabilityKey.XaeSynchronize,
            "synchronize.admission");
        OperationCapabilityGuard synchronizeGuard = new(
            RequireCapabilities("synchronize.admission"),
            profile,
            CapabilityKey.XaeSynchronize);
        if (parameters.DiscardDirtyDocuments)
        {
            preflight.EnsureAllowed(
                profile.Name,
                CapabilityKey.XaeDiscardDirtyDocuments,
                "synchronize.discard.admission");
        }

        if (parameters.TimeoutSeconds.HasValue
            && parameters.TimeoutSeconds.Value <= 0)
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Synchronization timeout must be positive.",
                stage: "synchronize.validate");
        }

        SynchronizeParameters captured =
            CloneSynchronizeParameters(parameters);
        return _queue.Enqueue(
            OperationKind.Synchronize,
            (operationId, cancellationToken) =>
                ExecuteSynchronizationAsync(
                    operationId,
                    captured,
                    synchronizeGuard,
                    cancellationToken),
            TimeSpan.FromSeconds(
                captured.TimeoutSeconds ?? 120));
    }

    public OperationAccepted StartCloseXae(
        CloseXaeParameters parameters)
    {
        if (parameters is null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        if (_closeXaeExecutor is null || _activeProfile is null)
        {
            throw new GatewayOperationException(
                ErrorCodes.GatewayNotReady,
                "The XAE close executor is unavailable.",
                retryable: true,
                stage: "xae.close.enqueue");
        }

        CapabilityEvaluator capabilities =
            RequireCapabilities("xae.close.admission");
        OperationCapabilityGuard closeGuard = new(
            capabilities,
            _activeProfile,
            CapabilityKey.XaeClose,
            () => new CapabilityEvaluationContext(
                _xaeProcessIdProvider?.Invoke()
                    ?? _status.Read().Xae.ProcessId));
        closeGuard.EnsureAllowed("xae.close.admission");

        if (!Enum.IsDefined(
            typeof(XaeSaveMode),
            parameters.SaveMode))
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "The XAE save mode is not supported.",
                stage: "xae.close.validate");
        }

        if (parameters.SaveMode == XaeSaveMode.Discard)
        {
            capabilities.EnsureAllowed(
                _activeProfile,
                CapabilityKey.XaeDiscardDirtyDocuments,
                "xae.close.discard.admission",
                new CapabilityEvaluationContext(
                    _xaeProcessIdProvider?.Invoke()
                        ?? _status.Read().Xae.ProcessId));
        }

        if (parameters.TimeoutSeconds.HasValue
            && parameters.TimeoutSeconds.Value <= 0)
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "XAE close timeout must be positive.",
                stage: "xae.close.validate");
        }

        CloseXaeParameters captured =
            CloneCloseXaeParameters(parameters);
        return _queue.Enqueue(
            OperationKind.CloseXae,
            (operationId, cancellationToken) =>
                ExecuteCloseXaeAsync(
                    operationId,
                    captured,
                    closeGuard,
                    cancellationToken),
            TimeSpan.FromSeconds(
                (captured.TimeoutSeconds ?? 120) + 5d));
    }

    public OperationDetails<object> GetOperation(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Operation ID is required.");
        }

        StoredOperation? operation = _operations.Get(operationId);
        if (operation is null)
        {
            throw new GatewayOperationException(
                ErrorCodes.OperationNotFound,
                $"Operation '{operationId}' was not found.");
        }

        return new OperationDetails<object>
        {
            Operation = operation.Summary,
            Result = operation.Result,
        };
    }

    public OperationDetails<TestResult> GetTestResults(
        string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Test operation ID is required.");
        }

        StoredOperation? operation =
            _operations.Get(operationId);
        if (operation is null)
        {
            throw new GatewayOperationException(
                ErrorCodes.OperationNotFound,
                $"Operation '{operationId}' was not found.");
        }

        if (operation.Summary.Kind != OperationKind.Test)
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                $"Operation '{operationId}' is not a test operation.");
        }

        return new OperationDetails<TestResult>
        {
            Operation = operation.Summary,
            Result = operation.Result as TestResult,
        };
    }

    public CancelOperationResult CancelOperation(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Operation ID is required.");
        }

        OperationCancellationResult cancellation =
            _operationCancellation.Cancel(operationId);
        if (cancellation == OperationCancellationResult.NotFound)
        {
            throw new GatewayOperationException(
                ErrorCodes.OperationNotFound,
                $"Operation '{operationId}' was not found.");
        }

        if (cancellation == OperationCancellationResult.AlreadyTerminal)
        {
            throw new GatewayOperationException(
                ErrorCodes.OperationNotCancellable,
                $"Operation '{operationId}' has already completed.");
        }

        return new CancelOperationResult
        {
            OperationId = operationId,
            Cancelled = true,
            State = cancellation
                == OperationCancellationResult.CancelledBeforeStart
                    ? OperationState.Cancelled
                    : OperationState.Running,
        };
    }

    public ResourceContent GetResource(
        string uri,
        int maximumCharacters,
        long offset)
    {
        try
        {
            if (string.Equals(
                    uri,
                    GatewayResourceUris.CurrentGatewayLog,
                    StringComparison.Ordinal))
            {
                return ReadCurrentLogPath(
                    maximumCharacters,
                    offset);
            }

            if (GatewayResourceUris.TryParseProfileSources(
                uri,
                out string profile,
                out bool files))
            {
                EnsureSourceProfileIdentity(profile);
                if (maximumCharacters <= 0
                    || maximumCharacters
                        > MaximumResourceCharacters)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(maximumCharacters));
                }

                SourceManifestResourceReader reader =
                    _sourceManifestReader
                    ?? throw new GatewayOperationException(
                        ErrorCodes.GatewayNotReady,
                        "The source manifest is unavailable because "
                            + "no active profile is configured.",
                        retryable: true,
                        stage: "profile.sources.read",
                        component: GatewayComponent.Profile);
                return files
                    ? reader.ReadFiles(
                        profile,
                        maximumCharacters,
                        offset)
                    : reader.ReadManifest(
                        profile,
                        maximumCharacters,
                        offset);
            }

            return _logs.Read(uri, maximumCharacters, offset);
        }
        catch (FileNotFoundException exception)
        {
            throw new GatewayOperationException(
                ErrorCodes.ResourceNotFound,
                $"Resource '{uri}' was not found.",
                innerException: exception);
        }
        catch (ArgumentException exception)
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                exception.Message,
                innerException: exception);
        }
    }

    private void EnsureSourceProfileIdentity(string profile)
    {
        if (_activeProfile is not null
            && string.Equals(
                profile,
                _activeProfile.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new GatewayOperationException(
            _activeProfile is null
                ? ErrorCodes.ProfileNotFound
                : ErrorCodes.XaeSolutionMismatch,
            _activeProfile is null
                ? $"Project profile '{profile}' was not found."
                : $"Profile '{profile}' is not the active XAE context.",
            stage: "profile.sources.read",
            component: GatewayComponent.Profile,
            sideEffectsStarted: false,
            expected: new IdentityEvidence
            {
                Profile = profile,
            },
            observed: _activeProfile is null
                ? null
                : new IdentityEvidence
                {
                    Profile = _activeProfile.Name,
                    Solution = _activeProfile.Xae.Solution,
                });
    }

    private ResourceContent ReadCurrentLogPath(
        int maximumCharacters,
        long offset)
    {
        if (maximumCharacters <= 0
            || maximumCharacters > MaximumResourceCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCharacters));
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        string? currentPath = _currentLogPathProvider?.Invoke();
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            throw new GatewayOperationException(
                ErrorCodes.GatewayNotRunning,
                "The current gateway log is unavailable because the gateway is not running.");
        }

        int contentOffset =
            offset >= currentPath!.Length
                ? currentPath.Length
                : (int)offset;
        int contentLength = Math.Min(
            maximumCharacters,
            currentPath.Length - contentOffset);
        bool truncated =
            contentOffset + contentLength < currentPath.Length;
        return new ResourceContent
        {
            Uri = GatewayResourceUris.CurrentGatewayLog,
            ContentType = "text/plain",
            Content = currentPath.Substring(
                contentOffset,
                contentLength),
            Offset = offset,
            NextOffset = truncated
                ? offset + contentLength
                : null,
            Truncated = truncated,
        };
    }

    public IReadOnlyList<StoredOperation> GetRecentOperations(int maximumCount)
    {
        return _operations.GetRecent(maximumCount);
    }

    private async Task<OperationExecutionResult> ExecuteXaeBuildAsync(
        string operationId,
        XaeBuildParameters parameters,
        OperationCapabilityGuard buildGuard,
        CancellationToken cancellationToken)
    {
        buildGuard.EnsureAllowed(
            "xae.build.preSideEffect");
        XaeBuildResult result = await _xaeBuildExecutor!(
            operationId,
            parameters,
            cancellationToken).ConfigureAwait(false);
        result.OperationId = operationId;
        List<ResourceReference> resources = new();
        if (result.Log is not null)
        {
            resources.Add(result.Log);
        }

        resources.AddRange(
            result.ExpectedProjectNoise
                .Select(change => change.Details)
                .Where(reference => reference is not null)
                .Cast<ResourceReference>()
                .GroupBy(
                    reference => reference.Uri,
                    StringComparer.Ordinal)
                .Select(group => group.First()));
        if (result.Ok)
        {
            return OperationExecutionResult.Success(
                result,
                resources);
        }

        return OperationExecutionResult.Failure(
            new GatewayError
            {
                Code = ErrorCodes.BuildFailed,
                Message = "XAE completed the build with errors.",
                Retryable = false,
                Stage = "xae.build.verify",
                RawLogRef = result.Log?.Uri,
            },
            result,
            resources);
    }

    private async Task<OperationExecutionResult>
        ExecuteSynchronizationAsync(
            string operationId,
            SynchronizeParameters parameters,
            OperationCapabilityGuard synchronizeGuard,
            CancellationToken cancellationToken)
    {
        DateTimeOffset startedAtUtc = _clock.UtcNow;
        _status.Update(status =>
        {
            status.CurrentOperation = new OperationSummary
            {
                OperationId = operationId,
                Kind = OperationKind.Synchronize,
                State = OperationState.Running,
                QueuedAtUtc = startedAtUtc,
                StartedAtUtc = startedAtUtc,
            };
            status.Xae.SynchronizationState =
                SynchronizationState.Synchronizing;
            return status;
        });
        try
        {
            synchronizeGuard.EnsureAllowed(
                "synchronize.preSideEffect");
            SynchronizeResult result =
                await _synchronizeExecutor!(
                    operationId,
                    parameters,
                    cancellationToken).ConfigureAwait(false);
            result.OperationId = operationId;
            _status.Update(status =>
            {
                status.Xae.SynchronizationState =
                    SynchronizationState.Confirmed;
                status.Xae.DiscardedDocumentCount =
                    result.DiscardedDocumentCount;
                return status;
            });
            return OperationExecutionResult.Success(result);
        }
        finally
        {
            _status.Update(status =>
            {
                status.CurrentOperation = null;
                if (status.Xae.SynchronizationState
                    == SynchronizationState.Synchronizing)
                {
                    status.Xae.SynchronizationState =
                        SynchronizationState.SyncRequired;
                }

                return status;
            });
        }
    }

    private async Task<OperationExecutionResult> ExecuteCloseXaeAsync(
        string operationId,
        CloseXaeParameters parameters,
        OperationCapabilityGuard closeGuard,
        CancellationToken cancellationToken)
    {
        DateTimeOffset startedAtUtc = _clock.UtcNow;
        _status.Update(status =>
        {
            status.Gateway.State = GatewayState.ClosingXae;
            status.CurrentOperation = new OperationSummary
            {
                OperationId = operationId,
                Kind = OperationKind.CloseXae,
                State = OperationState.Running,
                QueuedAtUtc = startedAtUtc,
                StartedAtUtc = startedAtUtc,
            };
            return status;
        });
        try
        {
            closeGuard.EnsureAllowed(
                "xae.close.preSideEffect");
            CloseXaeResult result =
                await _closeXaeExecutor!(
                    operationId,
                    parameters,
                    cancellationToken).ConfigureAwait(false);
            result.OperationId = operationId;
            return result.Ok
                ? OperationExecutionResult.Success(result)
                : OperationExecutionResult.Failure(
                    new GatewayError
                    {
                        Code = ErrorCodes.XaeCloseFailed,
                        Message =
                            "XAE close did not satisfy its postcondition.",
                        Stage = "xae.close.verify",
                    },
                    result);
        }
        finally
        {
            _status.Update(status =>
            {
                status.CurrentOperation = null;
                status.Gateway.State = status.Xae.Connected
                    ? GatewayState.Ready
                    : GatewayState.Disconnected;
                return status;
            });
        }
    }

    private async Task<OperationExecutionResult>
        ExecuteActivationAsync(
            string operationId,
            ActivateParameters parameters,
            OperationCapabilityGuard activationGuard,
            OperationCapabilityGuard? verificationGuard,
            CancellationToken cancellationToken)
    {
        DateTimeOffset startedAtUtc = _clock.UtcNow;
        _status.Update(status =>
        {
            status.Gateway.State = GatewayState.Activating;
            status.CurrentOperation = new OperationSummary
            {
                OperationId = operationId,
                Kind = OperationKind.Activate,
                State = OperationState.Running,
                QueuedAtUtc = startedAtUtc,
                StartedAtUtc = startedAtUtc,
            };
            return status;
        });
        try
        {
            activationGuard.EnsureAllowed(
                "activation.preSideEffect");
            bool verifyWithTcUnit =
                parameters.Verification == VerificationMode.TcUnit;
            TcUnitRunPreparation? preparation =
                verifyWithTcUnit
                    ? _tcUnitPreparationExecutor!(
                        operationId)
                    : null;
            ActivationResult result =
                await _activationExecutor!(
                    operationId,
                    parameters,
                    cancellationToken).ConfigureAwait(false);
            result.OperationId = operationId;
            if (result.Ok
                && preparation is not null)
            {
                result.Verification =
                    await ExecuteTcUnitStageAsync(
                        operationId,
                        "activation.verification",
                        preparation,
                        verificationGuard!,
                        cancellationToken).ConfigureAwait(false);
                result.Ok = result.Verification.Completion
                    == OperationCompletion.Succeeded;
            }

            _status.Update(status =>
            {
                status.LastActivation = new ActivationSummary
                {
                    Ok = result.Ok,
                    OperationId = operationId,
                    Profile = result.Profile,
                    Target = CloneTarget(result.Target),
                };
                return status;
            });
            if (result.Ok)
            {
                return OperationExecutionResult.Success(
                    result,
                    result.Resources);
            }

            return OperationExecutionResult.Failure(
                GetActivationFailure(result),
                result,
                result.Resources);
        }
        finally
        {
            _status.Update(status =>
            {
                status.CurrentOperation = null;
                status.Gateway.State = status.Xae.Connected
                    ? GatewayState.Ready
                    : GatewayState.Disconnected;
                return status;
            });
        }
    }

    private async Task<OperationExecutionResult>
        ExecuteTargetConfigAsync(
            string operationId,
            TargetConfigParameters parameters,
            OperationCapabilityGuard capabilityGuard,
            CancellationToken cancellationToken)
    {
        capabilityGuard.EnsureAllowed(
            "target.config.preSideEffect",
            sideEffectsStarted: false);
        TargetConfigResult result = await _targetConfigExecutor!(
            operationId,
            parameters,
            cancellationToken).ConfigureAwait(false);
        result.OperationId = operationId;
        return OperationExecutionResult.Success(result);
    }

    private async Task<OperationExecutionResult>
        ExecuteTargetStartRestartAsync(
            string operationId,
            TargetStartRestartParameters parameters,
            OperationCapabilityGuard capabilityGuard,
            OperationCapabilityGuard? verificationGuard,
            CancellationToken cancellationToken)
    {
        capabilityGuard.EnsureAllowed(
            "target.startRestart.preSideEffect",
            sideEffectsStarted: false);
        TcUnitRunPreparation? preparation =
            parameters.Verification == VerificationMode.TcUnit
                ? _tcUnitPreparationExecutor!(operationId)
                : null;
        TargetStartRestartResult result =
            await _targetStartRestartExecutor!(
                operationId,
                parameters,
                cancellationToken).ConfigureAwait(false);
        result.OperationId = operationId;
        if (preparation is null)
        {
            result.Verification = CreateSkippedVerificationStage(
                operationId,
                "target.startRestart.verification");
            return OperationExecutionResult.Success(result);
        }

        result.Verification = await ExecuteTcUnitStageAsync(
            operationId,
            "target.startRestart.verification",
            preparation,
            verificationGuard!,
            cancellationToken).ConfigureAwait(false);
        result.Ok = result.Verification.Completion
            == OperationCompletion.Succeeded;
        if (result.Ok)
        {
            return OperationExecutionResult.Success(
                result,
                result.Verification.Resources);
        }

        return OperationExecutionResult.Failure(
            result.Verification.Error
                ?? CreateTestFailedError(
                    operationId,
                    "target.startRestart.verification",
                    result.Verification.Result),
            result,
            result.Verification.Resources);
    }

    private async Task<OperationStageResult<TestResult>>
        ExecuteTcUnitStageAsync(
            string operationId,
            string stage,
            TcUnitRunPreparation preparation,
            OperationCapabilityGuard verificationGuard,
            CancellationToken cancellationToken)
    {
        try
        {
            verificationGuard.EnsureAllowed(
                stage + ".preflight");
            TestResult result = await _tcUnitExecutor!(
                operationId,
                preparation,
                cancellationToken).ConfigureAwait(false);
            result.OperationId = operationId;
            _status.Update(status =>
            {
                status.LastTest = new TestSummary
                {
                    Ok = result.Ok,
                    OperationId = operationId,
                    Tests = result.Counts.Tests,
                    Failed = result.Counts.Failed,
                };
                return status;
            });

            ResourceReference[] resources =
                result.Report is null
                    ? Array.Empty<ResourceReference>()
                    : new[]
                    {
                        result.Report,
                    };
            OperationStageResult<TestResult> stageResult = new()
            {
                OperationId = operationId,
                Component = GatewayComponent.Verification,
                Stage = stage,
                Completion = result.Ok
                    ? OperationCompletion.Succeeded
                    : OperationCompletion.Failed,
                SideEffectsStarted = true,
                Result = result,
                Resources = resources.ToList(),
            };
            if (!result.Ok)
            {
                stageResult.Error = CreateTestFailedError(
                    operationId,
                    stage,
                    result);
            }
            return stageResult;
        }
        catch (GatewayOperationException exception)
        {
            return new OperationStageResult<TestResult>
            {
                OperationId = operationId,
                Component = GatewayComponent.Verification,
                Stage = exception.Stage ?? stage,
                Completion = OperationCompletion.Failed,
                SideEffectsStarted =
                    exception.SideEffectsStarted ?? true,
                Error = ToGatewayError(
                    operationId,
                    exception,
                    GatewayComponent.Verification,
                    stage),
            };
        }
    }

    private static XaeBuildParameters CloneXaeBuildParameters(
        XaeBuildParameters source)
    {
        return new XaeBuildParameters
        {
            Profile = source.Profile,
            Action = source.Action,
            Scope = source.Scope,
            Project = source.Project,
            ChangedPaths = source.ChangedPaths?
                .ToList()
                ?? new List<string>(),
            Detail = source.Detail,
        };
    }

    private OperationCapabilityPreflight RequirePreflight(
        string stage)
    {
        return _preflight
            ?? throw new GatewayOperationException(
                ErrorCodes.GatewayNotReady,
                "Operation capability preflight is unavailable.",
                retryable: true,
                stage: stage,
                component: GatewayComponent.Profile,
                sideEffectsStarted: false);
    }

    private CapabilityEvaluator RequireCapabilities(
        string stage)
    {
        return _capabilities
            ?? throw new GatewayOperationException(
                ErrorCodes.GatewayNotReady,
                "Capability evaluation is unavailable.",
                retryable: true,
                stage: stage,
                component: GatewayComponent.Profile,
                sideEffectsStarted: false);
    }

    private static SynchronizeParameters
        CloneSynchronizeParameters(
            SynchronizeParameters source)
    {
        return new SynchronizeParameters
        {
            Profile = source.Profile,
            ChangedPaths = source.ChangedPaths?
                .ToList()
                ?? new List<string>(),
            DiscardDirtyDocuments =
                source.DiscardDirtyDocuments,
            TimeoutSeconds = source.TimeoutSeconds,
        };
    }

    private static ActivateParameters CloneActivateParameters(
        ActivateParameters source)
    {
        return new ActivateParameters
        {
            Profile = source.Profile,
            FinalTargetMode = source.FinalTargetMode,
            Verification = source.Verification,
            ChangedPaths = source.ChangedPaths?.ToList()
                ?? new List<string>(),
            TimeoutSeconds = source.TimeoutSeconds,
        };
    }

    private static OperationStageResult<TestResult>
        CreateSkippedVerificationStage(
            string operationId,
            string stage)
    {
        return new OperationStageResult<TestResult>
        {
            OperationId = operationId,
            Component = GatewayComponent.Verification,
            Stage = stage,
            Completion = OperationCompletion.Skipped,
            SideEffectsStarted = false,
        };
    }

    private static GatewayError GetActivationFailure(
        ActivationResult result)
    {
        return result.Sync.Error
            ?? result.Compile.Error
            ?? result.Deploy.Error
            ?? result.TargetTransition.Error
            ?? result.Verification.Error
            ?? new GatewayError
            {
                Code = ErrorCodes.ActivateConfigurationFailed,
                Message = "TwinCAT activation did not satisfy its postconditions.",
                OperationId = result.OperationId,
                Component = GatewayComponent.Xae,
                Stage = "activation.complete",
                SideEffectsStarted = result.Deploy.SideEffectsStarted,
            };
    }

    private static GatewayError CreateTestFailedError(
        string operationId,
        string stage,
        TestResult? result)
    {
        return new GatewayError
        {
            Code = ErrorCodes.TestFailed,
            Message = "TcUnit completed with failed tests.",
            OperationId = operationId,
            Component = GatewayComponent.Verification,
            Stage = stage,
            SideEffectsStarted = true,
            RawLogRef = result?.Report?.Uri,
        };
    }

    private static GatewayError ToGatewayError(
        string operationId,
        GatewayOperationException exception,
        GatewayComponent fallbackComponent,
        string fallbackStage)
    {
        return new GatewayError
        {
            Code = exception.Code,
            Message = exception.Message,
            Details = exception.Details,
            Retryable = exception.Retryable,
            OperationId = operationId,
            Stage = exception.Stage ?? fallbackStage,
            RawLogRef = exception.RawLogRef,
            Component = exception.Component ?? fallbackComponent,
            SideEffectsStarted = exception.SideEffectsStarted,
            Expected = exception.Expected,
            Observed = exception.Observed,
        };
    }

    private static CloseXaeParameters CloneCloseXaeParameters(
        CloseXaeParameters source)
    {
        return new CloseXaeParameters
        {
            SaveMode = source.SaveMode,
            TimeoutSeconds = source.TimeoutSeconds,
        };
    }

    private static TargetIdentity CloneTarget(
        TargetIdentity source)
    {
        return new TargetIdentity
        {
            Name = source.Name,
            AmsNetId = source.AmsNetId,
        };
    }
}
