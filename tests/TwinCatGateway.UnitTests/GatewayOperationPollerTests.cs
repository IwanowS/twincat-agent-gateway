using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Client;
using TwinCatGateway.Contracts;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class GatewayOperationPollerTests
{
    [Fact]
    public async Task ReturnsTerminalOperation()
    {
        FakeClient client = new(
            OperationState.Queued,
            OperationState.Running,
            OperationState.Succeeded);
        TestClock clock = new();
        GatewayOperationPoller poller = CreatePoller(
            client,
            clock);

        OperationSnapshot<XaeBuildResult> response =
                await poller.WaitAsync<XaeBuildResult>(
                    "build-1",
                    TimeSpan.FromSeconds(3));

        Assert.Equal(
            OperationState.Succeeded,
            response.Operation.State);
        Assert.Equal(3, client.ReadCount);
        Assert.Equal(
            TimeSpan.FromMilliseconds(200),
            clock.Elapsed);
    }

    [Fact]
    public async Task ReturnsGatewayFailureWithoutRetry()
    {
        FakeClient client = new(new InvalidOperationException("Unavailable"));
        TestClock clock = new();
        GatewayOperationPoller poller = CreatePoller(
            client,
            clock);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => poller.WaitAsync<XaeBuildResult>("build-1", TimeSpan.FromSeconds(3)));

        Assert.Equal("Unavailable", exception.Message);
        Assert.Equal(1, client.ReadCount);
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
    }

    [Fact]
    public async Task TimesOutWhileOperationRemainsRunning()
    {
        FakeClient client = new(OperationState.Running);
        TestClock clock = new();
        GatewayOperationPoller poller = CreatePoller(
            client,
            clock);

        TimeoutException exception =
            await Assert.ThrowsAsync<TimeoutException>(
                () => poller.WaitAsync<XaeBuildResult>(
                    "build-1",
                    TimeSpan.FromMilliseconds(250)));

        Assert.Contains("build-1", exception.Message);
        Assert.Equal(
            TimeSpan.FromMilliseconds(250),
            clock.Elapsed);
    }

    [Fact]
    public async Task HonorsCancellationBeforePolling()
    {
        FakeClient client = new(OperationState.Running);
        TestClock clock = new();
        GatewayOperationPoller poller = CreatePoller(
            client,
            clock);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => poller.WaitAsync<XaeBuildResult>(
                "build-1",
                TimeSpan.FromSeconds(1),
                cancellation.Token));

        Assert.Equal(0, client.ReadCount);
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
    }

    private static GatewayOperationPoller CreatePoller(
        ITwinCatGatewayClient client,
        TestClock clock)
    {
        return new GatewayOperationPoller(
            client,
            TimeSpan.FromMilliseconds(100),
            clock.UtcNow,
            clock.DelayAsync);
    }

    private sealed class TestClock
    {
        private readonly DateTimeOffset _start =
            new(2026, 7, 28, 0, 0, 0, TimeSpan.Zero);

        public TimeSpan Elapsed { get; private set; }

        public DateTimeOffset UtcNow()
        {
            return _start.Add(Elapsed);
        }

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Elapsed += delay;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClient : GatewayClientStub
    {
        private readonly Queue<OperationSnapshot<XaeBuildResult>> _responses;
        private OperationSnapshot<XaeBuildResult> _last;
        private readonly Exception? _failure;

        public FakeClient(params OperationState[] states)
            : this(CreateResponses(states))
        {
        }

        public FakeClient(Exception failure)
        {
            _failure = failure;
            _responses = new Queue<OperationSnapshot<XaeBuildResult>>();
            _last = CreateSnapshot(OperationState.Running);
        }

        private FakeClient(params OperationSnapshot<XaeBuildResult>[] responses)
        {
            _responses = new Queue<OperationSnapshot<XaeBuildResult>>(responses);
            _last = responses.Length > 0
                ? responses[responses.Length - 1]
                : throw new ArgumentException(
                    "At least one response is required.",
                    nameof(responses));
        }

        public int ReadCount { get; private set; }

        public override Task<OperationSnapshot<TResult>> GetOperationAsync<TResult>(
                string operationId,
                CancellationToken cancellationToken = default)
        {
            Assert.Equal("build-1", operationId);
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            if (_failure is not null)
            {
                return Task.FromException<OperationSnapshot<TResult>>(_failure);
            }
            if (_responses.Count > 0)
            {
                _last = _responses.Dequeue();
            }

            return Task.FromResult((OperationSnapshot<TResult>)(object)_last);
        }

        private static OperationSnapshot<XaeBuildResult>[] CreateResponses(
                IEnumerable<OperationState> states)
        {
            List<OperationSnapshot<XaeBuildResult>> responses = new();
            foreach (OperationState state in states)
            {
                responses.Add(CreateSnapshot(state));
            }

            return responses.ToArray();
        }

        private static OperationSnapshot<XaeBuildResult> CreateSnapshot(OperationState state) =>
            new()
            {
                Operation = new OperationRecord
                {
                    OperationId = "build-1",
                    Kind = OperationKind.XaeBuild,
                    State = state,
                },
            };
    }
}
