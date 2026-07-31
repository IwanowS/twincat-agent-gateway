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
    private readonly CapabilityEvaluator _capabilities;
    private readonly Action? _shutdownRequested;
    private readonly GatewayApplicationService _service;
    private readonly JsonSerializerOptions _serializerOptions =
        GatewayJson.CreateSerializerOptions();

    public GatewayRequestDispatcher(
        GatewayApplicationService service,
        CapabilityEvaluator capabilities,
        Action? shutdownRequested = null)
    {
        _service = service
            ?? throw new ArgumentNullException(nameof(service));
        _capabilities = capabilities
            ?? throw new ArgumentNullException(
                nameof(capabilities));
        _shutdownRequested = shutdownRequested;
    }

    public async Task<GatewayDispatchResult> DispatchAsync(
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
                return DispatchShutdown();
            }

            object? result =
                request.Method
                    == GatewayMethods.GetXaeMessages
                ? await DispatchXaeMessagesAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false)
                : Dispatch(request);
            return GatewayDispatchResult.Success(
                result,
                GetRuntimeAlert());
        }
        catch (GatewayOperationException exception)
        {
            return GatewayDispatchResult.Failure(
                new GatewayError
                {
                    Code = exception.Code,
                    Message = exception.Message,
                    Details = exception.Details,
                    Retryable = exception.Retryable,
                    Stage = exception.Stage,
                    RawLogRef = exception.RawLogRef,
                    Component = exception.Component,
                    SideEffectsStarted =
                        exception.SideEffectsStarted,
                    Expected = exception.Expected,
                    Observed = exception.Observed,
                },
                GetRuntimeAlert());
        }
    }

    private Task<XaeMessagesResult>
        DispatchXaeMessagesAsync(
            GatewayRequestContext request,
            CancellationToken cancellationToken)
    {
        GetXaeMessagesParameters parameters =
            request.DeserializeParameters<
                GetXaeMessagesParameters>(
                _serializerOptions);
        return _service.GetXaeMessagesAsync(
            parameters,
            cancellationToken);
    }

    private GatewayDispatchResult DispatchShutdown()
    {
        _capabilities.EnsureGatewayAllowed(
            CapabilityKey.GatewayShutdown,
            "gateway.shutdown.admission");

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
            GetRuntimeAlert(),
            callback);
    }

    private RuntimeAlert? GetRuntimeAlert()
    {
        return GatewayStatusSnapshotStore.CloneRuntimeAlert(
            _service.GetStatus().TwinCat.Alert);
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

            case GatewayMethods.XaeBuild:
                {
                    XaeBuildParameters parameters =
                        request.DeserializeParameters<XaeBuildParameters>(
                            _serializerOptions);
                    return _service.StartXaeBuild(parameters);
                }

            case GatewayMethods.Synchronize:
                {
                    SynchronizeParameters parameters =
                        request.DeserializeParameters<
                            SynchronizeParameters>(
                            _serializerOptions);
                    return _service.StartSynchronization(
                        parameters,
                        agentRequest: true);
                }

            case GatewayMethods.CloseXae:
                {
                    CloseXaeParameters parameters =
                        request.DeserializeParameters<
                            CloseXaeParameters>(
                            _serializerOptions);
                    return _service.StartCloseXae(parameters);
                }

            case GatewayMethods.Activate:
                {
                    ActivateParameters parameters =
                        request.DeserializeParameters<
                            ActivateParameters>(
                            _serializerOptions);
                    return _service.StartActivation(parameters);
                }

            case GatewayMethods.TargetConfig:
                {
                    TargetConfigParameters parameters =
                        request.DeserializeParameters<
                            TargetConfigParameters>(
                            _serializerOptions);
                    return _service.StartTargetConfig(parameters);
                }

            case GatewayMethods.TargetStartRestart:
                {
                    TargetStartRestartParameters parameters =
                        request.DeserializeParameters<
                            TargetStartRestartParameters>(
                            _serializerOptions);
                    return _service.StartTargetStartRestart(
                        parameters);
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
