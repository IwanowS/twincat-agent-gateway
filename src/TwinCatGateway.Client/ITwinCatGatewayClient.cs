using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Client;

public interface ITwinCatGatewayClient
{
    Task<GatewayStateSnapshot> GetGatewayStateAsync(
        CancellationToken cancellationToken = default);

    Task<GatewayShutdownResult> ShutdownAsync(
        CancellationToken cancellationToken = default);

    Task<OperationResult<XaeOpenResult>> OpenXaeAsync(
        XaeOpenParameters parameters,
        CancellationToken cancellationToken = default);

    Task<OperationResult<CloseXaeResult>> CloseXaeAsync(
        CloseXaeParameters parameters,
        CancellationToken cancellationToken = default);

    Task<OperationResult<SynchronizeResult>> SynchronizeXaeAsync(
        SynchronizeParameters parameters,
        CancellationToken cancellationToken = default);

    Task<OperationResult<XaeBuildResult>> BuildXaeAsync(
        XaeBuildParameters parameters,
        CancellationToken cancellationToken = default);

    Task<OperationResult<ActivationResult>> ActivateXaeAsync(
        ActivateParameters parameters,
        CancellationToken cancellationToken = default);

    Task<OperationResult<TargetConfigResult>> ConfigureTargetAsync(
        TargetConfigParameters parameters,
        CancellationToken cancellationToken = default);

    Task<OperationResult<TargetStartRestartResult>> StartRestartTargetAsync(
        TargetStartRestartParameters parameters,
        CancellationToken cancellationToken = default);

    Task<OperationSnapshot<TResult>> GetOperationAsync<TResult>(
        string operationId,
        CancellationToken cancellationToken = default);

    Task<OperationCancellationReceipt> CancelOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default);

    Task<ResourceContent> GetResourceAsync(
        string uri,
        int maximumCharacters = 64 * 1024,
        long offset = 0,
        CancellationToken cancellationToken = default);
}
