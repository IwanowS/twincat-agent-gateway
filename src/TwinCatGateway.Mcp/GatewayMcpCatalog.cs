using System;
using System.Collections.Generic;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Mcp;

public sealed record McpToolDefinition(
    string Name,
    string Description,
    string InputSchema,
    Type OutputSchemaType,
    string Capability,
    bool ReadOnly,
    bool Destructive,
    bool Idempotent,
    bool OpenWorld);

public sealed record McpResourceDefinition(
    string UriTemplate,
    string Name,
    string MimeType);

public static class GatewayMcpCatalog
{
    public static class ToolNames
    {
        public const string GatewayStart = "gateway_start";
        public const string GatewayShutdown = "gateway_shutdown";
        public const string XaeOpen = "twincat_xae_open";
        public const string XaeClose = "twincat_xae_close";
        public const string XaeSync = "twincat_xae_sync";
        public const string XaeBuild = "twincat_xae_build";
        public const string XaeActivate = "twincat_xae_activate";
        public const string TargetConfig = "twincat_target_config";
        public const string TargetStartRestart = "twincat_target_start_restart";
    }

    public static class ResourceTemplates
    {
        public const string GatewayState = "twincat-gateway://state";
        public const string GatewayDiagnostics = "twincat-gateway://diagnostics";
        public const string ProfileCapabilities = "twincat-profile://{profile}/capabilities";
        public const string ProfileSources = "twincat-profile://{profile}/sources";
        public const string ProfileSourceFiles = "twincat-profile://{profile}/sources/files";
        public const string XaeState = "twincat-xae://profile/{profile}/state";
        public const string XaeDiagnostics = "twincat-xae://profile/{profile}/diagnostics";
        public const string XaeMessages = "twincat-xae://profile/{profile}/messages/current";
        public const string TargetState = "twincat-target://profile/{profile}/state";
        public const string TargetDiagnostics = "twincat-target://profile/{profile}/diagnostics";
        public const string PlcState = "twincat-plc://profile/{profile}/{runtime}/state";
        public const string PlcDiagnostics = "twincat-plc://profile/{profile}/{runtime}/diagnostics";
        public const string Operation = "twincat-operation://{operationId}";
        public const string OperationEvents = "twincat-operation://{operationId}/events";
        public const string OperationBuild = "twincat-operation://{operationId}/build";
        public const string OperationXaeMessages = "twincat-operation://{operationId}/xae-messages";
        public const string OperationXunit = "twincat-operation://{operationId}/test/xunit";
        public const string OperationProjectNoise = "twincat-operation://{operationId}/project-noise";
        public const string Setup = "twincat-doc://setup";
        public const string Configuration = "twincat-doc://configuration";
        public const string Mcp = "twincat-doc://mcp";
        public const string CurrentLog = "twincat-log://gateway/current";
    }

