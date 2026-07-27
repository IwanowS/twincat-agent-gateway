using System;
using System.Linq;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class GatewayErrorJournalTests
{
    [Fact]
    public void ReadingDoesNotMutateTheClientCursorOrGlobalStatus()
    {
        GatewayStatusSnapshotStore status = CreateStatus();
        GatewayErrorJournal journal = new(status);
        journal.Record(
            Error("FIRST"),
            new DateTimeOffset(
                2026,
                7,
                28,
                1,
                0,
                0,
                TimeSpan.Zero));

        GatewayErrorPage first = journal.ReadAfter(0, 50);
        GatewayErrorPage second = journal.ReadAfter(0, 50);

        Assert.Equal(1, status.Read().LatestErrorCursor);
        Assert.Equal(1, Assert.Single(first.Errors).Cursor);
        Assert.Equal(1, Assert.Single(second.Errors).Cursor);
        Assert.Equal(1, first.NextCursor);
        Assert.False(first.MoreAvailable);
        Assert.False(first.HistoryTruncated);
    }

    [Fact]
    public void PagesErrorsInMonotonicCursorOrder()
    {
        GatewayStatusSnapshotStore status = CreateStatus();
        GatewayErrorJournal journal = new(status);
        for (int index = 1; index <= 3; index++)
        {
            journal.Record(
                Error($"ERROR_{index}"),
                DateTimeOffset.UtcNow);
        }

        GatewayErrorPage first = journal.ReadAfter(0, 2);
        GatewayErrorPage second = journal.ReadAfter(
            first.NextCursor,
            2);

        Assert.Equal(new long[] { 1, 2 }, first.Errors
            .Select(entry => entry.Cursor));
        Assert.True(first.MoreAvailable);
        Assert.Equal(2, first.NextCursor);
        Assert.Equal(3, Assert.Single(second.Errors).Cursor);
        Assert.False(second.MoreAvailable);
        Assert.Equal(3, second.NextCursor);
    }

    [Fact]
    public void ReportsRetentionGapAndReturnsOldestRetainedError()
    {
        GatewayStatusSnapshotStore status = CreateStatus();
        GatewayErrorJournal journal = new(status, capacity: 2);
        journal.Record(Error("FIRST"), DateTimeOffset.UtcNow);
        journal.Record(Error("SECOND"), DateTimeOffset.UtcNow);
        journal.Record(Error("THIRD"), DateTimeOffset.UtcNow);

        GatewayErrorPage page = journal.ReadAfter(0, 50);

        Assert.True(page.HistoryTruncated);
        Assert.Equal(
            new long[] { 2, 3 },
            page.Errors.Select(entry => entry.Cursor));
        Assert.Equal(3, page.NextCursor);
    }

    [Fact]
    public void FutureCursorResetsToCurrentJournalEpoch()
    {
        GatewayStatusSnapshotStore status = CreateStatus();
        GatewayErrorJournal journal = new(status);
        journal.Record(Error("CURRENT"), DateTimeOffset.UtcNow);

        GatewayErrorPage page = journal.ReadAfter(100, 50);

        Assert.True(page.HistoryTruncated);
        Assert.Equal(1, Assert.Single(page.Errors).Cursor);
        Assert.Equal(1, page.NextCursor);
    }

    private static GatewayStatusSnapshotStore CreateStatus()
    {
        return new GatewayStatusSnapshotStore(
            GatewayStatusSnapshotStore.CreateInitial("0.1.0"));
    }

    private static GatewayError Error(string code)
    {
        return new GatewayError
        {
            Code = code,
            Message = code,
        };
    }
}
