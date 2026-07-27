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

    public Task<GatewayResponse<GatewayDiagnosticsResult>> GetDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        return _client.SendAsync<EmptyParameters, GatewayDiagnosticsResult>(
            GatewayMethods.GetDiagnostics,
            new EmptyParameters(),
            wait: true,
            cancellationToken);
    }

    public Task<GatewayResponse<OperationAccepted>> StartBuildAsync(
        BuildParameters parameters,
        CancellationToken cancellationToken = default)
    {
        return _client.SendAsync<
            BuildParameters,
            OperationAccepted>(
            GatewayMethods.Build,
            parameters,
            wait: false,
            cancellationToken);
    }

    public Task<GatewayResponse<OperationDetails<TResult>>> GetOperationAsync<TResult>(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        return _client.SendAsync<
            GetOperationParameters,
            OperationDetails<TResult>>(
            GatewayMethods.GetOperation,
            new GetOperationParameters
            {
                OperationId = operationId,
            },
            wait: true,
            cancellationToken);
    }

    public Task<GatewayResponse<CancelOperationResult>> CancelOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        return _client.SendAsync<
            CancelOperationParameters,
            CancelOperationResult>(
            GatewayMethods.CancelOperation,
            new CancelOperationParameters
            {
                OperationId = operationId,
            },
            wait: true,
            cancellationToken);
    }

    public Task<GatewayResponse<ResourceContent>> GetResourceAsync(
        string uri,
        int maximumCharacters = 64 * 1024,
        long offset = 0,
        CancellationToken cancellationToken = default)
    {
        return _client.SendAsync<GetResourceParameters, ResourceContent>(
            GatewayMethods.GetResource,
            new GetResourceParameters
            {
                Uri = uri,
                MaximumCharacters = maximumCharacters,
                Offset = offset,
            },
            wait: true,
            cancellationToken);
    }
}