    public static IReadOnlyList<McpToolDefinition> Tools { get; } =
        new McpToolDefinition[]
        {
            Tool(ToolNames.GatewayStart, "Start or reuse the configured TwinCAT Agent Gateway desktop process.", "config?: string", typeof(GatewayLifecycleResult<GatewayStartResult>), "gateway.processControl.allowStart", destructive: false, idempotent: true),
            Tool(ToolNames.GatewayShutdown, "Request graceful Gateway shutdown after its IPC response is written.", "(empty)", typeof(GatewayLifecycleResult<GatewayShutdownResult>), "gateway.processControl.allowShutdown", destructive: true, idempotent: true),
            Tool(ToolNames.XaeOpen, "Ensure the exact configured XAE solution session is attached or launched.", "profile: string", typeof(OperationResult<XaeOpenResult>), "profile.xae.capabilities.launch", destructive: false, idempotent: true),
            Tool(ToolNames.XaeClose, "Close the exact profile XAE process subject to PID-scoped consent.", "profile: string; saveMode?: save|discard|prompt", typeof(OperationResult<CloseXaeResult>), "profile.xae.capabilities.close + PID consent", destructive: true, idempotent: false),
            Tool(ToolNames.XaeSync, "Synchronize the exact XAE project graph with disk.", "profile: string; changedPaths?: string[]; discardDirtyDocuments?: boolean", typeof(OperationResult<SynchronizeResult>), "profile.xae.capabilities.synchronize", destructive: false, idempotent: false),
            Tool(ToolNames.XaeBuild, "Compile one logical PLC project or build the complete solution; never activate.", "profile: string; action?: build|rebuild|clean; scope?: plc|solution; project?: string; changedPaths?: string[]; detail?: compact|full", typeof(OperationResult<XaeBuildResult>), "profile.xae.capabilities.build", destructive: false, idempotent: false),
            Tool(ToolNames.XaeActivate, "Run native XAE activation with optional TcUnit verification.", "profile: string; finalTargetMode?: run|unchanged; verification?: none|tcunit; changedPaths?: string[]", typeof(OperationResult<ActivationResult>), "profile.xae.capabilities.activate (+ tcUnitVerification)", destructive: true, idempotent: false),
            Tool(ToolNames.TargetConfig, "Transition the exact profile Target System to Config.", "profile: string", typeof(OperationResult<TargetConfigResult>), "profile.target.capabilities.config", destructive: true, idempotent: true),
            Tool(ToolNames.TargetStartRestart, "Start a stopped Target or restart a running Target with optional TcUnit verification.", "profile: string; verification?: none|tcunit", typeof(OperationResult<TargetStartRestartResult>), "profile.target.capabilities.startRestart (+ tcUnitVerification)", destructive: true, idempotent: false),
        };

    public static IReadOnlyList<McpResourceDefinition> Resources { get; } =
        new McpResourceDefinition[]
        {
            Resource(ResourceTemplates.GatewayState, "Gateway state"),
            Resource(ResourceTemplates.GatewayDiagnostics, "Gateway diagnostics"),
            Resource(ResourceTemplates.ProfileCapabilities, "Profile capabilities"),
            Resource(ResourceTemplates.ProfileSources, "Profile source manifest"),
            Resource(ResourceTemplates.ProfileSourceFiles, "Profile source files"),
            Resource(ResourceTemplates.XaeState, "XAE session state"),
            Resource(ResourceTemplates.XaeDiagnostics, "XAE diagnostics"),
            Resource(ResourceTemplates.XaeMessages, "Current XAE messages"),
            Resource(ResourceTemplates.TargetState, "Target System state"),
            Resource(ResourceTemplates.TargetDiagnostics, "Target diagnostics"),
            Resource(ResourceTemplates.PlcState, "PLC runtime state"),
            Resource(ResourceTemplates.PlcDiagnostics, "PLC runtime diagnostics"),
            Resource(ResourceTemplates.Operation, "Operation summary"),
            Resource(ResourceTemplates.OperationEvents, "Operation events"),
            Resource(ResourceTemplates.OperationBuild, "Operation build output", "text/plain"),
            Resource(ResourceTemplates.OperationXaeMessages, "Operation XAE messages"),
            Resource(ResourceTemplates.OperationXunit, "Operation TcUnit xUnit report", "application/xml"),
            Resource(ResourceTemplates.OperationProjectNoise, "Operation project noise"),
            Resource(ResourceTemplates.Setup, "Gateway setup", "text/plain"),
            Resource(ResourceTemplates.Configuration, "Gateway configuration reference", "text/markdown"),
            Resource(ResourceTemplates.Mcp, "Gateway MCP reference", "text/markdown"),
            Resource(ResourceTemplates.CurrentLog, "Current Gateway log", "text/plain"),
        };

    private static McpToolDefinition Tool(
        string name,
        string description,
        string inputSchema,
        Type outputSchemaType,
        string capability,
        bool destructive,
        bool idempotent) =>
        new(name, description, inputSchema, outputSchemaType, capability, false, destructive, idempotent, false);

    private static McpResourceDefinition Resource(
        string uriTemplate,
        string name,
        string mimeType = "application/json") =>
        new(uriTemplate, name, mimeType);
}
