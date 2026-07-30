using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using TwinCatGateway.Client;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Mcp;

[McpServerToolType]
public sealed class TwinCatTools
{
    private const int DefaultOperationTimeoutSeconds = 120;
    private const int ClientTimeoutGraceSeconds = 15;

    private readonly GatewayMcpRuntime? _runtime;
    private readonly ITwinCatGatewayClient? _fixedClient;
    private readonly GatewayOperationPoller? _fixedPoller;

    [ActivatorUtilitiesConstructor]
    public TwinCatTools(GatewayMcpRuntime runtime)
    {
        _runtime = runtime
            ?? throw new ArgumentNullException(nameof(runtime));
    }

    public TwinCatTools(
        ITwinCatGatewayClient client,
        GatewayOperationPoller poller)
    {
        _fixedClient = client
            ?? throw new ArgumentNullException(nameof(client));
        _fixedPoller = poller
            ?? throw new ArgumentNullException(nameof(poller));
    }

    [McpServerTool(
        Name = "gateway_start",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Explicitly start TwinCAT Agent Gateway for the "
        + "discovered project configuration. Checks project "
        + "process-control policy and never replaces another "
        + "project's running gateway.")]
    public async Task<string> StartGatewayAsync(
        [Description(
            "Maximum time to wait for gateway IPC readiness.")]
        int timeoutSeconds = 30,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        McpGatewayJson.RequirePositive(
            timeoutSeconds,
            nameof(timeoutSeconds));
        if (_runtime is null)
        {
            return McpGatewayJson.Serialize(
                new GatewayResponse<GatewayStartResult>
                {
                    Ok = false,
                    Error = new GatewayError
                    {
                        Code =
                            ErrorCodes.GatewayStartFailed,
                        Message =
                            "Gateway lifecycle runtime is "
                            + "not configured.",
                        Stage =
                            "gateway.start.runtime",
                    },
                });
        }

        GatewayResponse<GatewayStartResult> response =
            await _runtime.StartAsync(
                    server,
                    TimeSpan.FromSeconds(timeoutSeconds),
                    cancellationToken)
                .ConfigureAwait(false);
        return McpGatewayJson.Serialize(response);
    }

    [McpServerTool(
        Name = "gateway_shutdown",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Explicitly close TwinCAT Agent Gateway when the "
        + "project configuration permits agent shutdown. "
        + "Does not close a user-owned XAE instance.")]
    public async Task<string> ShutdownGatewayAsync(
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        GatewayToolSession session =
            await ResolveSessionAsync(
                    server,
                    cancellationToken)
                .ConfigureAwait(false);
        GatewayResponse<GatewayShutdownResult> response =
            await session.Client
                .ShutdownAsync(cancellationToken)
                .ConfigureAwait(false);
        return McpGatewayJson.Serialize(response);
    }

    [McpServerTool(
        Name = "twincat_status",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Return compact gateway, XAE, solution, target, "
        + "runtime, and last-operation status.")]
    public async Task<string> GetStatusAsync(
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        GatewayToolSession session =
            await ResolveSessionAsync(
                    server,
                    cancellationToken)
                .ConfigureAwait(false);
        GatewayResponse<GatewayStatusResult> response =
            await session.Client
                .GetStatusAsync(cancellationToken)
                .ConfigureAwait(false);
        return McpGatewayJson.Serialize(response);
    }

