using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TwinCatGateway.Client;
using TwinCatGateway.Contracts;
using TwinCatGateway.Ipc;

namespace TwinCatGateway.Mcp;

[McpServerToolType]
public sealed class TwinCatTools
{
    private const int DefaultOperationTimeoutSeconds = 120;
    private readonly GatewayMcpRuntime? _runtime;
    private readonly ITwinCatGatewayClient? _fixedClient;

    [ActivatorUtilitiesConstructor]
    public TwinCatTools(GatewayMcpRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public TwinCatTools(ITwinCatGatewayClient client)
    {
        _fixedClient = client ?? throw new ArgumentNullException(nameof(client));
    }

    [McpServerTool(
        Name = "gateway_start",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(GatewayLifecycleResult<GatewayStartResult>))]
    [Description("Start or reuse the configured TwinCAT Agent Gateway desktop process.")]
    public async Task<CallToolResult> StartGatewayAsync(
        [Description("Optional explicit gateway configuration path.")]
        string? config = null,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        if (_runtime is null)
        {
            return LifecycleFailure<GatewayStartResult>(
                ErrorCodes.GatewayStartFailed,
                "Gateway lifecycle runtime is not configured.",
                "gateway.start.runtime");
        }

        GatewayLifecycleResult<GatewayStartResult> result =
            await _runtime.StartAsync(
                    server,
                    NullIfWhiteSpace(config),
                    TimeSpan.FromSeconds(30),
                    cancellationToken)
                .ConfigureAwait(false);
        return CreateResult(result, !result.Ok);
    }

    [McpServerTool(
        Name = "gateway_shutdown",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(GatewayLifecycleResult<GatewayShutdownResult>))]
    [Description("Request graceful Gateway shutdown after its IPC response is written.")]
    public async Task<CallToolResult> ShutdownGatewayAsync(
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ITwinCatGatewayClient client =
                await ResolveClientAsync(server, cancellationToken).ConfigureAwait(false);
            GatewayShutdownResult result =
                await client.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            return CreateResult(
                new GatewayLifecycleResult<GatewayShutdownResult>
                {
                    Ok = true,
                    Result = result,
                },
                isError: false);
        }
        catch (GatewayClientException exception)
        {
            return CreateResult(
                new GatewayLifecycleResult<GatewayShutdownResult>
                {
                    Ok = false,
                    Error = exception.Error,
                },
                isError: true);
        }
    }

