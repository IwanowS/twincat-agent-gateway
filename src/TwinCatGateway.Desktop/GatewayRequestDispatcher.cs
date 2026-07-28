using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Ipc;

namespace TwinCatGateway.Desktop;

public sealed class GatewayRequestDispatcher
{
    private readonly bool _allowShutdown;
    private readonly Action? _shutdownRequested;
    private readonly GatewayApplicationService _service;
    private readonly JsonSerializerOptions _serializerOptions =
        GatewayJson.CreateSerializerOptions();

    public GatewayRequestDispatcher(
        GatewayApplicationService service,
        bool allowShutdown = false,
        Action? shutdownRequested = null)
    {
        _service = service
            ?? throw new ArgumentNullException(nameof(service));
        _allowShutdown = allowShutdown;
        _shutdownRequested = shutdownRequested;
    }

    public Task<GatewayDispatchResult> DispatchAsync(
        GatewayRequestContext request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (request.Method == GatewayMethods.Shutdown)
            {
                return Task.FromResult(DispatchShutdown());
            }

            object? result = Dispatch(request);
            return Task.FromResult(GatewayDispatchResult.Success(result));
        }
        catch (GatewayOperationException exception)
        {
            return Task.FromResult(
                GatewayDispatchResult.Failure(
                    new GatewayError
                    {
                        Code = exception.Code,
                        Message = exception.Message,
                        Retryable = exception.Retryable,
                        Stage = exception.Stage,
                        RawLogRef = exception.RawLogRef,
                    }));
        }
    }

    private GatewayDispatchResult DispatchShutdown()
    {
        if (!_allowShutdown)
        {
            throw new GatewayOperationException(
                ErrorCodes.GatewayShutdownDisabled,
                "Gateway shutdown is disabled by "
                + "agentProcessControl.allowShutdown.",
                stage: "gateway.shutdown.policy");
        }

        Action callback = _shutdownRequested
            ?? throw new GatewayOperationException(
                ErrorCodes.OperationFailed,
                "Gateway shutdown is not available in this host.",
                stage: "gateway.shutdown.host");
        return GatewayDispatchResult.Success(
            new GatewayShutdownResult
            {
                ShutdownRequested = true,
            },
            callback);
    }

    private object? Dispatch(GatewayRequestContext request)
    {
        switch (request.Method)
        {
            case GatewayMethods.Health:
                return _service.GetHealth();
            case GatewayMethods.Status:
                return _service.GetStatus();
            case GatewayMethods.GetDiagnostics:
                {
                    GetDiagnosticsParameters parameters =
                        request.DeserializeParameters<
                            GetDiagnosticsParameters>(
                            _serializerOptions);
                    return _service.GetDiagnostics(parameters);
                }

            case GatewayMethods.Build:
                {
                    BuildParameters parameters =
                        request.DeserializeParameters<BuildParameters>(
                            _serializerOptions);
                    return _service.StartBuild(parameters);
                }

            case GatewayMethods.Activate:
                {
                    ActivateParameters parameters =
                        request.DeserializeParameters<
                            ActivateParameters>(
                            _serializerOptions);
                    return _service.StartActivation(parameters);
                }

            case GatewayMethods.GetOperation:
                {
                    GetOperationParameters parameters =
                        request.DeserializeParameters<GetOperationParameters>(
                            _serializerOptions);
                    return _service.GetOperation(parameters.OperationId);
                }

            case GatewayMethods.GetTestResults:
                {
                    GetTestResultsParameters parameters =
                        request.DeserializeParameters<
                            GetTestResultsParameters>(
                            _serializerOptions);
                    return _service.GetTestResults(
                        parameters.OperationId);
                }

            case GatewayMethods.CancelOperation:
                {
                    CancelOperationParameters parameters =
                        request.DeserializeParameters<CancelOperationParameters>(
                            _serializerOptions);
                    return _service.CancelOperation(parameters.OperationId);
                }

            case GatewayMethods.GetResource:
                {
                    GetResourceParameters parameters =
                        request.DeserializeParameters<GetResourceParameters>(
                            _serializerOptions);
                    return _service.GetResource(
                        parameters.Uri,
                        parameters.MaximumCharacters,
                        parameters.Offset);
                }

            default:
                throw new GatewayOperationException(
                    ErrorCodes.MethodNotFound,
                    $"Gateway method '{request.Method}' is not available.");
        }
    }
}
