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

        GatewayResponse<OperationDetails<BuildResult>>
            response =
                await poller.WaitAsync<BuildResult>(
                    "build-1",
                    TimeSpan.FromSeconds(3));

        Assert.True(response.Ok);
        Assert.Equal(
            OperationState.Succeeded,
            response.Result?.Operation.State);
        Assert.Equal(3, client.ReadCount);
        Assert.Equal(
            TimeSpan.FromMilliseconds(200),
            clock.Elapsed);
    }

    [Fact]
    public async Task ReturnsGatewayFailureWithoutRetry()
    {
        FakeClient client = new(
            new GatewayResponse<
                OperationDetails<BuildResult>>
            {
                Ok = false,
                Error = new GatewayError
                {
                    Code = "IPC_FAILED",
                    Message = "Unavailable",
                },
            });
        TestClock clock = new();
        GatewayOperationPoller poller = CreatePoller(
            client,
            clock);

        GatewayResponse<OperationDetails<BuildResult>>
            response =
                await poller.WaitAsync<BuildResult>(
                    "build-1",
                    TimeSpan.FromSeconds(3));

        Assert.False(response.Ok);
        Assert.Equal("IPC_FAILED", response.Error?.Code);
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
                () => poller.WaitAsync<BuildResult>(
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
            () => poller.WaitAsync<BuildResult>(
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

    private sealed class FakeClient : ITwinCatGatewayClient
    {
        private readonly Queue<
            GatewayResponse<
                OperationDetails<BuildResult>>> _responses;
        private GatewayResponse<
            OperationDetails<BuildResult>> _last;

        public FakeClient(params OperationState[] states)
            : this(CreateResponses(states))
        {
        }

        public FakeClient(
            params GatewayResponse<
                OperationDetails<BuildResult>>[] responses)
        {
            _responses = new Queue<
                GatewayResponse<
                    OperationDetails<BuildResult>>>(responses);
            _last = responses.Length > 0
                ? responses[responses.Length - 1]
                : throw new ArgumentException(
                    "At least one response is required.",
                    nameof(responses));
        }

        public int ReadCount { get; private set; }

        public Task<
            GatewayResponse<OperationDetails<TResult>>>
            GetOperationAsync<TResult>(
                string operationId,
                CancellationToken cancellationToken = default)
        {
            Assert.Equal("build-1", operationId);
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            if (_responses.Count > 0)
            {
                _last = _responses.Dequeue();
            }

            return Task.FromResult(
                (GatewayResponse<OperationDetails<TResult>>)
                (object)_last);
        }

        public Task<GatewayResponse<HealthResult>>
            GetHealthAsync(
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<GatewayStatusResult>>
            GetStatusAsync(
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<GatewayDiagnosticsResult>>
            GetDiagnosticsAsync(
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<GatewayDiagnosticsResult>>
            GetDiagnosticsAsync(
                GetDiagnosticsParameters parameters,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<OperationAccepted>>
            StartBuildAsync(
                BuildParameters parameters,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<OperationAccepted>>
            StartActivationAsync(
                ActivateParameters parameters,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<
            GatewayResponse<OperationDetails<TestResult>>>
            GetTestResultsAsync(
                string operationId,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<CancelOperationResult>>
            CancelOperationAsync(
                string operationId,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<ResourceContent>>
            GetResourceAsync(
                string uri,
                int maximumCharacters = 64 * 1024,
                long offset = 0,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        private static GatewayResponse<
            OperationDetails<BuildResult>>[] CreateResponses(
                IEnumerable<OperationState> states)
        {
            List<GatewayResponse<
                OperationDetails<BuildResult>>> responses = new();
            foreach (OperationState state in states)
            {
                responses.Add(
                    new GatewayResponse<
                        OperationDetails<BuildResult>>
                    {
                        Ok = true,
                        Result =
                            new OperationDetails<BuildResult>
                            {
                                Operation =
                                    new OperationSummary
                                    {
                                        OperationId = "build-1",
                                        State = state,
                                    },
                            },
                    });
            }

            return responses.ToArray();
        }
    }
}