    [McpServerTool(
        Name = "twincat_xae_open",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(OperationResult<XaeOpenResult>))]
    [Description("Ensure the exact configured XAE solution session is attached or launched.")]
    public Task<CallToolResult> OpenXaeAsync(
        string profile,
        McpServer? server = null,
        CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(
            server: server,
            cancellationToken: cancellationToken,
            executeAsync: client => client.OpenXaeAsync(
                new XaeOpenParameters { Profile = RequireText(profile, nameof(profile)) },
                cancellationToken));

    [McpServerTool(
        Name = "twincat_xae_close",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(OperationResult<CloseXaeResult>))]
    [Description("Close the exact profile XAE process subject to PID-scoped consent.")]
    public Task<CallToolResult> CloseXaeAsync(
        string profile,
        string saveMode = "prompt",
        McpServer? server = null,
        CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(
            server: server,
            cancellationToken: cancellationToken,
            executeAsync: client => client.CloseXaeAsync(
                new CloseXaeParameters
                {
                    Profile = RequireText(profile, nameof(profile)),
                    SaveMode = McpGatewayJson.ParseEnum<XaeSaveMode>(
                        saveMode,
                        nameof(saveMode)),
                    TimeoutSeconds = DefaultOperationTimeoutSeconds,
                },
                cancellationToken));

    [McpServerTool(
        Name = "twincat_xae_sync",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(OperationResult<SynchronizeResult>))]
    [Description("Synchronize the exact XAE project graph with disk.")]
    public Task<CallToolResult> SynchronizeXaeAsync(
        string profile,
        string[]? changedPaths = null,
        bool discardDirtyDocuments = false,
        McpServer? server = null,
        CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(
            server: server,
            cancellationToken: cancellationToken,
            executeAsync: client => client.SynchronizeXaeAsync(
                new SynchronizeParameters
                {
                    Profile = RequireText(profile, nameof(profile)),
                    ChangedPaths = changedPaths?.ToList() ?? new(),
                    DiscardDirtyDocuments = discardDirtyDocuments,
                    TimeoutSeconds = DefaultOperationTimeoutSeconds,
                },
                cancellationToken));

    [McpServerTool(
        Name = "twincat_xae_build",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(OperationResult<XaeBuildResult>))]
    [Description("Compile one logical PLC project or build the complete solution; never activate.")]
    public Task<CallToolResult> BuildXaeAsync(
        string profile,
        string action = "rebuild",
        string scope = "plc",
        string? project = null,
        string[]? changedPaths = null,
        string detail = "compact",
        McpServer? server = null,
        CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(
            server: server,
            cancellationToken: cancellationToken,
            executeAsync: client => client.BuildXaeAsync(
                new XaeBuildParameters
                {
                    Profile = RequireText(profile, nameof(profile)),
                    Action = McpGatewayJson.ParseEnum<BuildAction>(action, nameof(action)),
                    Scope = McpGatewayJson.ParseEnum<XaeBuildScope>(scope, nameof(scope)),
                    Project = NullIfWhiteSpace(project),
                    ChangedPaths = changedPaths?.ToList() ?? new(),
                    Detail = McpGatewayJson.ParseEnum<DetailLevel>(detail, nameof(detail)),
                },
                cancellationToken));

    [McpServerTool(
        Name = "twincat_xae_activate",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(OperationResult<ActivationResult>))]
    [Description("Run native XAE activation with optional TcUnit verification.")]
    public Task<CallToolResult> ActivateXaeAsync(
        string profile,
        string finalTargetMode = "run",
        string verification = "none",
        string[]? changedPaths = null,
        McpServer? server = null,
        CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(
            server: server,
            cancellationToken: cancellationToken,
            executeAsync: client => client.ActivateXaeAsync(
                new ActivateParameters
                {
                    Profile = RequireText(profile, nameof(profile)),
                    FinalTargetMode = McpGatewayJson.ParseEnum<ActivationFinalTargetMode>(
                        finalTargetMode,
                        nameof(finalTargetMode)),
                    Verification = McpGatewayJson.ParseEnum<VerificationMode>(
                        verification,
                        nameof(verification)),
                    ChangedPaths = changedPaths?.ToList() ?? new(),
                    TimeoutSeconds = DefaultOperationTimeoutSeconds,
                },
                cancellationToken));

    [McpServerTool(
        Name = "twincat_target_config",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(OperationResult<TargetConfigResult>))]
    [Description("Transition the exact profile Target System to Config.")]
    public Task<CallToolResult> ConfigureTargetAsync(
        string profile,
        McpServer? server = null,
        CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(
            server: server,
            cancellationToken: cancellationToken,
            executeAsync: client => client.ConfigureTargetAsync(
                new TargetConfigParameters
                {
                    Profile = RequireText(profile, nameof(profile)),
                },
                cancellationToken));

    [McpServerTool(
        Name = "twincat_target_start_restart",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(OperationResult<TargetStartRestartResult>))]
    [Description("Start a stopped Target or restart a running Target with optional TcUnit verification.")]
    public Task<CallToolResult> StartRestartTargetAsync(
        string profile,
        string verification = "none",
        McpServer? server = null,
        CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(
            server: server,
            cancellationToken: cancellationToken,
            executeAsync: client => client.StartRestartTargetAsync(
                new TargetStartRestartParameters
                {
                    Profile = RequireText(profile, nameof(profile)),
                    Verification = McpGatewayJson.ParseEnum<VerificationMode>(
                        verification,
                        nameof(verification)),
                },
                cancellationToken));

    private async Task<CallToolResult> ExecuteMutationAsync<TResult>(
        McpServer? server,
        Func<ITwinCatGatewayClient, Task<OperationResult<TResult>>> executeAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            ITwinCatGatewayClient client =
                await ResolveClientAsync(server, cancellationToken).ConfigureAwait(false);
            OperationResult<TResult> result =
                await executeAsync(client).ConfigureAwait(false);
            return CreateResult(result, !result.Ok, result.Resources);
        }
        catch (GatewayClientException exception)
        {
            OperationResult<TResult> result = new()
            {
                Ok = false,
                Component = exception.Error.Component ?? GatewayComponent.Gateway,
                Stage = exception.Error.Stage ?? "gateway.connect",
                Completion = OperationCompletion.Failed,
                SideEffectsStarted = exception.Error.SideEffectsStarted ?? false,
                Error = exception.Error,
                Resources = exception.Error.Resources.ToList(),
            };
            return CreateResult(result, isError: true, result.Resources);
        }
    }

    private async Task<ITwinCatGatewayClient> ResolveClientAsync(
        McpServer? server,
        CancellationToken cancellationToken)
    {
        if (_fixedClient is not null)
        {
            return _fixedClient;
        }

        return await (_runtime
                ?? throw new InvalidOperationException("Gateway MCP runtime is unavailable."))
            .ResolveClientAsync(server, cancellationToken)
            .ConfigureAwait(false);
    }

    private static CallToolResult LifecycleFailure<TResult>(
        string code,
        string message,
        string stage) =>
        CreateResult(
            new GatewayLifecycleResult<TResult>
            {
                Ok = false,
                Error = new GatewayError
                {
                    Code = code,
                    Message = message,
                    Stage = stage,
                    Component = GatewayComponent.Gateway,
                    SideEffectsStarted = false,
                },
            },
            isError: true);

    private static CallToolResult CreateResult<T>(
        T value,
        bool isError,
        IEnumerable<ResourceReference>? resources = null)
    {
        List<ContentBlock> content = new()
        {
            new TextContentBlock
            {
                Text = McpGatewayJson.Serialize(value),
            },
        };
        if (resources is not null)
        {
            content.AddRange(
                resources
                    .Where(resource => !string.IsNullOrWhiteSpace(resource.Uri))
                    .GroupBy(resource => resource.Uri, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .Select(resource => (ContentBlock)new ResourceLinkBlock
                    {
                        Uri = resource.Uri,
                        Name = GetResourceName(resource.Uri),
                        MimeType = resource.MimeType,
                    }));
        }

        return new CallToolResult
        {
            Content = content,
            StructuredContent = McpGatewayJson.ToElement(value),
            IsError = isError,
        };
    }

    private static string GetResourceName(string uri)
    {
        int separator = uri.LastIndexOf('/');
        return separator >= 0 && separator + 1 < uri.Length
            ? uri.Substring(separator + 1)
            : uri;
    }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ModelContextProtocol.McpException($"{parameterName} is required.")
            : value;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
