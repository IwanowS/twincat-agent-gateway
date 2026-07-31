using System;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Ipc;

namespace TwinCatGateway.Client;

public sealed class TwinCatGatewayClient : ITwinCatGatewayClient
{
    private readonly NamedPipeGatewayClient _client;

    public TwinCatGatewayClient(
        string pipeName = "TwinCatAgentGateway",
        TimeSpan? connectTimeout = null)
    {
        _client = new NamedPipeGatewayClient(pipeName, connectTimeout);
    }

    public Task<GatewayStateSnapshot> GetGatewayStateAsync(
        CancellationToken cancellationToken = default)
    {
        return _client.SendAsync<EmptyParameters, GatewayStateSnapshot>(
            GatewayMethods.GatewayState,
            new EmptyParameters(),
            wait: true,
            cancellationToken);
    }

    public Task<GatewayShutdownResult> ShutdownAsync(
        CancellationToken cancellationToken = default)
    {
        return _client.SendAsync<
            EmptyParameters,
            GatewayShutdownResult>(
            GatewayMethods.Shutdown,
            new EmptyParameters(),
            wait: true,
            cancellationToken);
    }

    public Task<OperationResult<XaeOpenResult>> OpenXaeAsync(
        XaeOpenParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _client.SendAsync<
            XaeOpenParameters,
            OperationResult<XaeOpenResult>>(
            GatewayMethods.XaeOpen,
            parameters,
            wait: true,
            cancellationToken);
    }

    public Task<OperationResult<XaeBuildResult>> BuildXaeAsync(
        XaeBuildParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return _client.SendAsync<
            XaeBuildParameters,
            OperationResult<XaeBuildResult>>(
            GatewayMethods.XaeBuild,
            parameters,
            wait: true,
            cancellationToken);
    }

    public Task<OperationResult<SynchronizeResult>>
        SynchronizeXaeAsync(
            SynchronizeParameters parameters,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return _client.SendAsync<
            SynchronizeParameters,
            OperationResult<SynchronizeResult>>(
            GatewayMethods.XaeSynchronize,
            parameters,
            wait: true,
            cancellationToken);
    }

    public Task<OperationResult<ActivationResult>>
        ActivateXaeAsync(
            ActivateParameters parameters,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return _client.SendAsync<
            ActivateParameters,
            OperationResult<ActivationResult>>(
            GatewayMethods.XaeActivate,
            parameters,
            wait: true,
            cancellationToken);
    }

    public Task<OperationResult<CloseXaeResult>>
        CloseXaeAsync(
            CloseXaeParameters parameters,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return _client.SendAsync<
            CloseXaeParameters,
            OperationResult<CloseXaeResult>>(
            GatewayMethods.XaeClose,
            parameters,
            wait: true,
            cancellationToken);
    }

    public Task<OperationResult<TargetConfigResult>>
        ConfigureTargetAsync(
            TargetConfigParameters parameters,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return _client.SendAsync<
            TargetConfigParameters,
            OperationResult<TargetConfigResult>>(
            GatewayMethods.TargetConfig,
            parameters,
            wait: true,
            cancellationToken);
    }

    public Task<OperationResult<TargetStartRestartResult>>
        StartRestartTargetAsync(
            TargetStartRestartParameters parameters,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return _client.SendAsync<
            TargetStartRestartParameters,
            OperationResult<TargetStartRestartResult>>(
            GatewayMethods.TargetStartRestart,
            parameters,
            wait: true,
            cancellationToken);
    }

    public Task<OperationSnapshot<TResult>> GetOperationAsync<TResult>(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        return _client.SendAsync<
            GetOperationParameters,
            OperationSnapshot<TResult>>(
            GatewayMethods.GetOperation,
            new GetOperationParameters
            {
                OperationId = operationId,
            },
            wait: true,
            cancellationToken);
    }

    public Task<OperationCancellationReceipt> CancelOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        return _client.SendAsync<
            CancelOperationParameters,
            OperationCancellationReceipt>(
            GatewayMethods.CancelOperation,
            new CancelOperationParameters
            {
                OperationId = operationId,
            },
            wait: true,
            cancellationToken);
    }

    public Task<ResourceContent> GetResourceAsync(
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
