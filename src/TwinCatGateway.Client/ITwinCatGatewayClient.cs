using System;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Client;

public interface ITwinCatGatewayClient
{
    Task<GatewayResponse<HealthResult>> GetHealthAsync(
        CancellationToken cancellationToken = default);

    Task<GatewayResponse<GatewayStatusResult>> GetStatusAsync(
        CancellationToken cancellationToken = default);

    Task<GatewayResponse<GatewayShutdownResult>> ShutdownAsync(
        CancellationToken cancellationToken = default);

    Task<GatewayResponse<GatewayDiagnosticsResult>>
        GetDiagnosticsAsync(
            CancellationToken cancellationToken = default);

    Task<GatewayResponse<GatewayDiagnosticsResult>>
        GetDiagnosticsAsync(
            GetDiagnosticsParameters parameters,
            CancellationToken cancellationToken = default);

    Task<GatewayResponse<XaeMessagesResult>>
        GetXaeMessagesAsync(
            GetXaeMessagesParameters parameters,
            CancellationToken cancellationToken = default)
    {
        return Task.FromException<
            GatewayResponse<XaeMessagesResult>>(
            new NotSupportedException(
                "XAE Error List reading is not implemented by this client."));
    }

    Task<GatewayResponse<OperationAccepted>> StartBuildAsync(
        BuildParameters parameters,
        CancellationToken cancellationToken = default);

    Task<GatewayResponse<OperationAccepted>>
        StartSynchronizationAsync(
            SynchronizeParameters parameters,
            CancellationToken cancellationToken = default)
    {
        return Task.FromException<
            GatewayResponse<OperationAccepted>>(
            new NotSupportedException(
                "Synchronization is not implemented by this client."));
    }

    Task<GatewayResponse<OperationAccepted>>
        StartActivationAsync(
            ActivateParameters parameters,
            CancellationToken cancellationToken = default);

    Task<GatewayResponse<OperationAccepted>>
        StartRecoverToConfigAsync(
            RecoverToConfigParameters parameters,
            CancellationToken cancellationToken = default)
    {
        return Task.FromException<
            GatewayResponse<OperationAccepted>>(
            new NotSupportedException(
                "Runtime recovery is not implemented by this client."));
    }

    Task<GatewayResponse<OperationDetails<TResult>>>
        GetOperationAsync<TResult>(
            string operationId,
            CancellationToken cancellationToken = default);

    Task<GatewayResponse<OperationDetails<TestResult>>>
        GetTestResultsAsync(
            string operationId,
            CancellationToken cancellationToken = default);

    Task<GatewayResponse<CancelOperationResult>>
        CancelOperationAsync(
            string operationId,
            CancellationToken cancellationToken = default);

    Task<GatewayResponse<ResourceContent>> GetResourceAsync(
        string uri,
        int maximumCharacters = 64 * 1024,
        long offset = 0,
        CancellationToken cancellationToken = default);
}
