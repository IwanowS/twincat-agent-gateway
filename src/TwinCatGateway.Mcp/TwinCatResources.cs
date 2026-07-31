using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TwinCatGateway.Client;
using TwinCatGateway.Contracts;
using TwinCatGateway.Ipc;

namespace TwinCatGateway.Mcp;

[McpServerResourceType]
public sealed class TwinCatResources
{
    private const int MaximumResourceCharacters = 1024 * 1024;
    private readonly GatewayMcpRuntime? _runtime;
    private readonly ITwinCatGatewayClient? _fixedClient;
    private readonly string _documentationDirectory;

    [ActivatorUtilitiesConstructor]
    public TwinCatResources(GatewayMcpRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _documentationDirectory = AppContext.BaseDirectory;
    }

    public TwinCatResources(ITwinCatGatewayClient client)
    {
        _fixedClient = client ?? throw new ArgumentNullException(nameof(client));
        _documentationDirectory = AppContext.BaseDirectory;
    }

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.GatewayState, Name = "Gateway state", MimeType = "application/json")]
    public Task<TextResourceContents> GetGatewayStateAsync(McpServer? server = null, CancellationToken cancellationToken = default) =>
        ReadGatewayAsync("twincat-gateway://state", server, cancellationToken);

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.GatewayDiagnostics, Name = "Gateway diagnostics", MimeType = "application/json")]
    public Task<TextResourceContents> GetGatewayDiagnosticsAsync(McpServer? server = null, CancellationToken cancellationToken = default) =>
        ReadGatewayAsync("twincat-gateway://diagnostics", server, cancellationToken);

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.ProfileCapabilities, Name = "Profile capabilities", MimeType = "application/json")]
    public Task<TextResourceContents> GetProfileCapabilitiesAsync(string profile, McpServer? server = null, CancellationToken cancellationToken = default) =>
        ReadGatewayAsync(ProfileUri(profile, "capabilities"), server, cancellationToken);

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.ProfileSources, Name = "Profile source manifest", MimeType = "application/json")]
    public Task<TextResourceContents> GetProfileSourcesAsync(string profile, McpServer? server = null, CancellationToken cancellationToken = default) =>
        ReadGatewayAsync(ProfileUri(profile, "sources"), server, cancellationToken);

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.ProfileSourceFiles, Name = "Profile source files", MimeType = "application/json")]
    public Task<TextResourceContents> GetProfileSourceFilesAsync(string profile, McpServer? server = null, CancellationToken cancellationToken = default) =>
        ReadGatewayAsync(ProfileUri(profile, "sources/files"), server, cancellationToken);

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.XaeState, Name = "XAE session state", MimeType = "application/json")]
    public Task<TextResourceContents> GetXaeStateAsync(string profile, McpServer? server = null, CancellationToken cancellationToken = default) =>
        ReadGatewayAsync(ObjectUri("twincat-xae", profile, "state"), server, cancellationToken);

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.XaeDiagnostics, Name = "XAE diagnostics", MimeType = "application/json")]
    public Task<TextResourceContents> GetXaeDiagnosticsAsync(string profile, McpServer? server = null, CancellationToken cancellationToken = default) =>
        ReadGatewayAsync(ObjectUri("twincat-xae", profile, "diagnostics"), server, cancellationToken);

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.XaeMessages, Name = "Current XAE messages", MimeType = "application/json")]
    public Task<TextResourceContents> GetXaeMessagesAsync(string profile, McpServer? server = null, CancellationToken cancellationToken = default) =>
        ReadGatewayAsync(ObjectUri("twincat-xae", profile, "messages/current"), server, cancellationToken);

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.TargetState, Name = "Target System state", MimeType = "application/json")]
    public Task<TextResourceContents> GetTargetStateAsync(string profile, McpServer? server = null, CancellationToken cancellationToken = default) =>
        ReadGatewayAsync(ObjectUri("twincat-target", profile, "state"), server, cancellationToken);

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.TargetDiagnostics, Name = "Target diagnostics", MimeType = "application/json")]
    public Task<TextResourceContents> GetTargetDiagnosticsAsync(string profile, McpServer? server = null, CancellationToken cancellationToken = default) =>
        ReadGatewayAsync(ObjectUri("twincat-target", profile, "diagnostics"), server, cancellationToken);

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.PlcState, Name = "PLC runtime state", MimeType = "application/json")]
    public Task<TextResourceContents> GetPlcStateAsync(string profile, string runtime, McpServer? server = null, CancellationToken cancellationToken = default) =>
        ReadGatewayAsync(PlcUri(profile, runtime, "state"), server, cancellationToken);

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.PlcDiagnostics, Name = "PLC runtime diagnostics", MimeType = "application/json")]
    public Task<TextResourceContents> GetPlcDiagnosticsAsync(string profile, string runtime, McpServer? server = null, CancellationToken cancellationToken = default) =>
        ReadGatewayAsync(PlcUri(profile, runtime, "diagnostics"), server, cancellationToken);

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.Operation, Name = "Operation summary", MimeType = "application/json")]
    public Task<TextResourceContents> GetOperationAsync(string operationId, McpServer? server = null, CancellationToken cancellationToken = default) =>
        ReadGatewayAsync(OperationUri(operationId, null), server, cancellationToken);

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.OperationEvents, Name = "Operation events", MimeType = "application/json")]
    public Task<TextResourceContents> GetOperationEventsAsync(string operationId, McpServer? server = null, CancellationToken cancellationToken = default) =>
        ReadGatewayAsync(OperationUri(operationId, "events"), server, cancellationToken);

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.OperationBuild, Name = "Operation build output", MimeType = "text/plain")]
    public Task<TextResourceContents> GetOperationBuildAsync(string operationId, McpServer? server = null, CancellationToken cancellationToken = default) =>
        ReadGatewayAsync(OperationUri(operationId, "build"), server, cancellationToken);

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.OperationXaeMessages, Name = "Operation XAE messages", MimeType = "application/json")]
    public Task<TextResourceContents> GetOperationXaeMessagesAsync(string operationId, McpServer? server = null, CancellationToken cancellationToken = default) =>
        ReadGatewayAsync(OperationUri(operationId, "xae-messages"), server, cancellationToken);

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.OperationXunit, Name = "Operation TcUnit xUnit report", MimeType = "application/xml")]
    public Task<TextResourceContents> GetOperationXunitAsync(string operationId, McpServer? server = null, CancellationToken cancellationToken = default) =>
        ReadGatewayAsync(OperationUri(operationId, "test/xunit"), server, cancellationToken);

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.OperationProjectNoise, Name = "Operation project noise", MimeType = "application/json")]
    public Task<TextResourceContents> GetOperationProjectNoiseAsync(string operationId, McpServer? server = null, CancellationToken cancellationToken = default) =>
        ReadGatewayAsync(OperationUri(operationId, "project-noise"), server, cancellationToken);

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.Setup, Name = "Gateway setup", MimeType = "text/plain")]
    public Task<TextResourceContents> GetSetupDocumentationAsync(CancellationToken cancellationToken = default) =>
        ReadDocumentationAsync("twincat-doc://setup", "SETUP_INSTRUCTIONS.txt", "text/plain", cancellationToken);

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.Configuration, Name = "Gateway configuration reference", MimeType = "text/markdown")]
    public Task<TextResourceContents> GetConfigurationDocumentationAsync(CancellationToken cancellationToken = default) =>
        ReadDocumentationAsync("twincat-doc://configuration", "CONFIGURATION.md", "text/markdown", cancellationToken);

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.Mcp, Name = "Gateway MCP reference", MimeType = "text/markdown")]
    public Task<TextResourceContents> GetMcpDocumentationAsync(CancellationToken cancellationToken = default) =>
        ReadDocumentationAsync("twincat-doc://mcp", "MCP_REFERENCE.md", "text/markdown", cancellationToken);

    [McpServerResource(UriTemplate = GatewayMcpCatalog.ResourceTemplates.CurrentLog, Name = "Current Gateway log", MimeType = "text/plain")]
    public Task<TextResourceContents> GetCurrentGatewayLogAsync(McpServer? server = null, CancellationToken cancellationToken = default) =>
        ReadGatewayAsync(GatewayResourceUris.CurrentGatewayLog, server, cancellationToken);

    private async Task<TextResourceContents> ReadGatewayAsync(
        string uri,
        McpServer? server,
        CancellationToken cancellationToken)
    {
        ITwinCatGatewayClient client = _fixedClient
            ?? await (_runtime ?? throw new InvalidOperationException("Gateway MCP runtime is unavailable."))
                .ResolveClientAsync(server, cancellationToken)
                .ConfigureAwait(false);
        try
        {
            ResourceContent resource = await client.GetResourceAsync(
                    uri,
                    MaximumResourceCharacters,
                    0,
                    cancellationToken)
                .ConfigureAwait(false);
            JsonObject metadata = new()
            {
                ["gatewayOffset"] = resource.Offset,
                ["gatewayTruncated"] = resource.Truncated,
            };
            if (resource.NextOffset is long nextOffset)
            {
                metadata["gatewayNextOffset"] = nextOffset;
            }

            return new TextResourceContents
            {
                Uri = resource.Uri,
                MimeType = resource.ContentType,
                Text = resource.Content,
                Meta = metadata,
            };
        }
        catch (GatewayClientException exception)
        {
            throw new McpException(McpGatewayJson.Serialize(exception.Error));
        }
    }

    private Task<TextResourceContents> ReadDocumentationAsync(
        string uri,
        string fileName,
        string mimeType,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = Path.Combine(_documentationDirectory, fileName);
        try
        {
            return Task.FromResult(new TextResourceContents
            {
                Uri = uri,
                MimeType = mimeType,
                Text = File.ReadAllText(path),
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"Documentation resource '{uri}' is unavailable: {exception.Message}");
        }
    }

    private static string ProfileUri(string profile, string suffix) =>
        $"twincat-profile://{Escape(profile)}/{suffix}";

    private static string ObjectUri(string scheme, string profile, string suffix) =>
        $"{scheme}://profile/{Escape(profile)}/{suffix}";

    private static string PlcUri(string profile, string runtime, string suffix) =>
        $"twincat-plc://profile/{Escape(profile)}/{Escape(runtime)}/{suffix}";

    private static string OperationUri(string operationId, string? suffix) =>
        $"twincat-operation://{Escape(operationId)}"
            + (suffix is null ? string.Empty : "/" + suffix);

    private static string Escape(string value) =>
        Uri.EscapeDataString(string.IsNullOrWhiteSpace(value)
            ? throw new McpException("Resource identity is required.")
            : value);
}
