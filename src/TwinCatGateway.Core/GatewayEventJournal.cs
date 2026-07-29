using System;
using System.Collections.Generic;
using System.Linq;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class GatewayEventPage
{
    public IReadOnlyList<GatewayEvent> Events { get; set; } =
        Array.Empty<GatewayEvent>();

    public string EventStreamId { get; set; } = string.Empty;

    public long NextScanCursor { get; set; }

    public long LatestCursor { get; set; }

    public bool MoreMatchingEventsAvailable { get; set; }

    public bool HistoryTruncated { get; set; }
}

public interface IGatewayEventSink
{
    long Record(
        GatewayEvent gatewayEvent,
        DateTimeOffset occurredAtUtc);
}

public sealed class GatewayEventJournal : IGatewayEventSink
{
    private readonly object _sync = new();
    private readonly LinkedList<GatewayEvent> _events = new();
    private readonly GatewayStatusSnapshotStore _status;
    private readonly int _capacity;
    private readonly string _eventStreamId;
    private long _latestCursor;

    public GatewayEventJournal(
        GatewayStatusSnapshotStore status,
        int capacity = 1000)
    {
        _status = status
            ?? throw new ArgumentNullException(nameof(status));
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _eventStreamId = Guid.NewGuid().ToString("N");
        _status.Update(snapshot =>
        {
            snapshot.EventStreamId = _eventStreamId;
            snapshot.LatestEventCursor = 0;
            return snapshot;
        });
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

        _status.Update(status =>
        {
            status.LatestEventCursor = Math.Max(
                status.LatestEventCursor,
                cursor);
            return status;
        });
        return cursor;
    }

    public GatewayEventPage ReadAfter(
        string? eventStreamId,
        long afterCursor,
        int maximumCount,
        DiagnosticSeverity? minimumSeverity = null)
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
                            >= minimumSeverity.Value))
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
            return new GatewayEventPage
            {
                Events = page,
                EventStreamId = _eventStreamId,
                NextScanCursor = nextScanCursor,
                LatestCursor = _latestCursor,
                MoreMatchingEventsAvailable =
                    moreAvailable,
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
            Severity = source.Severity,
            OperationId = source.OperationId,
            OperationKind = source.OperationKind,
            Stage = source.Stage,
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
            OperationId = source.OperationId,
            Kind = source.Kind,
        };
    }
}
