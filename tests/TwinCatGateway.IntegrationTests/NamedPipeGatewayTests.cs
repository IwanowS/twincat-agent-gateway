using System;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Ipc;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class NamedPipeGatewayTests
{
    [Fact]
    public async Task CurrentUserCanReadHealthFromLocalPipe()
    {
        string pipeName = "TwinCatGatewayTests-" + Guid.NewGuid().ToString("N");
        GatewayProtocolHandler handler = new(
            (request, cancellationToken) =>
                Task.FromResult(
                    GatewayDispatchResult.Success(
                        new HealthResult
                        {
                            Version = "0.1.0",
                            State = GatewayState.Ready,
                            Ready = true,
                        })));
        using NamedPipeGatewayServer server = new(pipeName, handler);
        using CancellationTokenSource shutdown = new();
        Task serverTask = server.RunAsync(shutdown.Token);
        NamedPipeGatewayClient client = new(
            pipeName,
            connectTimeout: TimeSpan.FromSeconds(5));

        GatewayResponse<HealthResult> response =
            await client.SendAsync<EmptyParameters, HealthResult>(
                GatewayMethods.Health,
                new EmptyParameters(),
                wait: true,
                CancellationToken.None);

        shutdown.Cancel();
        await WithTimeoutAsync(serverTask, TimeSpan.FromSeconds(5));

        Assert.True(response.Ok);
        Assert.True(response.Result?.Ready);
        Assert.Equal(GatewayState.Ready, response.Result?.State);
    }

    [Fact]
    public async Task SlowRequestDoesNotBlockIndependentStatusConnection()
    {
        string pipeName = "TwinCatGatewayTests-" + Guid.NewGuid().ToString("N");
        TaskCompletionSource<bool> slowStarted = NewCompletionSource();
        TaskCompletionSource<bool> releaseSlow = NewCompletionSource();
        GatewayProtocolHandler handler = new(
            async (request, cancellationToken) =>
            {
                if (string.Equals(request.Method, "slow", StringComparison.Ordinal))
                {
                    slowStarted.SetResult(true);
                    await WaitAsync(releaseSlow.Task, cancellationToken);
                }

                return GatewayDispatchResult.Success(
                    new HealthResult
                    {
                        Version = "0.1.0",
                        State = GatewayState.Ready,
                        Ready = true,
                    });
            });
        using NamedPipeGatewayServer server = new(pipeName, handler);
        using CancellationTokenSource shutdown = new();
        Task serverTask = server.RunAsync(shutdown.Token);
        NamedPipeGatewayClient slowClient = new(pipeName);
        NamedPipeGatewayClient statusClient = new(pipeName);

        Task<GatewayResponse<HealthResult>> slow =
            slowClient.SendAsync<EmptyParameters, HealthResult>(
                "slow",
                new EmptyParameters(),
                wait: true,
                CancellationToken.None);
        await slowStarted.Task;

        Task<GatewayResponse<HealthResult>> status =
            statusClient.SendAsync<EmptyParameters, HealthResult>(
                GatewayMethods.Health,
                new EmptyParameters(),
                wait: true,
                CancellationToken.None);
        GatewayResponse<HealthResult> statusResponse =
            await WithTimeoutAsync(status, TimeSpan.FromSeconds(2));

        releaseSlow.SetResult(true);
        await slow;
        shutdown.Cancel();
        await WithTimeoutAsync(serverTask, TimeSpan.FromSeconds(5));

        Assert.True(statusResponse.Ok);
        Assert.True(statusResponse.Result?.Ready);
    }

    private static async Task WaitAsync(
        Task task,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> cancelled = NewCompletionSource();
        using CancellationTokenRegistration registration =
            cancellationToken.Register(() => cancelled.TrySetCanceled());
        Task completed = await Task.WhenAny(task, cancelled.Task);
        await completed;
    }

    private static async Task WithTimeoutAsync(Task task, TimeSpan timeout)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (!ReferenceEquals(task, completed))
        {
            throw new TimeoutException("The operation did not complete in time.");
        }

        await task;
    }

    private static async Task<T> WithTimeoutAsync<T>(
        Task<T> task,
        TimeSpan timeout)
    {
        await WithTimeoutAsync((Task)task, timeout);
        return await task;
    }

    private static TaskCompletionSource<bool> NewCompletionSource()
    {
        return new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