    [McpServerTool(
        Name = "twincat_get_xae_messages",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Read the current error and warning entries from the "
        + "Error List of the exact attached XAE solution.")]
    public async Task<string> GetXaeMessagesAsync(
        [Description(
            "Maximum XAE Error List entries in the response.")]
        int maximumMessages = 50,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        McpGatewayJson.RequirePositive(
            maximumMessages,
            nameof(maximumMessages));
        GatewayToolSession session =
            await ResolveSessionAsync(
                    server,
                    cancellationToken)
                .ConfigureAwait(false);
        GatewayResponse<XaeMessagesResult> response =
            await session.Client.GetXaeMessagesAsync(
                    new GetXaeMessagesParameters
                    {
                        MaximumMessages =
                            maximumMessages,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        return McpGatewayJson.Serialize(response);
    }

    [McpServerTool(
        Name = "twincat_build",
        Destructive = false,
        Idempotent = false,
        OpenWorld = false)]
    [Description(
        "Build, rebuild, or clean the configured solution and "
        + "wait for completion. Scans and policy-checks the selected "
        + "TwinCAT project graph; changedPaths is only a hint. "
        + "Never activates TwinCAT.")]
    public async Task<string> BuildAsync(
        [Description(
            "Operator-controlled gateway profile name.")]
        string profile,
        [Description(
            "build, rebuild, or clean. Default is rebuild.")]
        string action = "rebuild",
        [Description(
            "Optional solution configuration name.")]
        string? configuration = null,
        [Description(
            "Optional solution platform name.")]
        string? platform = null,
        [Description(
            "Externally edited project paths to synchronize "
            + "before the operation.")]
        string[]? changedPaths = null,
        [Description(
            "Discard unsaved XAE buffers only when the profile "
            + "explicitly permits it.")]
        bool discardDirtyDocuments = false,
        [Description(
            "compact or detailed result.")]
        string detail = "compact",
        [Description(
            "Gateway operation timeout in seconds.")]
        int timeoutSeconds =
            DefaultOperationTimeoutSeconds,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        McpGatewayJson.RequirePositive(
            timeoutSeconds,
            nameof(timeoutSeconds));
        BuildParameters parameters = new()
        {
            Profile = RequireText(profile, nameof(profile)),
            Action = McpGatewayJson.ParseEnum<BuildAction>(
                action,
                nameof(action)),
            Configuration = NullIfWhiteSpace(configuration),
            Platform = NullIfWhiteSpace(platform),
            ChangedPaths =
                changedPaths is null
                    ? new()
                    : new(changedPaths),
            DiscardDirtyDocuments =
                discardDirtyDocuments,
            Detail = McpGatewayJson.ParseEnum<DetailLevel>(
                detail,
                nameof(detail)),
            TimeoutSeconds = timeoutSeconds,
        };

        GatewayToolSession session =
            await ResolveSessionAsync(
                    server,
                    cancellationToken)
                .ConfigureAwait(false);
        GatewayResponse<OperationAccepted> accepted =
            await session.Client.StartBuildAsync(
                    parameters,
                    cancellationToken)
                .ConfigureAwait(false);
        if (!accepted.Ok || accepted.Result is null)
        {
            return McpGatewayJson.Serialize(accepted);
        }

        GatewayResponse<OperationDetails<BuildResult>> completed =
            await session.Poller.WaitAsync<BuildResult>(
                    accepted.Result.OperationId,
                    GetClientWaitTimeout(timeoutSeconds),
                    cancellationToken)
                .ConfigureAwait(false);
        return McpGatewayJson.Serialize(completed);
    }

    [McpServerTool(
        Name = "twincat_sync",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false)]
    [Description(
        "Force XAE to synchronize the selected TwinCAT project graph "
        + "with disk. Requires profile permission for agent use.")]
    public async Task<string> SynchronizeAsync(
        [Description("Operator-controlled gateway profile name.")]
        string profile,
        [Description(
            "Optional changed-path hints; the gateway always scans "
            + "the authoritative project graph.")]
        string[]? changedPaths = null,
        [Description(
            "Discard unsaved XAE buffers only when the profile "
            + "explicitly permits it.")]
        bool discardDirtyDocuments = false,
        [Description("Gateway operation timeout in seconds.")]
        int timeoutSeconds =
            DefaultOperationTimeoutSeconds,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        McpGatewayJson.RequirePositive(
            timeoutSeconds,
            nameof(timeoutSeconds));
        SynchronizeParameters parameters = new()
        {
            Profile = RequireText(profile, nameof(profile)),
            ChangedPaths = changedPaths is null
                ? new()
                : new(changedPaths),
            DiscardDirtyDocuments =
                discardDirtyDocuments,
            TimeoutSeconds = timeoutSeconds,
        };
        GatewayToolSession session =
            await ResolveSessionAsync(
                    server,
                    cancellationToken)
                .ConfigureAwait(false);
        GatewayResponse<OperationAccepted> accepted =
            await session.Client.StartSynchronizationAsync(
                    parameters,
                    cancellationToken)
                .ConfigureAwait(false);
        if (!accepted.Ok || accepted.Result is null)
        {
            return McpGatewayJson.Serialize(accepted);
        }

        GatewayResponse<OperationDetails<SynchronizeResult>> completed =
            await session.Poller.WaitAsync<SynchronizeResult>(
                    accepted.Result.OperationId,
                    GetClientWaitTimeout(timeoutSeconds),
                    cancellationToken)
                .ConfigureAwait(false);
        return McpGatewayJson.Serialize(completed);
    }

    [McpServerTool(
        Name = "twincat_close_xae",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false)]
    [Description(
        "Explicitly close the exact XAE process selected by the active "
        + "gateway profile. Requires allowCloseXae; discard also requires "
        + "allowDirtyDocumentDiscard.")]
    public async Task<string> CloseXaeAsync(
        [Description("save, discard, or prompt.")]
        string saveMode,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        CloseXaeParameters parameters = new()
        {
            SaveMode = McpGatewayJson.ParseEnum<XaeSaveMode>(
                saveMode,
                nameof(saveMode)),
            TimeoutSeconds = DefaultOperationTimeoutSeconds,
        };
        GatewayToolSession session =
            await ResolveSessionAsync(
                    server,
                    cancellationToken)
                .ConfigureAwait(false);
        GatewayResponse<OperationAccepted> accepted =
            await session.Client.StartCloseXaeAsync(
                    parameters,
                    cancellationToken)
                .ConfigureAwait(false);
        if (!accepted.Ok || accepted.Result is null)
        {
            return McpGatewayJson.Serialize(accepted);
        }

        GatewayResponse<OperationDetails<CloseXaeResult>> completed =
            await session.Poller.WaitAsync<CloseXaeResult>(
                    accepted.Result.OperationId,
                    GetClientWaitTimeout(
                        DefaultOperationTimeoutSeconds),
                    cancellationToken)
                .ConfigureAwait(false);
        return McpGatewayJson.Serialize(completed);
    }

    [McpServerTool(
        Name = "twincat_activate",
        Destructive = true,
        Idempotent = false,
        OpenWorld = false)]
    [Description(
        "Explicitly activate the configured allow-listed remote "
        + "TwinCAT target, optionally enter Run, verify postconditions, and "
        + "optionally link a TcUnit run.")]
    public async Task<string> ActivateAsync(
        [Description(
            "Operator-controlled activation profile name.")]
        string profile,
        [Description(
            "auto, true, or false. Auto uses profile policy.")]
        string waitForTcUnit = "auto",
        [Description(
            "Confirm the XAE Run prompt after activation. "
            + "False cancels that prompt without forcing a runtime "
            + "mode transition.")]
        bool runAfterActivation = true,
        [Description(
            "Gateway operation timeout in seconds.")]
        int timeoutSeconds =
            DefaultOperationTimeoutSeconds,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        McpGatewayJson.RequirePositive(
            timeoutSeconds,
            nameof(timeoutSeconds));
        ActivateParameters parameters = new()
        {
            Profile = RequireText(profile, nameof(profile)),
            RunAfterActivation = runAfterActivation,
            WaitForTcUnit =
                McpGatewayJson.ParseOptionalBoolean(
                    waitForTcUnit,
                    nameof(waitForTcUnit)),
            TimeoutSeconds = timeoutSeconds,
        };

        GatewayToolSession session =
            await ResolveSessionAsync(
                    server,
                    cancellationToken)
                .ConfigureAwait(false);
        GatewayResponse<OperationAccepted> accepted =
            await session.Client.StartActivationAsync(
                    parameters,
                    cancellationToken)
                .ConfigureAwait(false);
        if (!accepted.Ok || accepted.Result is null)
        {
            return McpGatewayJson.Serialize(accepted);
        }

        GatewayResponse<OperationDetails<ActivationResult>>
            completed =
                await session.Poller.WaitAsync<ActivationResult>(
                        accepted.Result.OperationId,
                        GetClientWaitTimeout(timeoutSeconds),
                        cancellationToken)
                    .ConfigureAwait(false);
        return McpGatewayJson.Serialize(completed);
    }

    [McpServerTool(
        Name = "twincat_recover_to_config",
        Destructive = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Explicitly transition the configured allow-listed remote "
        + "TwinCAT runtime to Config Mode. Use this only after the "
        + "runtime diagnostic state has been inspected; build and "
        + "activation never invoke recovery automatically.")]
    public async Task<string> RecoverToConfigAsync(
        [Description(
            "Operator-controlled activation profile name.")]
        string profile,
        [Description(
            "Gateway operation timeout in seconds.")]
        int timeoutSeconds =
            DefaultOperationTimeoutSeconds,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        McpGatewayJson.RequirePositive(
            timeoutSeconds,
            nameof(timeoutSeconds));
        RecoverToConfigParameters parameters = new()
        {
            Profile = RequireText(profile, nameof(profile)),
            TimeoutSeconds = timeoutSeconds,
        };

        GatewayToolSession session =
            await ResolveSessionAsync(
                    server,
                    cancellationToken)
                .ConfigureAwait(false);
        GatewayResponse<OperationAccepted> accepted =
            await session.Client.StartRecoverToConfigAsync(
                    parameters,
                    cancellationToken)
                .ConfigureAwait(false);
        if (!accepted.Ok || accepted.Result is null)
        {
            return McpGatewayJson.Serialize(accepted);
        }

        GatewayResponse<
            OperationDetails<RecoverToConfigResult>> completed =
                await session.Poller
                    .WaitAsync<RecoverToConfigResult>(
                        accepted.Result.OperationId,
                        GetClientWaitTimeout(timeoutSeconds),
                        cancellationToken)
                    .ConfigureAwait(false);
        return McpGatewayJson.Serialize(completed);
    }

    [McpServerTool(
        Name = "twincat_get_diagnostics",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Return diagnostics and one page of the unified event "
        + "stream after a cursor. Use severity filtering for "
        + "errors without a separate error state.")]
    public async Task<string> GetDiagnosticsAsync(
        [Description(
            "Expected event stream ID from an earlier response, "
            + "or null on the first read.")]
        string? eventStreamId = null,
        [Description(
            "Return matching events after this cursor.")]
        long afterCursor = 0,
        [Description(
            "Maximum events in this response.")]
        int maximumEvents = 100,
        [Description(
            "all, info, warning, or error.")]
        string minimumSeverity = "all",
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        McpGatewayJson.RequireNonNegative(
            afterCursor,
            nameof(afterCursor));
        McpGatewayJson.RequirePositive(
            maximumEvents,
            nameof(maximumEvents));
        GetDiagnosticsParameters parameters = new()
        {
            EventStreamId =
                NullIfWhiteSpace(eventStreamId),
            AfterEventCursor = afterCursor,
            MaximumEvents = maximumEvents,
            MinimumSeverity =
                McpGatewayJson.ParseSeverity(
                    minimumSeverity),
        };

        GatewayToolSession session =
            await ResolveSessionAsync(
                    server,
                    cancellationToken)
                .ConfigureAwait(false);
        GatewayResponse<GatewayDiagnosticsResult> response =
            await session.Client.GetDiagnosticsAsync(
                    parameters,
                    cancellationToken)
                .ConfigureAwait(false);
        return McpGatewayJson.Serialize(response);
    }

    [McpServerTool(
        Name = "twincat_get_test_results",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Return compact TcUnit results for the linked test "
        + "operation after ADS completion and a fresh xUnit report.")]
    public async Task<string> GetTestResultsAsync(
        [Description(
            "Linked TcUnit test operation ID.")]
        string operationId,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        GatewayToolSession session =
            await ResolveSessionAsync(
                    server,
                    cancellationToken)
                .ConfigureAwait(false);
        GatewayResponse<OperationDetails<TestResult>> response =
            await session.Client.GetTestResultsAsync(
                    RequireText(
                        operationId,
                        nameof(operationId)),
                    cancellationToken)
                .ConfigureAwait(false);
        return McpGatewayJson.Serialize(response);
    }

    private async Task<GatewayToolSession>
        ResolveSessionAsync(
            McpServer? server,
            CancellationToken cancellationToken)
    {
        if (_fixedClient is not null
            && _fixedPoller is not null)
        {
            return new GatewayToolSession(
                _fixedClient,
                _fixedPoller);
        }

        ITwinCatGatewayClient client =
            await (_runtime
                    ?? throw new InvalidOperationException(
                        "Gateway MCP runtime is unavailable."))
                .ResolveClientAsync(
                    server,
                    cancellationToken)
                .ConfigureAwait(false);
        return new GatewayToolSession(
            client,
            new GatewayOperationPoller(client));
    }

    private static TimeSpan GetClientWaitTimeout(
        int operationTimeoutSeconds)
    {
        return TimeSpan.FromSeconds(
            checked(
                operationTimeoutSeconds
                + ClientTimeoutGraceSeconds));
    }

    private static string RequireText(
        string value,
        string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ModelContextProtocol.McpException(
                $"{parameterName} is required.")
            : value;
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value;
    }

    private sealed class GatewayToolSession
    {
        public GatewayToolSession(
            ITwinCatGatewayClient client,
            GatewayOperationPoller poller)
        {
            Client = client;
            Poller = poller;
        }

        public ITwinCatGatewayClient Client { get; }

        public GatewayOperationPoller Poller { get; }
    }
}
