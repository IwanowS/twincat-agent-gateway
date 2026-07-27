using System;
using System.Collections.Generic;
using System.Linq;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class GatewayErrorPage
{
    public IReadOnlyList<GatewayErrorEntry> Errors { get; set; } =
        Array.Empty<GatewayErrorEntry>();

    public long NextCursor { get; set; }

    public bool MoreAvailable { get; set; }

    public bool HistoryTruncated { get; set; }
}

public interface IGatewayErrorSink
{
    long Record(
        GatewayError gatewayError,
        DateTimeOffset occurredAtUtc);
}

public sealed class GatewayErrorJournal : IGatewayErrorSink
{
    private readonly object _sync = new();
    private readonly LinkedList<GatewayErrorEntry> _entries = new();
    private readonly GatewayStatusSnapshotStore _status;
    private readonly int _capacity;
    private long _latestCursor;

    public GatewayErrorJournal(
        GatewayStatusSnapshotStore status,
        int capacity = 500)
    {
        _status = status
            ?? throw new ArgumentNullException(nameof(status));
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public long Record(
        GatewayError gatewayError,
        DateTimeOffset occurredAtUtc)
    {
        if (gatewayError is null)
        {
            throw new ArgumentNullException(
                nameof(gatewayError));
        }

        long cursor;
        lock (_sync)
        {
            cursor = checked(++_latestCursor);
            _entries.AddLast(new GatewayErrorEntry
            {
                Cursor = cursor,
                OccurredAtUtc = occurredAtUtc,
                Error = CloneError(gatewayError),
            });
            while (_entries.Count > _capacity)
            {
                _entries.RemoveFirst();
            }
        }

        _status.Update(status =>
        {
            status.LatestErrorCursor = Math.Max(
                status.LatestErrorCursor,
                cursor);
            return status;
        });
        return cursor;
    }

    public GatewayErrorPage ReadAfter(
        long afterCursor,
        int maximumCount)
    {
        if (afterCursor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterCursor));
        }

        if (maximumCount <= 0
            || maximumCount == int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        lock (_sync)
        {
            long oldestCursor = _entries.First?.Value.Cursor
                ?? checked(_latestCursor + 1);
            bool reset = afterCursor > _latestCursor;
            bool retentionGap = _entries.Count != 0
                && afterCursor < oldestCursor - 1;
            long effectiveCursor = reset || retentionGap
                ? oldestCursor - 1
                : afterCursor;
            GatewayErrorEntry[] matching = _entries
                .Where(entry => entry.Cursor > effectiveCursor)
                .Take(checked(maximumCount + 1))
                .Select(CloneEntry)
                .ToArray();
            bool moreAvailable = matching.Length > maximumCount;
            GatewayErrorEntry[] page = matching
                .Take(maximumCount)
                .ToArray();
            long nextCursor = page.Length == 0
                ? Math.Min(effectiveCursor, _latestCursor)
                : page[page.Length - 1].Cursor;
            return new GatewayErrorPage
            {
                Errors = page,
                NextCursor = nextCursor,
                MoreAvailable = moreAvailable,
                HistoryTruncated = reset || retentionGap,
            };
        }
    }

    private static GatewayErrorEntry CloneEntry(
        GatewayErrorEntry source)
    {
        return new GatewayErrorEntry
        {
            Cursor = source.Cursor,
            OccurredAtUtc = source.OccurredAtUtc,
            Error = CloneError(source.Error),
        };
    }

    private static GatewayError CloneError(GatewayError source)
    {
        return new GatewayError
        {
            Code = source.Code,
            Message = source.Message,
            Retryable = source.Retryable,
            OperationId = source.OperationId,
            Stage = source.Stage,
            RawLogRef = source.RawLogRef,
        };
    }
}
