using System;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Client;

public sealed class GatewayOperationPoller
{
    private static readonly TimeSpan DefaultPollInterval =
        TimeSpan.FromMilliseconds(100);

    private readonly ITwinCatGatewayClient _client;
    private readonly TimeSpan _pollInterval;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<
        TimeSpan,
        CancellationToken,
        Task> _delay;

    public GatewayOperationPoller(
        ITwinCatGatewayClient client,
        TimeSpan? pollInterval = null)
        : this(
            client,
            pollInterval ?? DefaultPollInterval,
            () => DateTimeOffset.UtcNow,
            Task.Delay)
    {
    }

    internal GatewayOperationPoller(
        ITwinCatGatewayClient client,
        TimeSpan pollInterval,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _client = client
            ?? throw new ArgumentNullException(nameof(client));
        ArgumentOutOfRangeException
            .ThrowIfLessThanOrEqual(
                pollInterval,
                TimeSpan.Zero);

        _pollInterval = pollInterval;
        _utcNow = utcNow
            ?? throw new ArgumentNullException(nameof(utcNow));
        _delay = delay
            ?? throw new ArgumentNullException(nameof(delay));
    }

    public async Task<OperationSnapshot<TResult>> WaitAsync<TResult>(
            string operationId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new ArgumentException(
                "Operation ID is required.",
                nameof(operationId));
        }

        ArgumentOutOfRangeException
            .ThrowIfLessThanOrEqual(
                timeout,
                TimeSpan.Zero);

        DateTimeOffset deadline = _utcNow().Add(timeout);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OperationSnapshot<TResult> snapshot =
                await _client.GetOperationAsync<TResult>(
                    operationId,
                    cancellationToken).ConfigureAwait(false);
            switch (snapshot.Operation.State)
            {
                case OperationState.Queued:
                case OperationState.Running:
                    break;
                default:
                    return snapshot;
            }

            TimeSpan remaining = deadline - _utcNow();
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"Operation '{operationId}' did not complete "
                    + $"within {timeout.TotalSeconds:0.###} seconds.");
            }

            await _delay(
                    remaining < _pollInterval
                        ? remaining
                        : _pollInterval,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
