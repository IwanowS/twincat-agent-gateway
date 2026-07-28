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

    Task<GatewayResponse<OperationAccepted>> StartBuildAsync(
        BuildParameters parameters,
        CancellationToken cancellationToken = default);

    Task<GatewayResponse<OperationAccepted>>
        StartActivationAsync(
            ActivateParameters parameters,
            CancellationToken cancellationToken = default);

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
