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

            object? result = await DispatchCoreAsync(
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            return GatewayDispatchResult.Success(result);
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
                });
        }
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
            afterResponseWritten: callback);
    }

    private async Task<object?> DispatchCoreAsync(
        GatewayRequestContext request,
        CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case GatewayMethods.GatewayState:
                return _service.GetGatewayState();

            case GatewayMethods.XaeOpen:
                {
                    XaeOpenParameters parameters =
                        request.DeserializeParameters<XaeOpenParameters>(
                            _serializerOptions);
                    return await StartOperationAsync<XaeOpenResult>(
                            OperationKind.XaeOpen,
                            parameters.Profile,
                            () => _service.StartXaeOpen(parameters),
                            request.Wait,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

            case GatewayMethods.XaeBuild:
                {
                    XaeBuildParameters parameters =
                        request.DeserializeParameters<XaeBuildParameters>(
                            _serializerOptions);
                    return await StartOperationAsync<XaeBuildResult>(
                            OperationKind.XaeBuild,
                            parameters.Profile,
                            () => _service.StartXaeBuild(parameters),
                            request.Wait,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

            case GatewayMethods.XaeSynchronize:
                {
                    SynchronizeParameters parameters =
                        request.DeserializeParameters<
                            SynchronizeParameters>(
                            _serializerOptions);
                    return await StartOperationAsync<SynchronizeResult>(
                            OperationKind.Synchronize,
                            parameters.Profile,
                            () => _service.StartSynchronization(
                                parameters,
                                agentRequest: true),
                            request.Wait,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

            case GatewayMethods.XaeClose:
                {
                    CloseXaeParameters parameters =
                        request.DeserializeParameters<
                            CloseXaeParameters>(
                            _serializerOptions);
                    return await StartOperationAsync<CloseXaeResult>(
                            OperationKind.CloseXae,
                            parameters.Profile,
                            () => _service.StartCloseXae(parameters),
                            request.Wait,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

            case GatewayMethods.XaeActivate:
                {
                    ActivateParameters parameters =
                        request.DeserializeParameters<
                            ActivateParameters>(
                            _serializerOptions);
                    return await StartOperationAsync<ActivationResult>(
                            OperationKind.Activate,
                            parameters.Profile,
                            () => _service.StartActivation(parameters),
                            request.Wait,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

            case GatewayMethods.TargetConfig:
                {
                    TargetConfigParameters parameters =
                        request.DeserializeParameters<
                            TargetConfigParameters>(
                            _serializerOptions);
                    return await StartOperationAsync<TargetConfigResult>(
                            OperationKind.TargetConfig,
                            parameters.Profile,
                            () => _service.StartTargetConfig(parameters),
                            request.Wait,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

            case GatewayMethods.TargetStartRestart:
                {
                    TargetStartRestartParameters parameters =
                        request.DeserializeParameters<
                            TargetStartRestartParameters>(
                            _serializerOptions);
                    return await StartOperationAsync<TargetStartRestartResult>(
                            OperationKind.TargetStartRestart,
                            parameters.Profile,
                            () => _service.StartTargetStartRestart(parameters),
                            request.Wait,
                            cancellationToken)
                        .ConfigureAwait(false);
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

    private async Task<object> StartOperationAsync<TResult>(
        OperationKind kind,
        string? profile,
        Func<OperationHandle> start,
        bool wait,
        CancellationToken cancellationToken)
    {
        OperationHandle handle;
        try
        {
            handle = start();
        }
        catch (GatewayOperationException exception)
        {
            handle = _service.EnqueuePreflightFailure(
                kind,
                profile,
                exception);
        }

        if (!wait)
        {
            return new OperationReceipt
            {
                OperationId = handle.OperationId,
                State = handle.State,
            };
        }

        return await _service.WaitForOperationAsync<TResult>(
                handle.OperationId,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
