using System;
using System.Linq;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class GatewayEventJournalTests
{
    [Fact]
    public void ReadingDoesNotMutateClientCursorOrGlobalStatus()
    {
        GatewayEventJournal journal = new();
        journal.Record(
            Event("gateway.started"),
            new DateTimeOffset(
                2026,
                7,
                28,
                1,
                0,
                0,
                TimeSpan.Zero));

        OperationEventPage first = journal.ReadAfter(null, 0, 100);
        OperationEventPage second = journal.ReadAfter(null, 0, 100);

        Assert.Equal(1, journal.LatestCursor);
        Assert.Equal(1, Assert.Single(first.Events).Cursor);
        Assert.Equal(1, Assert.Single(second.Events).Cursor);
        Assert.Equal(1, first.NextCursor);
        Assert.Equal(1, first.LatestCursor);
        Assert.False(first.HasMore);
        Assert.False(first.HistoryTruncated);
    }

    [Fact]
    public void PagesEventsInMonotonicCursorOrder()
    {
        GatewayEventJournal journal = new();
        for (int index = 1; index <= 3; index++)
        {
            journal.Record(
                Event($"event.{index}"),
                DateTimeOffset.UtcNow);
        }

        OperationEventPage first = journal.ReadAfter(null, 0, 2);
        OperationEventPage second = journal.ReadAfter(
            first.JournalId,
            first.NextCursor,
            2);

        Assert.Equal(new long[] { 1, 2 }, first.Events
            .Select(gatewayEvent => gatewayEvent.Cursor));
        Assert.True(first.HasMore);
        Assert.Equal(2, first.NextCursor);
        Assert.Equal(3, Assert.Single(second.Events).Cursor);
        Assert.False(second.HasMore);
        Assert.Equal(3, second.NextCursor);
    }

    [Fact]
    public void ErrorFilterAdvancesAcrossNonMatchingEvents()
    {
        GatewayEventJournal journal = new();
        journal.Record(
            Event("build.started"),
            DateTimeOffset.UtcNow);
        journal.Record(
            Event(
                "build.failed",
                DiagnosticSeverity.Error,
                ErrorCodes.BuildFailed),
            DateTimeOffset.UtcNow);
        journal.Record(
            Event("xae.connected"),
            DateTimeOffset.UtcNow);

        OperationEventPage page = journal.ReadAfter(
            null,
            0,
            100,
            DiagnosticSeverity.Error);

        GatewayEvent error = Assert.Single(page.Events);
        Assert.Equal(2, error.Cursor);
        Assert.Equal(ErrorCodes.BuildFailed, error.Error?.Code);
        Assert.Equal(3, page.NextCursor);
        Assert.Equal(3, page.LatestCursor);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void FilteredPagingDoesNotSkipNextMatchingEvent()
    {
        GatewayEventJournal journal = new();
        journal.Record(
            Event(
                "first.error",
                DiagnosticSeverity.Error,
                "FIRST"),
            DateTimeOffset.UtcNow);
        journal.Record(
            Event("between"),
            DateTimeOffset.UtcNow);
        journal.Record(
            Event(
                "second.error",
                DiagnosticSeverity.Error,
                "SECOND"),
            DateTimeOffset.UtcNow);

        OperationEventPage first = journal.ReadAfter(
            null,
            0,
            1,
            DiagnosticSeverity.Error);
        OperationEventPage second = journal.ReadAfter(
            first.JournalId,
            first.NextCursor,
            1,
            DiagnosticSeverity.Error);

        Assert.Equal(1, Assert.Single(first.Events).Cursor);
        Assert.True(first.HasMore);
        Assert.Equal(1, first.NextCursor);
        Assert.Equal(3, Assert.Single(second.Events).Cursor);
        Assert.False(second.HasMore);
        Assert.Equal(3, second.NextCursor);
    }

    [Fact]
    public void ReportsRetentionGapAndReturnsOldestRetainedEvent()
    {
        GatewayEventJournal journal = new(capacity: 2);
        journal.Record(Event("first"), DateTimeOffset.UtcNow);
        journal.Record(Event("second"), DateTimeOffset.UtcNow);
        journal.Record(Event("third"), DateTimeOffset.UtcNow);

        OperationEventPage page = journal.ReadAfter(null, 0, 100);

        Assert.True(page.HistoryTruncated);
        Assert.Equal(
            new long[] { 2, 3 },
            page.Events.Select(gatewayEvent =>
                gatewayEvent.Cursor));
        Assert.Equal(3, page.NextCursor);
    }

    [Fact]
    public void StreamIdMismatchResetsToCurrentJournal()
    {
        GatewayEventJournal journal = new();
        journal.Record(Event("current"), DateTimeOffset.UtcNow);

        OperationEventPage page = journal.ReadAfter(
            "previous-stream",
            1,
            100);

        Assert.True(page.HistoryTruncated);
        Assert.Equal(1, Assert.Single(page.Events).Cursor);
        Assert.Equal(1, page.NextCursor);
        Assert.NotEqual("previous-stream", page.JournalId);
    }

    private static GatewayEvent Event(
        string type,
        DiagnosticSeverity severity =
            DiagnosticSeverity.Info,
        string? errorCode = null)
    {
        return new GatewayEvent
        {
            Type = type,
            Severity = severity,
            Message = type,
            Error = errorCode is null
                ? null
                : new GatewayError
                {
                    Code = errorCode,
                    Message = type,
                },
        };
    }
}
