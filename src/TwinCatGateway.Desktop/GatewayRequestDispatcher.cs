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
    private readonly GatewayApplicationService _service;
    private readonly JsonSerializerOptions _serializerOptions =
        GatewayJson.CreateSerializerOptions();

    public GatewayRequestDispatcher(GatewayApplicationService service)
    {
        _service = service
            ?? throw new ArgumentNullException(nameof(service));
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

    private object? Dispatch(GatewayRequestContext request)
    {
        switch (request.Method)
        {
            case GatewayMethods.Health:
                return _service.GetHealth();
            case GatewayMethods.Status:
                return _service.GetStatus();
            case GatewayMethods.GetDiagnostics:
                return _service.GetDiagnostics();
            case GatewayMethods.Build:
                {
                    BuildParameters parameters =
                        request.DeserializeParameters<BuildParameters>(
                            _serializerOptions);
                    return _service.StartBuild(parameters);
                }

            case GatewayMethods.GetOperation:
                {
                    GetOperationParameters parameters =
                        request.DeserializeParameters<GetOperationParameters>(
                            _serializerOptions);
                    return _service.GetOperation(parameters.OperationId);
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
