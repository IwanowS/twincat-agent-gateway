using System;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Xae;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class ComStaDispatcherTests
{
    [Fact]
    public async Task WorkRunsSequentiallyOnOneStaThread()
    {
        using ComStaDispatcher dispatcher = new();
        ManualResetEventSlim firstStarted = new();
        ManualResetEventSlim releaseFirst = new();
        int secondStarted = 0;

        Task<ThreadIdentity> first = dispatcher.InvokeAsync(
            () =>
            {
                firstStarted.Set();
                releaseFirst.Wait();
                return ThreadIdentity.Current();
            },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        firstStarted.Wait(TimeSpan.FromSeconds(2));
        Task<ThreadIdentity> second = dispatcher.InvokeAsync(
            () =>
            {
                Interlocked.Exchange(ref secondStarted, 1);
                return ThreadIdentity.Current();
            },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(0, Volatile.Read(ref secondStarted));
        releaseFirst.Set();
        ThreadIdentity firstIdentity = await first;
        ThreadIdentity secondIdentity = await second;

        Assert.Equal(ApartmentState.STA, firstIdentity.Apartment);
        Assert.Equal(firstIdentity.ManagedThreadId, secondIdentity.ManagedThreadId);
    }

    [Fact]
    public async Task CancellationBeforeStartPreventsQueuedDelegate()
    {
        using ComStaDispatcher dispatcher = new();
        ManualResetEventSlim firstStarted = new();
        ManualResetEventSlim releaseFirst = new();
        using CancellationTokenSource cancellation = new();
        bool executed = false;

        Task<int> first = dispatcher.InvokeAsync(
            () =>
            {
                firstStarted.Set();
                releaseFirst.Wait();
                return 1;
            },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        firstStarted.Wait(TimeSpan.FromSeconds(2));
        Task<int> queued = dispatcher.InvokeAsync(
            () =>
            {
                executed = true;
                return 2;
            },
            TimeSpan.FromSeconds(5),
            cancellation.Token);
        cancellation.Cancel();
        releaseFirst.Set();

        await first;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await queued);
        Assert.False(executed);
    }

    [Fact]
    public async Task DeadlineReturnsWithoutWaitingForBusyStaCall()
    {
        using ComStaDispatcher dispatcher = new();
        ManualResetEventSlim started = new();
        ManualResetEventSlim release = new();

        Task<int> call = dispatcher.InvokeAsync(
            () =>
            {
                started.Set();
                release.Wait();
                return 1;
            },
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);
        started.Wait(TimeSpan.FromSeconds(2));

        GatewayOperationException exception =
            await Assert.ThrowsAsync<GatewayOperationException>(
                async () => await call);
        release.Set();

        Assert.Equal(ErrorCodes.ComCallTimeout, exception.Code);
        Assert.Contains("after it started", exception.Message);
    }

    [Fact]
    public async Task FailedCallDoesNotTerminateDispatcherAndRecordsHResult()
    {
        using ComStaDispatcher dispatcher = new();
        const int hResult = unchecked((int)0x80010001);

        await Assert.ThrowsAsync<TestComException>(
            async () => await dispatcher.InvokeAsync<int>(
                () => throw new TestComException("Rejected.", hResult),
                TimeSpan.FromSeconds(2),
                CancellationToken.None));
        int result = await dispatcher.InvokeAsync(
            () => 42,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        ComDiagnostics diagnostics = dispatcher.GetDiagnostics();

        Assert.Equal(42, result);
        Assert.Equal(hResult, diagnostics.LastHResult);
    }

    private sealed class TestComException : Exception
    {
        public TestComException(string message, int hResult)
            : base(message)
        {
            HResult = hResult;
        }
    }

    private sealed class ThreadIdentity
    {
        private ThreadIdentity(
            int managedThreadId,
            ApartmentState apartment)
        {
            ManagedThreadId = managedThreadId;
            Apartment = apartment;
        }

        public int ManagedThreadId { get; }

        public ApartmentState Apartment { get; }

        public static ThreadIdentity Current()
        {
            Thread thread = Thread.CurrentThread;
            return new ThreadIdentity(
                thread.ManagedThreadId,
                thread.GetApartmentState());
        }
    }
}
