using System;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Client;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;

namespace TwinCatGateway.Mcp;

internal sealed class UnavailableTwinCatGatewayClient
    : ITwinCatGatewayClient
{
    private readonly GatewayError _error;

    private UnavailableTwinCatGatewayClient(
        GatewayError error)
    {
        _error = error;
    }

    public static ITwinCatGatewayClient Create(
        GatewayOperationException exception)
    {
        return Create(
            exception.Code,
            exception.Message,
            exception.Retryable,
            exception.Stage,
            exception.Details);
    }

    public static ITwinCatGatewayClient Create(
        string code,
        string message,
        bool retryable,
        string? stage,
        string? details = null)
    {
        return new UnavailableTwinCatGatewayClient(
            new GatewayError
            {
                Code = code,
                Message = message,
                Details = details,
                Retryable = retryable,
                Stage = stage,
            });
    }

    public Task<GatewayResponse<GatewayStatusResult>>
        GetStatusAsync(
            CancellationToken cancellationToken = default)
    {
        return Response<GatewayStatusResult>(
            cancellationToken);
    }

    public Task<GatewayResponse<HealthResult>>
        GetHealthAsync(
            CancellationToken cancellationToken = default)
    {
        return Response<HealthResult>(
            cancellationToken);
    }

    public Task<GatewayResponse<GatewayShutdownResult>>
        ShutdownAsync(
            CancellationToken cancellationToken = default)
    {
        return Response<GatewayShutdownResult>(
            cancellationToken);
    }

    public Task<GatewayResponse<GatewayDiagnosticsResult>>
        GetDiagnosticsAsync(
            CancellationToken cancellationToken = default)
    {
        return Response<GatewayDiagnosticsResult>(
            cancellationToken);
    }

    public Task<GatewayResponse<XaeMessagesResult>>
        GetXaeMessagesAsync(
            GetXaeMessagesParameters parameters,
            CancellationToken cancellationToken = default)
    {
        return Response<XaeMessagesResult>(
            cancellationToken);
    }

    public Task<GatewayResponse<GatewayDiagnosticsResult>>
        GetDiagnosticsAsync(
            GetDiagnosticsParameters parameters,
            CancellationToken cancellationToken = default)
    {
        return Response<GatewayDiagnosticsResult>(
            cancellationToken);
    }

    public Task<GatewayResponse<OperationAccepted>>
        StartXaeBuildAsync(
            XaeBuildParameters parameters,
            CancellationToken cancellationToken = default)
    {
        return Response<OperationAccepted>(
            cancellationToken);
    }

    public Task<GatewayResponse<OperationAccepted>>
        StartActivationAsync(
            ActivateParameters parameters,
            CancellationToken cancellationToken = default)
    {
        return Response<OperationAccepted>(
            cancellationToken);
    }

    public Task<GatewayResponse<OperationAccepted>>
        StartRecoverToConfigAsync(
            RecoverToConfigParameters parameters,
            CancellationToken cancellationToken = default)
    {
        return Response<OperationAccepted>(
            cancellationToken);
    }

    public Task<GatewayResponse<OperationAccepted>>
        StartSynchronizationAsync(
            SynchronizeParameters parameters,
            CancellationToken cancellationToken = default)
    {
        return Response<OperationAccepted>(
            cancellationToken);
    }

    public Task<GatewayResponse<OperationAccepted>>
        StartCloseXaeAsync(
            CloseXaeParameters parameters,
            CancellationToken cancellationToken = default)
    {
        return Response<OperationAccepted>(
            cancellationToken);
    }

    public Task<GatewayResponse<OperationDetails<TResult>>>
        GetOperationAsync<TResult>(
            string operationId,
            CancellationToken cancellationToken = default)
    {
        return Response<OperationDetails<TResult>>(
            cancellationToken);
    }

    public Task<GatewayResponse<OperationDetails<TestResult>>>
        GetTestResultsAsync(
            string operationId,
            CancellationToken cancellationToken = default)
    {
        return Response<OperationDetails<TestResult>>(
            cancellationToken);
    }

    public Task<GatewayResponse<CancelOperationResult>>
        CancelOperationAsync(
            string operationId,
            CancellationToken cancellationToken = default)
    {
        return Response<CancelOperationResult>(
            cancellationToken);
    }

    public Task<GatewayResponse<ResourceContent>>
        GetResourceAsync(
            string uri,
            int maximumCharacters = 64 * 1024,
            long offset = 0,
            CancellationToken cancellationToken = default)
    {
        return Response<ResourceContent>(
            cancellationToken);
    }

    private Task<GatewayResponse<T>> Response<T>(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            new GatewayResponse<T>
            {
                Ok = false,
                Error = new GatewayError
                {
                    Code = _error.Code,
                    Message = _error.Message,
                    Details = _error.Details,
                    Retryable = _error.Retryable,
                    Stage = _error.Stage,
                },
            });
    }
}
