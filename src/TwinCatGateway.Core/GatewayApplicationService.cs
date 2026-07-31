using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public delegate Task<BuildResult> BuildOperationExecutor(
    string operationId,
    BuildParameters parameters,
    CancellationToken cancellationToken);

public delegate Task<ActivationResult> ActivationOperationExecutor(
    string operationId,
    ActivateParameters parameters,
    CancellationToken cancellationToken);

public delegate Task<RecoverToConfigResult> RecoveryOperationExecutor(
    string operationId,
    RecoverToConfigParameters parameters,
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
    private readonly BuildOperationExecutor? _buildExecutor;
    private readonly ActivationOperationExecutor? _activationExecutor;
    private readonly SynchronizeOperationExecutor? _synchronizeExecutor;
    private readonly CloseXaeOperationExecutor? _closeXaeExecutor;
    private readonly RecoveryOperationExecutor? _recoveryExecutor;
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

    public GatewayApplicationService(
        string version,
        GatewayStatusSnapshotStore status,
        OperationStore operations,
        OperationQueue queue,
        LocalLogStore logs,
        GatewayEventJournal eventJournal,
        Func<GatewayDiagnosticsResult>? diagnosticsProvider = null,
        BuildOperationExecutor? buildExecutor = null,
        ActivationOperationExecutor? activationExecutor = null,
        ResolvedProfile? activeProfile = null,
        OperationCapabilityPreflight? preflight = null,
        CapabilityEvaluator? capabilities = null,
        IClock? clock = null,
        TcUnitPreparationExecutor?
            tcUnitPreparationExecutor = null,
        TcUnitOperationExecutor? tcUnitExecutor = null,
        SynchronizeOperationExecutor? synchronizeExecutor = null,
        RecoveryOperationExecutor? recoveryExecutor = null,
        XaeMessagesProvider? xaeMessagesProvider = null,
        Func<string?>? currentLogPathProvider = null,
        CloseXaeOperationExecutor? closeXaeExecutor = null,
        SourceManifestStore? sourceManifests = null)
    {
        _version = version
            ?? throw new ArgumentNullException(nameof(version));
        _status = status
            ?? throw new ArgumentNullException(nameof(status));
        _operations = operations
            ?? throw new ArgumentNullException(nameof(operations));
        _queue = queue
            ?? throw new ArgumentNullException(nameof(queue));
        _logs = logs
            ?? throw new ArgumentNullException(nameof(logs));
        _diagnosticsProvider = diagnosticsProvider;
        _buildExecutor = buildExecutor;
        _activationExecutor = activationExecutor;
        _synchronizeExecutor = synchronizeExecutor;
        _closeXaeExecutor = closeXaeExecutor;
        _recoveryExecutor = recoveryExecutor;
        _xaeMessagesProvider = xaeMessagesProvider;
        _tcUnitPreparationExecutor =
            tcUnitPreparationExecutor;
        _tcUnitExecutor = tcUnitExecutor;
        _activeProfile = activeProfile;
        _preflight = preflight;
        _capabilities = capabilities;
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

    public OperationAccepted StartBuild(BuildParameters parameters)
    {
        if (parameters is null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        if (_buildExecutor is null)
        {
            throw new GatewayOperationException(
                ErrorCodes.GatewayNotReady,
                "The XAE build executor is unavailable.",
                retryable: true,
                stage: "build.enqueue");
        }

        OperationCapabilityPreflight preflight =
            RequirePreflight("build.admission");
        ResolvedProfile profile = preflight.EnsureAllowed(
            parameters.Profile,
            CapabilityKey.XaeBuild,
            "build.admission");
        if (parameters.DiscardDirtyDocuments)
        {
            preflight.EnsureAllowed(
                profile.Name,
                CapabilityKey.XaeDiscardDirtyDocuments,
                "build.discard.admission");
        }

        if (!Enum.IsDefined(
            typeof(BuildAction),
            parameters.Action))
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Build action is not supported.",
                stage: "build.validate");
        }

        if (parameters.TimeoutSeconds.HasValue
            && parameters.TimeoutSeconds.Value <= 0)
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Build timeout must be positive.",
                stage: "build.validate");
        }

        BuildParameters captured = CloneBuildParameters(parameters);
        TimeSpan timeout = TimeSpan.FromSeconds(
            captured.TimeoutSeconds ?? 120);
        return _queue.Enqueue(
            OperationKind.Build,
            (operationId, cancellationToken) =>
                ExecuteBuildAsync(
                    operationId,
                    captured,
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

        if (parameters.TimeoutSeconds.HasValue
            && parameters.TimeoutSeconds.Value <= 0)
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Activation timeout must be positive.",
                stage: "activation.validate");
        }

        bool waitForTcUnit = parameters.WaitForTcUnit ?? false;
        if (waitForTcUnit
            && !parameters.RunAfterActivation)
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "TcUnit waiting requires runAfterActivation=true.",
                stage: "activation.validate");
        }

        if (waitForTcUnit
            && profile.Target?.TcUnit is null)
        {
            throw new GatewayOperationException(
                ErrorCodes.ProfileInvalid,
                "TcUnit waiting is enabled but no TcUnit profile is configured.",
                stage: "activation.validate");
        }

        if (waitForTcUnit
            && (_tcUnitPreparationExecutor is null
                || _tcUnitExecutor is null))
        {
            throw new GatewayOperationException(
                ErrorCodes.GatewayNotReady,
                "The linked TcUnit executor is unavailable.",
                retryable: true,
                stage: "activation.tcunit");
        }

        if (waitForTcUnit)
        {
            preflight.EnsureAllowed(
                profile.Name,
                CapabilityKey.TargetTcUnitVerification,
                "activation.tcunit.admission",
                requireTarget: true);
        }

        TwinCatStatus runtimeStatus =
            _status.Read().TwinCat;
        RuntimeOperationPolicy.EnsureActivationAllowed(
            runtimeStatus.Mode,
            details: runtimeStatus.Alert?.Details);
        ActivateParameters captured =
            CloneActivateParameters(parameters);
        TimeSpan timeout = TimeSpan.FromSeconds(
            captured.TimeoutSeconds ?? 120);
        return _queue.Enqueue(
            OperationKind.Activate,
            (operationId, cancellationToken) =>
                ExecuteActivationAsync(
                    operationId,
                    captured,
                    cancellationToken),
            timeout);
    }

    public OperationAccepted StartRecoverToConfig(
        RecoverToConfigParameters parameters)
    {
        if (parameters is null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        if (_recoveryExecutor is null)
        {
            throw new GatewayOperationException(
                ErrorCodes.GatewayNotReady,
                "The TwinCAT Config recovery executor is unavailable.",
                retryable: true,
                stage: "recovery.enqueue");
        }

        if (string.IsNullOrWhiteSpace(parameters.Profile))
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Runtime recovery requires an explicit profile name.",
                stage: "recovery.validate");
        }

        RequirePreflight("recovery.admission").EnsureAllowed(
            parameters.Profile,
            CapabilityKey.TargetConfig,
            "recovery.admission",
            requireTarget: true);

        if (parameters.TimeoutSeconds.HasValue
            && parameters.TimeoutSeconds.Value <= 0)
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Recovery timeout must be positive.",
                stage: "recovery.validate");
        }

        RecoverToConfigParameters captured =
            CloneRecoverToConfigParameters(parameters);
        return _queue.Enqueue(
            OperationKind.RecoverToConfig,
            (operationId, cancellationToken) =>
                ExecuteRecoverToConfigAsync(
                    operationId,
                    captured,
                    cancellationToken),
            TimeSpan.FromSeconds(
                captured.TimeoutSeconds ?? 120));
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
        CapabilityEvaluationContext context = new(
            _status.Read().Xae.ProcessId);
        capabilities.EnsureAllowed(
            _activeProfile,
            CapabilityKey.XaeClose,
            "xae.close.admission",
            context);

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
                context);
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
            _queue.CancelBeforeStart(operationId);
        if (cancellation == OperationCancellationResult.NotFound)
        {
            throw new GatewayOperationException(
                ErrorCodes.OperationNotFound,
                $"Operation '{operationId}' was not found.");
        }

        if (cancellation == OperationCancellationResult.AlreadyStarted)
        {
            throw new GatewayOperationException(
                ErrorCodes.OperationNotCancellable,
                $"Operation '{operationId}' has already started or completed.");
        }

        return new CancelOperationResult
        {
            OperationId = operationId,
            Cancelled = true,
            State = OperationState.Cancelled,
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

    private async Task<OperationExecutionResult> ExecuteBuildAsync(
        string operationId,
        BuildParameters parameters,
        CancellationToken cancellationToken)
    {
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        _status.Update(status =>
        {
            status.Gateway.State = GatewayState.Building;
            status.CurrentOperation = new OperationSummary
            {
                OperationId = operationId,
                Kind = OperationKind.Build,
                State = OperationState.Running,
                QueuedAtUtc = startedAtUtc,
                StartedAtUtc = startedAtUtc,
            };
            return status;
        });
        try
        {
            BuildResult result = await _buildExecutor!(
                operationId,
                parameters,
                cancellationToken).ConfigureAwait(false);
            result.OperationId = operationId;
            _status.Update(status =>
            {
                status.LastBuild = new BuildSummary
                {
                    Ok = result.Ok,
                    OperationId = operationId,
                    Action = result.Action,
                    Errors = result.Counts.Errors,
                    Warnings = result.Counts.Warnings,
                };
                return status;
            });
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
                    Stage = "build.verify",
                    RawLogRef = result.Log?.Uri,
                },
                result,
                resources);
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
        ExecuteSynchronizationAsync(
            string operationId,
            SynchronizeParameters parameters,
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
            bool waitForTcUnit = parameters.WaitForTcUnit ?? false;
            TcUnitRunPreparation? preparation =
                waitForTcUnit
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
                int timeoutSeconds =
                    _activeProfile!.Target!.TcUnit!
                        .CompletionTimeoutSeconds;
                OperationAccepted test =
                    _queue.Enqueue(
                        OperationKind.Test,
                        (testOperationId, testCancellation) =>
                            ExecuteTcUnitAsync(
                                testOperationId,
                                operationId,
                                preparation,
                                testCancellation),
                        TimeSpan.FromSeconds(
                            timeoutSeconds + 5d));
                result.TestOperationId =
                    test.OperationId;
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
                new GatewayError
                {
                    Code = result.Compile?.Completed == true
                        && !result.Compile.Ok
                            ? ErrorCodes.BuildFailed
                            : ErrorCodes.ActivateConfigurationFailed,
                    Message = result.Compile?.Completed == true
                        && !result.Compile.Ok
                            ? "XAE activation stopped because its internal "
                                + "build completed with errors."
                            : "TwinCAT activation did not satisfy its "
                                + "postconditions.",
                    Stage = result.Compile?.Completed == true
                        && !result.Compile.Ok
                            ? "activation.compile"
                            : "activation.verify",
                    RawLogRef = result.Compile?.Completed == true
                        && !result.Compile.Ok
                            ? result.Compile.Log?.Uri
                            : null,
                },
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
        ExecuteRecoverToConfigAsync(
            string operationId,
            RecoverToConfigParameters parameters,
            CancellationToken cancellationToken)
    {
        DateTimeOffset startedAtUtc = _clock.UtcNow;
        _status.Update(status =>
        {
            status.Gateway.State =
                GatewayState.RecoveringToConfig;
            status.CurrentOperation = new OperationSummary
            {
                OperationId = operationId,
                Kind = OperationKind.RecoverToConfig,
                State = OperationState.Running,
                QueuedAtUtc = startedAtUtc,
                StartedAtUtc = startedAtUtc,
            };
            return status;
        });
        try
        {
            RecoverToConfigResult result =
                await _recoveryExecutor!(
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
                status.CurrentOperation = null;
                status.Gateway.State = status.Xae.Connected
                    ? GatewayState.Ready
                    : GatewayState.Disconnected;
                return status;
            });
        }
    }

    private async Task<OperationExecutionResult>
        ExecuteTcUnitAsync(
            string operationId,
            string activationOperationId,
            TcUnitRunPreparation preparation,
            CancellationToken cancellationToken)
    {
        DateTimeOffset startedAtUtc = _clock.UtcNow;
        _status.Update(status =>
        {
            status.Gateway.State = GatewayState.Testing;
            status.CurrentOperation = new OperationSummary
            {
                OperationId = operationId,
                Kind = OperationKind.Test,
                State = OperationState.Running,
                QueuedAtUtc = startedAtUtc,
                StartedAtUtc = startedAtUtc,
            };
            return status;
        });
        try
        {
            TestResult result = await _tcUnitExecutor!(
                operationId,
                activationOperationId,
                preparation,
                cancellationToken).ConfigureAwait(false);
            result.OperationId = operationId;
            result.ActivationOperationId =
                activationOperationId;
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
            if (result.Ok)
            {
                return OperationExecutionResult.Success(
                    result,
                    resources);
            }

            return OperationExecutionResult.Failure(
                new GatewayError
                {
                    Code = ErrorCodes.TestFailed,
                    Message =
                        "TcUnit completed with failed tests.",
                    Stage = "tcunit.verify",
                    RawLogRef = result.Report?.Uri,
                },
                result,
                resources);
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

    private static BuildParameters CloneBuildParameters(
        BuildParameters source)
    {
        return new BuildParameters
        {
            Profile = source.Profile,
            Action = source.Action,
            Configuration = source.Configuration,
            Platform = source.Platform,
            ChangedPaths = source.ChangedPaths?
                .ToList()
                ?? new List<string>(),
            DiscardDirtyDocuments =
                source.DiscardDirtyDocuments,
            Detail = source.Detail,
            TimeoutSeconds = source.TimeoutSeconds,
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
            RunAfterActivation = source.RunAfterActivation,
            WaitForTcUnit = source.WaitForTcUnit,
            TimeoutSeconds = source.TimeoutSeconds,
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

    private static RecoverToConfigParameters
        CloneRecoverToConfigParameters(
            RecoverToConfigParameters source)
    {
        return new RecoverToConfigParameters
        {
            Profile = source.Profile,
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
