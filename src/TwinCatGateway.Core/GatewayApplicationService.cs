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

public sealed class GatewayApplicationService
{
    private readonly string _version;
    private readonly GatewayStatusSnapshotStore _status;
    private readonly OperationStore _operations;
    private readonly OperationQueue _queue;
    private readonly LocalLogStore _logs;
    private readonly Func<GatewayDiagnosticsResult>? _diagnosticsProvider;
    private readonly BuildOperationExecutor? _buildExecutor;
    private readonly ActivationOperationExecutor? _activationExecutor;
    private readonly TcUnitPreparationExecutor?
        _tcUnitPreparationExecutor;
    private readonly TcUnitOperationExecutor?
        _tcUnitExecutor;
    private readonly ProjectProfile? _activeProfile;
    private readonly IClock _clock;
    private readonly GatewayEventJournal _eventJournal;

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
        ProjectProfile? activeProfile = null,
        IClock? clock = null,
        TcUnitPreparationExecutor?
            tcUnitPreparationExecutor = null,
        TcUnitOperationExecutor? tcUnitExecutor = null)
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
        _tcUnitPreparationExecutor =
            tcUnitPreparationExecutor;
        _tcUnitExecutor = tcUnitExecutor;
        _activeProfile = activeProfile;
        _clock = clock ?? SystemClock.Instance;
        _eventJournal = eventJournal
            ?? throw new ArgumentNullException(
                nameof(eventJournal));
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

        if (_activationExecutor is null
            || _activeProfile is null)
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

        if (!string.Equals(
            parameters.Profile,
            _activeProfile.Name,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayOperationException(
                ErrorCodes.ProfileNotFound,
                $"Project profile '{parameters.Profile}' is not active.",
                stage: "activation.validate");
        }

        if (!_activeProfile.AllowActivation)
        {
            throw new GatewayOperationException(
                ErrorCodes.ActivationNotAllowed,
                $"Activation is disabled for profile '{_activeProfile.Name}'.",
                stage: "activation.validate");
        }

        if (string.IsNullOrWhiteSpace(
            _activeProfile.ExpectedTarget?.AmsNetId))
        {
            throw new GatewayOperationException(
                ErrorCodes.ProfileInvalid,
                "The activation profile has no expected AMS NetId.",
                stage: "activation.validate");
        }

        if (parameters.TimeoutSeconds.HasValue
            && parameters.TimeoutSeconds.Value <= 0)
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Activation timeout must be positive.",
                stage: "activation.validate");
        }

        bool waitForTcUnit = parameters.WaitForTcUnit
            ?? _activeProfile.AutoWaitForTcUnit;
        if (waitForTcUnit
            && _activeProfile.TcUnit is null)
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

        ValidateRecentBuild(_activeProfile);
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
            bool waitForTcUnit =
                parameters.WaitForTcUnit
                ?? _activeProfile!.AutoWaitForTcUnit;
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
                    _activeProfile!.TcUnit!
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
                            timeoutSeconds));
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
                    Code =
                        ErrorCodes.ActivateConfigurationFailed,
                    Message =
                        "TwinCAT activation did not satisfy its postconditions.",
                    Stage = "activation.verify",
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

    private void ValidateRecentBuild(ProjectProfile profile)
    {
        if (!profile.RequireRecentSuccessfulBuild)
        {
            return;
        }

        StoredOperation? latestBuild = _operations
            .GetRecent(500)
            .FirstOrDefault(operation =>
                operation.Summary.Kind
                    == OperationKind.Build
                && operation.Summary.CompletedAtUtc.HasValue);
        BuildResult? result =
            latestBuild?.Result as BuildResult;
        DateTimeOffset oldestAllowed =
            _clock.UtcNow.AddSeconds(
                -profile.RecentBuildMaxAgeSeconds);
        bool acceptable = latestBuild is not null
            && latestBuild.Summary.State
                == OperationState.Succeeded
            && latestBuild.Summary.CompletedAtUtc
                >= oldestAllowed
            && result?.Ok == true
            && result.Action != BuildAction.Clean;
        if (!acceptable)
        {
            throw new GatewayOperationException(
                ErrorCodes.RecentBuildRequired,
                "Activation requires the latest build operation to be "
                    + "a recent successful Build or Rebuild.",
                stage: "activation.validate");
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
            Detail = source.Detail,
            TimeoutSeconds = source.TimeoutSeconds,
        };
    }

    private static ActivateParameters CloneActivateParameters(
        ActivateParameters source)
    {
        return new ActivateParameters
        {
            Profile = source.Profile,
            WaitForTcUnit = source.WaitForTcUnit,
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
