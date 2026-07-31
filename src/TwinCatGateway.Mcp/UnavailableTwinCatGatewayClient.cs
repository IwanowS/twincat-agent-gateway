using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Client;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Ipc;

namespace TwinCatGateway.Mcp;

internal sealed class UnavailableTwinCatGatewayClient : ITwinCatGatewayClient
{
    private readonly GatewayError _error;

    private UnavailableTwinCatGatewayClient(GatewayError error)
    {
        _error = error;
    }

    public static ITwinCatGatewayClient Create(
        GatewayOperationException exception) =>
        Create(
            exception.Code,
            exception.Message,
            exception.Retryable,
            exception.Stage,
            exception.Details);

    public static ITwinCatGatewayClient Create(
        string code,
        string message,
        bool retryable,
        string? stage,
        string? details = null) =>
        new UnavailableTwinCatGatewayClient(
            new GatewayError
            {
                Code = code,
                Message = message,
                Details = details,
                Retryable = retryable,
                Stage = stage,
                Component = GatewayComponent.Gateway,
                SideEffectsStarted = false,
            });

    public Task<GatewayStateSnapshot> GetGatewayStateAsync(
        CancellationToken cancellationToken = default) =>
        Failure<GatewayStateSnapshot>(cancellationToken);

    public Task<GatewayShutdownResult> ShutdownAsync(
        CancellationToken cancellationToken = default) =>
        Failure<GatewayShutdownResult>(cancellationToken);

    public Task<OperationResult<XaeOpenResult>> OpenXaeAsync(
        XaeOpenParameters parameters,
        CancellationToken cancellationToken = default) =>
        Failure<OperationResult<XaeOpenResult>>(cancellationToken);

    public Task<OperationResult<CloseXaeResult>> CloseXaeAsync(
        CloseXaeParameters parameters,
        CancellationToken cancellationToken = default) =>
        Failure<OperationResult<CloseXaeResult>>(cancellationToken);

    public Task<OperationResult<SynchronizeResult>> SynchronizeXaeAsync(
        SynchronizeParameters parameters,
        CancellationToken cancellationToken = default) =>
        Failure<OperationResult<SynchronizeResult>>(cancellationToken);

    public Task<OperationResult<XaeBuildResult>> BuildXaeAsync(
        XaeBuildParameters parameters,
        CancellationToken cancellationToken = default) =>
        Failure<OperationResult<XaeBuildResult>>(cancellationToken);

    public Task<OperationResult<ActivationResult>> ActivateXaeAsync(
        ActivateParameters parameters,
        CancellationToken cancellationToken = default) =>
        Failure<OperationResult<ActivationResult>>(cancellationToken);

    public Task<OperationResult<TargetConfigResult>> ConfigureTargetAsync(
        TargetConfigParameters parameters,
        CancellationToken cancellationToken = default) =>
        Failure<OperationResult<TargetConfigResult>>(cancellationToken);

    public Task<OperationResult<TargetStartRestartResult>> StartRestartTargetAsync(
        TargetStartRestartParameters parameters,
        CancellationToken cancellationToken = default) =>
        Failure<OperationResult<TargetStartRestartResult>>(cancellationToken);

    public Task<OperationSnapshot<TResult>> GetOperationAsync<TResult>(
        string operationId,
        CancellationToken cancellationToken = default) =>
        Failure<OperationSnapshot<TResult>>(cancellationToken);

    public Task<OperationCancellationReceipt> CancelOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default) =>
        Failure<OperationCancellationReceipt>(cancellationToken);

    public Task<ResourceContent> GetResourceAsync(
        string uri,
        int maximumCharacters = 64 * 1024,
        long offset = 0,
        CancellationToken cancellationToken = default) =>
        Failure<ResourceContent>(cancellationToken);

    private Task<TResult> Failure<TResult>(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<TResult>(
            new GatewayClientException(
                GatewayClientFailureKind.Transport,
                new GatewayError
                {
                    Code = _error.Code,
                    Message = _error.Message,
                    Details = _error.Details,
                    Retryable = _error.Retryable,
                    Stage = _error.Stage,
                    Component = _error.Component,
                    SideEffectsStarted = false,
                }));
    }
}
