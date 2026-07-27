using System;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Ipc;

namespace TwinCatGateway.Client;

public sealed class TwinCatGatewayClient
{
    private readonly NamedPipeGatewayClient _client;

    public TwinCatGatewayClient(
        string pipeName = "TwinCatAgentGateway",
        TimeSpan? connectTimeout = null)
    {
        _client = new NamedPipeGatewayClient(pipeName, connectTimeout);
    }

    public Task<GatewayResponse<HealthResult>> GetHealthAsync(
        CancellationToken cancellationToken = default)
    {
        return _client.SendAsync<EmptyParameters, HealthResult>(
            GatewayMethods.Health,
            new EmptyParameters(),
            wait: true,
            cancellationToken);
    }

    public Task<GatewayResponse<GatewayStatusResult>> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        return _client.SendAsync<EmptyParameters, GatewayStatusResult>(
            GatewayMethods.Status,
            new EmptyParameters(),
            wait: true,
            cancellationToken);
    }
}
