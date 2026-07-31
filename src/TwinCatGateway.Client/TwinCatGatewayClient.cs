using System;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Ipc;

namespace TwinCatGateway.Client;

public sealed class TwinCatGatewayClient : ITwinCatGatewayClient
{
    private readonly NamedPipeGatewayClient _client;

    public TwinCatGatewayClient(
        string pipeName = "TwinCatAgentGateway",
        TimeSpan? connectTimeout = null)
    {
        _client = new NamedPipeGatewayClient(pipeName, connectTimeout);
    }

    public Task<GatewayStateSnapshot> GetGatewayStateAsync(
        CancellationToken cancellationToken = default)
    {
        return _client.SendAsync<EmptyParameters, GatewayStateSnapshot>(
            GatewayMethods.GatewayState,
            new EmptyParameters(),
            wait: true,
            cancellationToken);
    }

    public Task<GatewayShutdownResult> ShutdownAsync(
        CancellationToken cancellationToken = default)
    {
        return _client.SendAsync<
            EmptyParameters,
            GatewayShutdownResult>(
            GatewayMethods.Shutdown,
            new EmptyParameters(),
            wait: true,
            cancellationToken);
    }

    public Task<OperationResult<XaeOpenResult>> OpenXaeAsync(
        XaeOpenParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return ExecuteOperationAsync<XaeOpenParameters, XaeOpenResult>(
            GatewayMethods.XaeOpen,
            parameters,
            cancellationToken);
    }

    public Task<OperationResult<XaeBuildResult>> BuildXaeAsync(
        XaeBuildParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return ExecuteOperationAsync<XaeBuildParameters, XaeBuildResult>(
            GatewayMethods.XaeBuild,
            parameters,
            cancellationToken);
    }

    public Task<OperationResult<SynchronizeResult>>
        SynchronizeXaeAsync(
            SynchronizeParameters parameters,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return ExecuteOperationAsync<SynchronizeParameters, SynchronizeResult>(
            GatewayMethods.XaeSynchronize,
            parameters,
            cancellationToken);
    }

    public Task<OperationResult<ActivationResult>>
        ActivateXaeAsync(
            ActivateParameters parameters,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return ExecuteOperationAsync<ActivateParameters, ActivationResult>(
            GatewayMethods.XaeActivate,
            parameters,
            cancellationToken);
    }

    public Task<OperationResult<CloseXaeResult>>
        CloseXaeAsync(
            CloseXaeParameters parameters,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return ExecuteOperationAsync<CloseXaeParameters, CloseXaeResult>(
            GatewayMethods.XaeClose,
            parameters,
            cancellationToken);
    }

    public Task<OperationResult<TargetConfigResult>>
        ConfigureTargetAsync(
            TargetConfigParameters parameters,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return ExecuteOperationAsync<TargetConfigParameters, TargetConfigResult>(
            GatewayMethods.TargetConfig,
            parameters,
            cancellationToken);
    }

    public Task<OperationResult<TargetStartRestartResult>>
        StartRestartTargetAsync(
            TargetStartRestartParameters parameters,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return ExecuteOperationAsync<TargetStartRestartParameters, TargetStartRestartResult>(
            GatewayMethods.TargetStartRestart,
            parameters,
            cancellationToken);
    }

    public Task<OperationSnapshot<TResult>> GetOperationAsync<TResult>(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        return _client.SendAsync<
            GetOperationParameters,
            OperationSnapshot<TResult>>(
            GatewayMethods.GetOperation,
            new GetOperationParameters
            {
                OperationId = operationId,
            },
            wait: true,
            cancellationToken);
    }

    public Task<OperationCancellationReceipt> CancelOperationAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        return _client.SendAsync<
            CancelOperationParameters,
            OperationCancellationReceipt>(
            GatewayMethods.CancelOperation,
            new CancelOperationParameters
            {
                OperationId = operationId,
            },
            wait: true,
            cancellationToken);
    }

    public Task<ResourceContent> GetResourceAsync(
        string uri,
        int maximumCharacters = 64 * 1024,
        long offset = 0,
        CancellationToken cancellationToken = default)
    {
        return _client.SendAsync<GetResourceParameters, ResourceContent>(
            GatewayMethods.GetResource,
            new GetResourceParameters
            {
                Uri = uri,
                MaximumCharacters = maximumCharacters,
                Offset = offset,
            },
            wait: true,
            cancellationToken);
    }

    private async Task<OperationResult<TResult>> ExecuteOperationAsync<TParameters, TResult>(
        string method,
        TParameters parameters,
        CancellationToken cancellationToken)
    {
        OperationReceipt handle = await _client.SendAsync<TParameters, OperationReceipt>(
                method,
                parameters,
                wait: false,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            OperationSnapshot<TResult> snapshot =
                await new GatewayOperationPoller(this).WaitAsync<TResult>(
                        handle.OperationId,
                        TimeSpan.FromHours(24),
                        cancellationToken)
                    .ConfigureAwait(false);
            return CreateOperationResult(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            using CancellationTokenSource cancellationDeadline =
                new(TimeSpan.FromSeconds(5));
            await CancelOperationAsync(
                    handle.OperationId,
                    cancellationDeadline.Token)
                .ConfigureAwait(false);
            throw;
        }
    }

    private static OperationResult<TResult> CreateOperationResult<TResult>(
        OperationSnapshot<TResult> snapshot)
    {
        OperationRecord operation = snapshot.Operation;
        return new OperationResult<TResult>
        {
            Ok = operation.State == OperationState.Succeeded,
            OperationId = operation.OperationId,
            Component = operation.Error?.Component ?? GetComponent(operation.Kind),
            Stage = operation.Error?.Stage ?? GetStage(operation.Kind),
            Completion = operation.State switch
            {
                OperationState.Succeeded => OperationCompletion.Succeeded,
                OperationState.TimedOut => OperationCompletion.TimedOut,
                OperationState.Cancelled => OperationCompletion.Cancelled,
                _ => OperationCompletion.Failed,
            },
            SideEffectsStarted = operation.Error?.SideEffectsStarted ?? false,
            Result = snapshot.Result,
            Error = operation.Error,
            Diagnostics = operation.Diagnostics,
            Resources = operation.Resources,
        };
    }

    private static GatewayComponent GetComponent(OperationKind kind) =>
        kind is OperationKind.TargetConfig or OperationKind.TargetStartRestart
            ? GatewayComponent.Target
            : GatewayComponent.Xae;

    private static string GetStage(OperationKind kind) => kind switch
    {
        OperationKind.XaeOpen => "xae.open",
        OperationKind.XaeBuild => "xae.build",
        OperationKind.Activate => "xae.activate",
        OperationKind.Synchronize => "xae.synchronize",
        OperationKind.CloseXae => "xae.close",
        OperationKind.TargetConfig => "target.config",
        OperationKind.TargetStartRestart => "target.startRestart",
        _ => "operation",
    };
}
