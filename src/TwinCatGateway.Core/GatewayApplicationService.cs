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

public sealed class GatewayApplicationService
{
    private readonly string _version;
    private readonly GatewayStatusSnapshotStore _status;
    private readonly OperationStore _operations;
    private readonly OperationQueue _queue;
    private readonly LocalLogStore _logs;
    private readonly Func<GatewayDiagnosticsResult>? _diagnosticsProvider;
    private readonly BuildOperationExecutor? _buildExecutor;
    private readonly GatewayErrorJournal _errorJournal;

    public GatewayApplicationService(
        string version,
        GatewayStatusSnapshotStore status,
        OperationStore operations,
        OperationQueue queue,
        LocalLogStore logs,
        GatewayErrorJournal errorJournal,
        Func<GatewayDiagnosticsResult>? diagnosticsProvider = null,
        BuildOperationExecutor? buildExecutor = null)
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
        _errorJournal = errorJournal
            ?? throw new ArgumentNullException(
                nameof(errorJournal));
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
        if (parameters.AfterErrorCursor < 0)
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "The error cursor cannot be negative.",
                stage: "diagnostics.validate");
        }

        if (parameters.MaximumErrors <= 0
            || parameters.MaximumErrors > 200)
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Maximum errors must be between 1 and 200.",
                stage: "diagnostics.validate");
        }

        GatewayErrorPage errors = _errorJournal.ReadAfter(
            parameters.AfterErrorCursor,
            parameters.MaximumErrors);
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
        result.Errors = errors.Errors.ToList();
        result.NextErrorCursor = errors.NextCursor;
        result.MoreErrorsAvailable = errors.MoreAvailable;
        result.ErrorHistoryTruncated =
            errors.HistoryTruncated;
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
}
