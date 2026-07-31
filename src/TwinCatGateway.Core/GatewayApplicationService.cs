using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public delegate Task<XaeBuildResult> XaeBuildOperationExecutor(
    string operationId,
    XaeBuildParameters parameters,
    CancellationToken cancellationToken);

public delegate Task<XaeOpenResult> XaeOpenOperationExecutor(
    string operationId,
    XaeOpenParameters parameters,
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
    private readonly GatewayStatusSnapshotStore _status;
    private readonly OperationStore _operations;
    private readonly OperationQueue _queue;
    private readonly LocalLogStore _logs;
    private readonly XaeBuildOperationExecutor? _xaeBuildExecutor;
    private readonly XaeOpenOperationExecutor? _xaeOpenExecutor;
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
    private readonly CapabilitySnapshotStore? _capabilitySnapshots;
    private readonly ProfileObservationStore? _profileObservations;
    private readonly Func<XaeSessionSnapshot>? _xaeStateProvider;
    private readonly Func<XaeDiagnosticsSnapshot>? _xaeDiagnosticsProvider;
    private readonly Func<XaeMessagesResult>? _xaeMessagesSnapshotProvider;
    private static readonly JsonSerializerOptions ResourceJsonOptions =
        CreateResourceJsonOptions();

    public GatewayApplicationService(
        string version,
        GatewayStatusSnapshotStore status,
        OperationStore operations,
        OperationQueue queue,
        LocalLogStore logs,
        GatewayEventJournal eventJournal,
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
        OperationCancellationService? operationCancellation = null,
        XaeOpenOperationExecutor? xaeOpenExecutor = null,
        CapabilitySnapshotStore? capabilitySnapshots = null,
        ProfileObservationStore? profileObservations = null,
        Func<XaeSessionSnapshot>? xaeStateProvider = null,
        Func<XaeDiagnosticsSnapshot>? xaeDiagnosticsProvider = null,
        Func<XaeMessagesResult>? xaeMessagesSnapshotProvider = null)
    {
        if (version is null)
        {
            throw new ArgumentNullException(nameof(version));
        }
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
        _xaeBuildExecutor = xaeBuildExecutor;
        _xaeOpenExecutor = xaeOpenExecutor;
        _capabilitySnapshots = capabilitySnapshots;
        _profileObservations = profileObservations;
        _xaeStateProvider = xaeStateProvider;
        _xaeDiagnosticsProvider = xaeDiagnosticsProvider;
        _xaeMessagesSnapshotProvider = xaeMessagesSnapshotProvider;
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

    public GatewayStateSnapshot GetGatewayState()
    {
        return _status.Read();
    }

    public OperationEventPage GetOperationEvents(
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

        return _eventJournal.ReadAfter(
            parameters.EventStreamId,
            parameters.AfterEventCursor,
            parameters.MaximumEvents,
            parameters.MinimumSeverity);
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

    public OperationHandle StartXaeOpen(XaeOpenParameters parameters)
    {
        if (parameters is null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        if (_xaeOpenExecutor is null || _activeProfile is null)
        {
            throw new GatewayOperationException(
                ErrorCodes.GatewayNotReady,
                "The XAE open executor is unavailable.",
                retryable: true,
                stage: "xae.open.enqueue",
                component: GatewayComponent.Xae,
                sideEffectsStarted: false);
        }

        if (!string.Equals(
                parameters.Profile,
                _activeProfile.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayOperationException(
                ErrorCodes.ProfileNotFound,
                $"Project profile '{parameters.Profile}' was not found.",
                stage: "xae.open.admission",
                component: GatewayComponent.Profile,
                sideEffectsStarted: false);
        }

        XaeOpenParameters captured = new()
        {
            Profile = parameters.Profile,
        };
        return _queue.Enqueue(
            OperationKind.XaeOpen,
            async (operationId, cancellationToken) =>
            {
                XaeOpenResult result = await _xaeOpenExecutor(
                        operationId,
                        captured,
                        cancellationToken)
                    .ConfigureAwait(false);
                return OperationExecutionResult.Success(result);
            },
            TimeSpan.FromSeconds(60),
            _activeProfile.Name);
    }

    public OperationHandle StartXaeBuild(XaeBuildParameters parameters)
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
            timeout,
            profile.Name);
    }

    public OperationHandle StartActivation(
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
            timeout,
            profile.Name);
    }

    public OperationHandle StartTargetConfig(
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
            TimeSpan.FromSeconds(120),
            profile.Name);
    }

    public OperationHandle StartTargetStartRestart(
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
                    : 0)),
            profile.Name);
    }

    public OperationHandle StartSynchronization(
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
                captured.TimeoutSeconds ?? 120),
            profile.Name);
    }

    public OperationHandle StartCloseXae(
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

        if (!string.Equals(
                parameters.Profile,
                _activeProfile.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayOperationException(
                ErrorCodes.ProfileNotFound,
                $"Project profile '{parameters.Profile}' was not found.",
                stage: "xae.close.admission",
                component: GatewayComponent.Profile,
                sideEffectsStarted: false);
        }

        CapabilityEvaluator capabilities =
            RequireCapabilities("xae.close.admission");
        OperationCapabilityGuard closeGuard = new(
            capabilities,
            _activeProfile,
            CapabilityKey.XaeClose,
            () => new CapabilityEvaluationContext(
                _xaeProcessIdProvider?.Invoke()));
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
                    _xaeProcessIdProvider?.Invoke()));
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
                (captured.TimeoutSeconds ?? 120) + 5d),
            _activeProfile.Name);
    }

    public OperationSnapshot<object> GetOperation(string operationId)
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

        return new OperationSnapshot<object>
        {
            Operation = operation.Summary,
            Result = operation.Result,
        };
    }

    public OperationHandle EnqueuePreflightFailure(
        OperationKind kind,
        string? profile,
        GatewayOperationException exception)
    {
        if (exception is null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        return _queue.Enqueue(
            kind,
            (operationId, _) =>
            {
                GatewayError error = ToGatewayError(
                    operationId,
                    exception,
                    GetOperationComponent(kind),
                    GetOperationStage(kind) + ".preflight");
                return Task.FromResult(
                    OperationExecutionResult.Failure(
                        error,
                        resources: error.Resources));
            },
            profile: profile);
    }

    public async Task<OperationResult<TResult>> WaitForOperationAsync<TResult>(
        string operationId,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                StoredOperation? operation = _operations.Get(operationId);
                if (operation is null)
                {
                    throw new GatewayOperationException(
                        ErrorCodes.OperationNotFound,
                        $"Operation '{operationId}' was not found.");
                }

                if (operation.Summary.State is not OperationState.Queued
                    and not OperationState.Running)
                {
                    return CreateOperationResult<TResult>(operation);
                }

                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _operationCancellation.Cancel(operationId);
            throw;
        }
    }

    public OperationCancellationReceipt CancelOperation(string operationId)
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

        return new OperationCancellationReceipt
        {
            OperationId = operationId,
            CancellationRequested = true,
            State = cancellation
                == OperationCancellationResult.CancelledBeforeStart
                    ? OperationState.Cancelled
                    : OperationState.Running,
        };
    }

    private static OperationResult<TResult> CreateOperationResult<TResult>(
        StoredOperation operation)
    {
        OperationRecord record = operation.Summary;
        return new OperationResult<TResult>
        {
            Ok = record.State == OperationState.Succeeded,
            OperationId = record.OperationId,
            Component = record.Error?.Component
                ?? GetOperationComponent(record.Kind),
            Stage = record.Error?.Stage ?? GetOperationStage(record.Kind),
            Completion = record.State switch
            {
                OperationState.Succeeded => OperationCompletion.Succeeded,
                OperationState.TimedOut => OperationCompletion.TimedOut,
                OperationState.Cancelled => OperationCompletion.Cancelled,
                _ => OperationCompletion.Failed,
            },
            SideEffectsStarted = record.Error?.SideEffectsStarted ?? false,
            Result = operation.Result is TResult result ? result : default,
            Error = record.Error,
            Diagnostics = record.Diagnostics.ToList(),
            Resources = record.Resources.ToList(),
        };
    }

    private static GatewayComponent GetOperationComponent(OperationKind kind) =>
        kind is OperationKind.TargetConfig or OperationKind.TargetStartRestart
            ? GatewayComponent.Target
            : GatewayComponent.Xae;

    private static string GetOperationStage(OperationKind kind) => kind switch
    {
        OperationKind.XaeOpen => "xae.open",
        OperationKind.XaeBuild => "xae.build",
        OperationKind.Activate => "xae.activate",
        OperationKind.Synchronize => "xae.synchronize",
        OperationKind.CloseXae => "xae.close",
        OperationKind.TargetConfig => "target.config",
        OperationKind.TargetStartRestart => "target.startRestart",
        _ => "operation",
    };

    public ResourceContent GetResource(
        string uri,
        int maximumCharacters,
        long offset)
    {
        try
        {
            GatewayResourceRoute route = GatewayResourceRouter.Parse(uri);
            if (route.Kind == GatewayResourceRouteKind.CurrentGatewayLog)
            {
                return ReadCurrentLogPath(maximumCharacters, offset);
            }

            if (route.Profile is not null)
            {
                EnsureProfileIdentity(route.Profile, "resource.read");
            }

            if (route.Kind is GatewayResourceRouteKind.ProfileSources
                or GatewayResourceRouteKind.ProfileSourceFiles)
            {
                SourceManifestResourceReader reader =
                    _sourceManifestReader
                    ?? throw new GatewayOperationException(
                        ErrorCodes.GatewayNotReady,
                        "The source manifest is unavailable because "
                            + "no active profile is configured.",
                        retryable: true,
                        stage: "profile.sources.read",
                        component: GatewayComponent.Profile);
                return route.Kind == GatewayResourceRouteKind.ProfileSourceFiles
                    ? reader.ReadFiles(
                        route.Profile!,
                        maximumCharacters,
                        offset)
                    : reader.ReadManifest(
                        route.Profile!,
                        maximumCharacters,
                        offset);
            }

            if (route.Kind == GatewayResourceRouteKind.OperationArtifact)
            {
                return _logs.Read(
                    route.CanonicalUri,
                    maximumCharacters,
                    offset);
            }

            object value = ReadStructuredResource(route);
            return SerializeResource(
                route.CanonicalUri,
                value,
                maximumCharacters,
                offset);
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

    private object ReadStructuredResource(GatewayResourceRoute route)
    {
        switch (route.Kind)
        {
            case GatewayResourceRouteKind.GatewayState:
                GatewayStateSnapshot state = _status.Read();
                state.JournalId = _eventJournal.JournalId;
                state.LatestEventCursor = _eventJournal.LatestCursor;
                state.ObservedAtUtc = _clock.UtcNow;
                return state;
            case GatewayResourceRouteKind.GatewayDiagnostics:
                return new GatewayDiagnosticsSnapshot
                {
                    Events = ReadEvents(GatewayComponent.Gateway),
                };
            case GatewayResourceRouteKind.ProfileCapabilities:
                return new ProfileCapabilitiesSnapshot
                {
                    Profile = route.Profile!,
                    Capabilities = (_capabilitySnapshots
                            ?? throw ResourceUnavailable(
                                "Profile capability snapshots are unavailable.",
                                "profile.capabilities.read",
                                GatewayComponent.Profile))
                        .ReadProfile(route.Profile!)
                        .ToList(),
                    ObservedAtUtc = _clock.UtcNow,
                };
            case GatewayResourceRouteKind.XaeState:
                return (_xaeStateProvider
                        ?? throw ResourceUnavailable(
                            "XAE state is unavailable.",
                            "xae.state.read",
                            GatewayComponent.Xae))();
            case GatewayResourceRouteKind.XaeDiagnostics:
                XaeDiagnosticsSnapshot xaeDiagnostics =
                    (_xaeDiagnosticsProvider
                        ?? throw ResourceUnavailable(
                            "XAE diagnostics are unavailable.",
                            "xae.diagnostics.read",
                            GatewayComponent.Xae))();
                xaeDiagnostics.Events = ReadEvents(
                    GatewayComponent.Xae,
                    route.Profile);
                return xaeDiagnostics;
            case GatewayResourceRouteKind.XaeMessages:
                return (_xaeMessagesSnapshotProvider
                        ?? throw ResourceUnavailable(
                            "The current XAE Error List snapshot is unavailable.",
                            "xae.messages.read",
                            GatewayComponent.Xae))();
            case GatewayResourceRouteKind.TargetState:
                return RequireObservations().Target;
            case GatewayResourceRouteKind.TargetDiagnostics:
                ProfileObservationSnapshot target = RequireObservations();
                return new TargetDiagnosticsSnapshot
                {
                    Profile = route.Profile!,
                    Target = target.Target,
                    XaeObserved = target.Xae,
                    Divergence = target.Divergence,
                    Events = ReadEvents(
                        GatewayComponent.Target,
                        route.Profile),
                };
            case GatewayResourceRouteKind.PlcState:
                return FindRuntime(route.RuntimeId!);
            case GatewayResourceRouteKind.PlcDiagnostics:
                return new PlcDiagnosticsSnapshot
                {
                    Profile = route.Profile!,
                    RuntimeId = route.RuntimeId!,
                    Runtime = FindRuntime(route.RuntimeId!),
                    Events = ReadEvents(
                        GatewayComponent.Plc,
                        route.Profile),
                };
            case GatewayResourceRouteKind.OperationSummary:
                return RequireOperation(route.OperationId!).Summary;
            case GatewayResourceRouteKind.OperationEvents:
                RequireOperation(route.OperationId!);
                return _eventJournal.ReadAfter(
                    _eventJournal.JournalId,
                    0,
                    200,
                    operationId: route.OperationId);
            default:
                throw new GatewayOperationException(
                    ErrorCodes.ResourceNotFound,
                    $"Resource '{route.CanonicalUri}' was not found.",
                    stage: "resource.read",
                    component: GatewayComponent.Gateway,
                    sideEffectsStarted: false);
        }
    }

    private OperationEventPage ReadEvents(
        GatewayComponent component,
        string? profile = null) =>
        _eventJournal.ReadAfter(
            _eventJournal.JournalId,
            0,
            200,
            component: component,
            profile: profile);

    private ProfileObservationSnapshot RequireObservations() =>
        (_profileObservations
            ?? throw ResourceUnavailable(
                "Profile observations are unavailable.",
                "target.state.read",
                GatewayComponent.Target)).Read();

    private PlcRuntimeObservation FindRuntime(string runtimeId)
    {
        PlcRuntimeObservation? runtime = RequireObservations()
            .PlcRuntimes
            .FirstOrDefault(candidate => string.Equals(
                candidate.RuntimeId,
                runtimeId,
                StringComparison.OrdinalIgnoreCase));
        return runtime
            ?? throw new GatewayOperationException(
                ErrorCodes.ResourceNotFound,
                $"PLC runtime '{runtimeId}' was not found.",
                stage: "plc.state.read",
                component: GatewayComponent.Plc,
                sideEffectsStarted: false);
    }

    private StoredOperation RequireOperation(string operationId) =>
        _operations.Get(operationId)
        ?? throw new GatewayOperationException(
            ErrorCodes.ResourceNotFound,
            $"Operation '{operationId}' was not found.",
            stage: "operation.resource.read",
            component: GatewayComponent.Gateway,
            sideEffectsStarted: false);

    private static GatewayOperationException ResourceUnavailable(
        string message,
        string stage,
        GatewayComponent component) =>
        new(
            ErrorCodes.GatewayNotReady,
            message,
            retryable: true,
            stage: stage,
            component: component,
            sideEffectsStarted: false);

    private static ResourceContent SerializeResource(
        string uri,
        object value,
        int maximumCharacters,
        long offset)
    {
        if (maximumCharacters <= 0
            || maximumCharacters > MaximumResourceCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        string json = JsonSerializer.Serialize(
            value,
            value.GetType(),
            ResourceJsonOptions);
        int start = offset >= json.Length ? json.Length : (int)offset;
        int length = Math.Min(maximumCharacters, json.Length - start);
        bool truncated = start + length < json.Length;
        return new ResourceContent
        {
            Uri = uri,
            ContentType = "application/json",
            Content = json.Substring(start, length),
            Offset = offset,
            NextOffset = truncated ? offset + length : null,
            Truncated = truncated,
        };
    }

    private static JsonSerializerOptions CreateResourceJsonOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private void EnsureProfileIdentity(string profile, string stage)
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
            stage: stage,
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
        _status.Update(status =>
        {
            status.State = GatewayProcessState.Busy;
            status.CurrentOperationId = operationId;
            status.ObservedAtUtc = _clock.UtcNow;
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
            return OperationExecutionResult.Success(result);
        }
        finally
        {
            _status.Update(status =>
            {
                status.CurrentOperationId = null;
                status.State = GatewayProcessState.Ready;
                status.ObservedAtUtc = _clock.UtcNow;
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
        _status.Update(status =>
        {
            status.State = GatewayProcessState.Busy;
            status.CurrentOperationId = operationId;
            status.ObservedAtUtc = _clock.UtcNow;
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
                status.CurrentOperationId = null;
                status.State = GatewayProcessState.Ready;
                status.ObservedAtUtc = _clock.UtcNow;
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
        _status.Update(status =>
        {
            status.State = GatewayProcessState.Busy;
            status.CurrentOperationId = operationId;
            status.ObservedAtUtc = _clock.UtcNow;
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
                status.CurrentOperationId = null;
                status.State = GatewayProcessState.Ready;
                status.ObservedAtUtc = _clock.UtcNow;
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
        if (preparation is not null)
        {
            preparation.RootOperationKind =
                OperationKind.TargetStartRestart;
        }
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
            Profile = source.Profile,
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
