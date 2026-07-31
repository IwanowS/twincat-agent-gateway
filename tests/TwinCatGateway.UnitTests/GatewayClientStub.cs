using System;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Client;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.UnitTests;

internal abstract class GatewayClientStub : ITwinCatGatewayClient
{
    public virtual Task<GatewayStateSnapshot> GetGatewayStateAsync(CancellationToken cancellationToken = default) => Unsupported<GatewayStateSnapshot>();
    public virtual Task<GatewayShutdownResult> ShutdownAsync(CancellationToken cancellationToken = default) => Unsupported<GatewayShutdownResult>();
    public virtual Task<OperationResult<XaeOpenResult>> OpenXaeAsync(XaeOpenParameters parameters, CancellationToken cancellationToken = default) => Unsupported<OperationResult<XaeOpenResult>>();
    public virtual Task<OperationResult<CloseXaeResult>> CloseXaeAsync(CloseXaeParameters parameters, CancellationToken cancellationToken = default) => Unsupported<OperationResult<CloseXaeResult>>();
    public virtual Task<OperationResult<SynchronizeResult>> SynchronizeXaeAsync(SynchronizeParameters parameters, CancellationToken cancellationToken = default) => Unsupported<OperationResult<SynchronizeResult>>();
    public virtual Task<OperationResult<XaeBuildResult>> BuildXaeAsync(XaeBuildParameters parameters, CancellationToken cancellationToken = default) => Unsupported<OperationResult<XaeBuildResult>>();
    public virtual Task<OperationResult<ActivationResult>> ActivateXaeAsync(ActivateParameters parameters, CancellationToken cancellationToken = default) => Unsupported<OperationResult<ActivationResult>>();
    public virtual Task<OperationResult<TargetConfigResult>> ConfigureTargetAsync(TargetConfigParameters parameters, CancellationToken cancellationToken = default) => Unsupported<OperationResult<TargetConfigResult>>();
    public virtual Task<OperationResult<TargetStartRestartResult>> StartRestartTargetAsync(TargetStartRestartParameters parameters, CancellationToken cancellationToken = default) => Unsupported<OperationResult<TargetStartRestartResult>>();
    public virtual Task<OperationSnapshot<TResult>> GetOperationAsync<TResult>(string operationId, CancellationToken cancellationToken = default) => Unsupported<OperationSnapshot<TResult>>();
    public virtual Task<OperationCancellationReceipt> CancelOperationAsync(string operationId, CancellationToken cancellationToken = default) => Unsupported<OperationCancellationReceipt>();
    public virtual Task<ResourceContent> GetResourceAsync(string uri, int maximumCharacters = 64 * 1024, long offset = 0, CancellationToken cancellationToken = default) => Unsupported<ResourceContent>();

    private static Task<T> Unsupported<T>() =>
        Task.FromException<T>(new NotSupportedException("This client operation is not configured for the test."));
}
