using System;
using System.Collections.Generic;
using System.Linq;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class OperationStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, StoredOperation> _operations =
        new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();
    private readonly int _capacity;

    public OperationStore(int capacity = 500)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public void AddQueued(
        string operationId,
        OperationKind kind,
        string? profile,
        DateTimeOffset queuedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new ArgumentException("Operation ID is required.", nameof(operationId));
        }

        lock (_sync)
        {
            if (_operations.ContainsKey(operationId))
            {
                throw new InvalidOperationException(
                    $"Operation '{operationId}' already exists.");
            }

            OperationRecord summary = new()
            {
                OperationId = operationId,
                Kind = kind,
                Profile = profile,
                State = OperationState.Queued,
                QueuedAtUtc = queuedAtUtc,
            };
            _operations.Add(operationId, new StoredOperation(summary, null));
            _order.AddLast(operationId);
            TrimCompletedOperations();
        }
    }

    public bool TryMarkRunning(string operationId, DateTimeOffset startedAtUtc)
    {
        lock (_sync)
        {
            if (!_operations.TryGetValue(operationId, out StoredOperation? current)
                || current.Summary.State != OperationState.Queued)
            {
                return false;
            }

            OperationRecord summary = CloneSummary(current.Summary);
            summary.State = OperationState.Running;
            summary.StartedAtUtc = startedAtUtc;
            _operations[operationId] = new StoredOperation(summary, current.Result);
            return true;
        }
    }

    public bool TryComplete(
        string operationId,
        OperationState finalState,
        DateTimeOffset completedAtUtc,
        object? result = null,
        GatewayError? error = null,
        IReadOnlyList<OperationDiagnostic>? diagnostics = null,
        IReadOnlyList<ResourceReference>? resources = null)
    {
        if (!IsFinal(finalState))
        {
            throw new ArgumentOutOfRangeException(
                nameof(finalState),
                "Operation state must be terminal.");
        }

        lock (_sync)
        {
            if (!_operations.TryGetValue(operationId, out StoredOperation? current)
                || IsFinal(current.Summary.State))
            {
                return false;
            }

            OperationRecord summary = CloneSummary(current.Summary);
            summary.State = finalState;
            summary.CompletedAtUtc = completedAtUtc;
            summary.DurationMs = CalculateDurationMs(summary);
            summary.Error = CloneError(error);
            summary.Diagnostics = diagnostics?.Select(CloneDiagnostic).ToList()
                ?? new();
            summary.Resources = resources?.Select(CloneResource).ToList() ?? new();
            _operations[operationId] = new StoredOperation(summary, result);
            TrimCompletedOperations();
            return true;
        }
    }

    public StoredOperation? Get(string operationId)
    {
        lock (_sync)
        {
            return _operations.TryGetValue(operationId, out StoredOperation? operation)
                ? CloneOperation(operation)
                : null;
        }
    }

    public IReadOnlyList<StoredOperation> GetRecent(int maximumCount)
    {
        if (maximumCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        lock (_sync)
        {
            return _order
                .Reverse()
                .Take(maximumCount)
                .Select(operationId => CloneOperation(_operations[operationId]))
                .ToArray();
        }
    }

    private static bool IsFinal(OperationState state)
    {
        return state == OperationState.Succeeded
            || state == OperationState.Failed
            || state == OperationState.TimedOut
            || state == OperationState.Cancelled;
    }

    private void TrimCompletedOperations()
    {
        LinkedListNode<string>? current = _order.First;
        while (_operations.Count > _capacity && current is not null)
        {
            LinkedListNode<string>? next = current.Next;
            StoredOperation operation = _operations[current.Value];
            if (IsFinal(operation.Summary.State))
            {
                _operations.Remove(current.Value);
                _order.Remove(current);
            }

            current = next;
        }
    }

    private static StoredOperation CloneOperation(StoredOperation operation)
    {
        return new StoredOperation(CloneSummary(operation.Summary), operation.Result);
    }

    private static OperationRecord CloneSummary(OperationRecord source)
    {
        return new OperationRecord
        {
            OperationId = source.OperationId,
            Kind = source.Kind,
            Profile = source.Profile,
            State = source.State,
            QueuedAtUtc = source.QueuedAtUtc,
            StartedAtUtc = source.StartedAtUtc,
            CompletedAtUtc = source.CompletedAtUtc,
            DurationMs = source.DurationMs,
            Error = CloneError(source.Error),
            Diagnostics = source.Diagnostics.Select(CloneDiagnostic).ToList(),
            Resources = source.Resources.Select(CloneResource).ToList(),
        };
    }

    private static long? CalculateDurationMs(OperationRecord summary)
    {
        DateTimeOffset? start = summary.StartedAtUtc ?? summary.QueuedAtUtc;
        return summary.CompletedAtUtc.HasValue
            ? Math.Max(
                0,
                (long)(summary.CompletedAtUtc.Value - start.Value)
                    .TotalMilliseconds)
            : null;
    }

    private static OperationDiagnostic CloneDiagnostic(
        OperationDiagnostic source)
    {
        return new OperationDiagnostic
        {
            Code = source.Code,
            Component = source.Component,
            Stage = source.Stage,
            Severity = source.Severity,
            Message = source.Message,
            OccurredAtUtc = source.OccurredAtUtc,
            Resources = source.Resources.Select(CloneResource).ToList(),
        };
    }

    private static GatewayError? CloneError(GatewayError? source)
    {
        return source is null
            ? null
            : new GatewayError
            {
                Code = source.Code,
                Message = source.Message,
                Details = source.Details,
                Retryable = source.Retryable,
                OperationId = source.OperationId,
                Stage = source.Stage,
                RawLogRef = source.RawLogRef,
            };
    }

    private static ResourceReference CloneResource(ResourceReference source)
    {
        return new ResourceReference
        {
            Uri = source.Uri,
            MimeType = source.MimeType,
        };
    }
}
