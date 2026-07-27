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
        GatewayStatusSnapshotStore status = CreateStatus();
        GatewayEventJournal journal = new(status);
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

        GatewayEventPage first = journal.ReadAfter(null, 0, 100);
        GatewayEventPage second = journal.ReadAfter(null, 0, 100);

        Assert.Equal(1, status.Read().LatestEventCursor);
        Assert.Equal(1, Assert.Single(first.Events).Cursor);
        Assert.Equal(1, Assert.Single(second.Events).Cursor);
        Assert.Equal(1, first.NextScanCursor);
        Assert.Equal(1, first.LatestCursor);
        Assert.False(first.MoreMatchingEventsAvailable);
        Assert.False(first.HistoryTruncated);
    }

    [Fact]
    public void PagesEventsInMonotonicCursorOrder()
    {
        GatewayStatusSnapshotStore status = CreateStatus();
        GatewayEventJournal journal = new(status);
        for (int index = 1; index <= 3; index++)
        {
            journal.Record(
                Event($"event.{index}"),
                DateTimeOffset.UtcNow);
        }

        GatewayEventPage first = journal.ReadAfter(null, 0, 2);
        GatewayEventPage second = journal.ReadAfter(
            first.EventStreamId,
            first.NextScanCursor,
            2);

        Assert.Equal(new long[] { 1, 2 }, first.Events
            .Select(gatewayEvent => gatewayEvent.Cursor));
        Assert.True(first.MoreMatchingEventsAvailable);
        Assert.Equal(2, first.NextScanCursor);
        Assert.Equal(3, Assert.Single(second.Events).Cursor);
        Assert.False(second.MoreMatchingEventsAvailable);
        Assert.Equal(3, second.NextScanCursor);
    }

    [Fact]
    public void ErrorFilterAdvancesAcrossNonMatchingEvents()
    {
        GatewayStatusSnapshotStore status = CreateStatus();
        GatewayEventJournal journal = new(status);
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

        GatewayEventPage page = journal.ReadAfter(
            null,
            0,
            100,
            DiagnosticSeverity.Error);

        GatewayEvent error = Assert.Single(page.Events);
        Assert.Equal(2, error.Cursor);
        Assert.Equal(ErrorCodes.BuildFailed, error.Error?.Code);
        Assert.Equal(3, page.NextScanCursor);
        Assert.Equal(3, page.LatestCursor);
        Assert.False(page.MoreMatchingEventsAvailable);
    }

    [Fact]
    public void FilteredPagingDoesNotSkipNextMatchingEvent()
    {
        GatewayStatusSnapshotStore status = CreateStatus();
        GatewayEventJournal journal = new(status);
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

        GatewayEventPage first = journal.ReadAfter(
            null,
            0,
            1,
            DiagnosticSeverity.Error);
        GatewayEventPage second = journal.ReadAfter(
            first.EventStreamId,
            first.NextScanCursor,
            1,
            DiagnosticSeverity.Error);

        Assert.Equal(1, Assert.Single(first.Events).Cursor);
        Assert.True(first.MoreMatchingEventsAvailable);
        Assert.Equal(1, first.NextScanCursor);
        Assert.Equal(3, Assert.Single(second.Events).Cursor);
        Assert.False(second.MoreMatchingEventsAvailable);
        Assert.Equal(3, second.NextScanCursor);
    }

    [Fact]
    public void ReportsRetentionGapAndReturnsOldestRetainedEvent()
    {
        GatewayStatusSnapshotStore status = CreateStatus();
        GatewayEventJournal journal = new(status, capacity: 2);
        journal.Record(Event("first"), DateTimeOffset.UtcNow);
        journal.Record(Event("second"), DateTimeOffset.UtcNow);
        journal.Record(Event("third"), DateTimeOffset.UtcNow);

        GatewayEventPage page = journal.ReadAfter(null, 0, 100);

        Assert.True(page.HistoryTruncated);
        Assert.Equal(
            new long[] { 2, 3 },
            page.Events.Select(gatewayEvent =>
                gatewayEvent.Cursor));
        Assert.Equal(3, page.NextScanCursor);
    }

    [Fact]
    public void StreamIdMismatchResetsToCurrentJournal()
    {
        GatewayStatusSnapshotStore status = CreateStatus();
        GatewayEventJournal journal = new(status);
        journal.Record(Event("current"), DateTimeOffset.UtcNow);

        GatewayEventPage page = journal.ReadAfter(
            "previous-stream",
            1,
            100);

        Assert.True(page.HistoryTruncated);
        Assert.Equal(1, Assert.Single(page.Events).Cursor);
        Assert.Equal(1, page.NextScanCursor);
        Assert.NotEqual("previous-stream", page.EventStreamId);
    }

    private static GatewayStatusSnapshotStore CreateStatus()
    {
        return new GatewayStatusSnapshotStore(
            GatewayStatusSnapshotStore.CreateInitial("0.1.0"));
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
