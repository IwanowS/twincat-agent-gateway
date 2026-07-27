using System;
using System.Collections.Generic;
using System.IO;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class GatewayApplicationService
{
    private readonly string _version;
    private readonly GatewayStatusSnapshotStore _status;
    private readonly OperationStore _operations;
    private readonly OperationQueue _queue;
    private readonly LocalLogStore _logs;
    private readonly Func<GatewayDiagnosticsResult>? _diagnosticsProvider;

    public GatewayApplicationService(
        string version,
        GatewayStatusSnapshotStore status,
        OperationStore operations,
        OperationQueue queue,
        LocalLogStore logs,
        Func<GatewayDiagnosticsResult>? diagnosticsProvider = null)
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

    public GatewayDiagnosticsResult GetDiagnostics()
    {
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
        return result;
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
}
