using System;
using System.Collections.Generic;
using System.Linq;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class GatewayEventJournal : IGatewayEventSink
{
    private readonly object _sync = new();
    private readonly LinkedList<GatewayEvent> _events = new();
    private readonly int _capacity;
    private readonly string _eventStreamId;
    private long _latestCursor;

    public GatewayEventJournal(
        int capacity = 1000)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _eventStreamId = Guid.NewGuid().ToString("N");
    }

    public string JournalId => _eventStreamId;

    public long LatestCursor
    {
        get
        {
            lock (_sync)
            {
                return _latestCursor;
            }
        }
    }

    public long Record(
        GatewayEvent gatewayEvent,
        DateTimeOffset occurredAtUtc)
    {
        if (gatewayEvent is null)
        {
            throw new ArgumentNullException(nameof(gatewayEvent));
        }

        if (string.IsNullOrWhiteSpace(gatewayEvent.Type))
        {
            throw new ArgumentException(
                "Gateway event type is required.",
                nameof(gatewayEvent));
        }

        if (!Enum.IsDefined(
            typeof(DiagnosticSeverity),
            gatewayEvent.Severity))
        {
            throw new ArgumentOutOfRangeException(
                nameof(gatewayEvent));
        }

        long cursor;
        lock (_sync)
        {
            cursor = checked(++_latestCursor);
            GatewayEvent stored = CloneEvent(gatewayEvent);
            stored.Cursor = cursor;
            stored.OccurredAtUtc = occurredAtUtc;
            _events.AddLast(stored);
            while (_events.Count > _capacity)
            {
                _events.RemoveFirst();
            }
        }

        return cursor;
    }

    public OperationEventPage ReadAfter(
        string? eventStreamId,
        long afterCursor,
        int maximumCount,
        DiagnosticSeverity? minimumSeverity = null,
        GatewayComponent? component = null,
        string? profile = null,
        string? operationId = null)
    {
        if (afterCursor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterCursor));
        }

        if (maximumCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        if (minimumSeverity.HasValue
            && !Enum.IsDefined(
                typeof(DiagnosticSeverity),
                minimumSeverity.Value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumSeverity));
        }

        lock (_sync)
        {
            long oldestCursor = _events.First?.Value.Cursor
                ?? checked(_latestCursor + 1);
            bool streamReset = eventStreamId is not null
                && !string.Equals(
                    eventStreamId,
                    _eventStreamId,
                    StringComparison.Ordinal);
            bool reset = streamReset
                || afterCursor > _latestCursor;
            bool retentionGap = _events.Count != 0
                && afterCursor < oldestCursor - 1;
            long effectiveCursor = reset || retentionGap
                ? oldestCursor - 1
                : afterCursor;
            GatewayEvent[] matching = _events
                .Where(gatewayEvent =>
                    gatewayEvent.Cursor > effectiveCursor
                    && (!minimumSeverity.HasValue
                        || gatewayEvent.Severity
                            >= minimumSeverity.Value)
                    && (!component.HasValue
                        || gatewayEvent.Component == component.Value)
                    && (profile is null
                        || string.Equals(
                            gatewayEvent.Profile,
                            profile,
                            StringComparison.Ordinal))
                    && (operationId is null
                        || string.Equals(
                            gatewayEvent.OperationId,
                            operationId,
                            StringComparison.Ordinal)))
                .Select(CloneEvent)
                .ToArray();
            bool moreAvailable =
                matching.Length > maximumCount;
            GatewayEvent[] page = matching
                .Take(maximumCount)
                .ToArray();
            long nextScanCursor = moreAvailable
                ? page[page.Length - 1].Cursor
                : _latestCursor;
            return new OperationEventPage
            {
                Events = page.ToList(),
                JournalId = _eventStreamId,
                NextCursor = nextScanCursor,
                LatestCursor = _latestCursor,
                HasMore = moreAvailable,
                HistoryTruncated = reset || retentionGap,
            };
        }
    }

    private static GatewayEvent CloneEvent(
        GatewayEvent source)
    {
        return new GatewayEvent
        {
            Cursor = source.Cursor,
            OccurredAtUtc = source.OccurredAtUtc,
            Type = source.Type,
            Profile = source.Profile,
            Component = source.Component,
            Severity = source.Severity,
            OperationId = source.OperationId,
            OperationKind = source.OperationKind,
            Stage = source.Stage,
            Code = source.Code,
            Message = source.Message,
            Error = CloneError(source.Error),
            Resources = source.Resources
                .Select(CloneResource)
                .ToList(),
            Properties = source.Properties.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal),
        };
    }

    private static GatewayError? CloneError(
        GatewayError? source)
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

    private static ResourceReference CloneResource(
        ResourceReference source)
    {
        return new ResourceReference
        {
            Uri = source.Uri,
            MimeType = source.MimeType,
        };
    }
}
