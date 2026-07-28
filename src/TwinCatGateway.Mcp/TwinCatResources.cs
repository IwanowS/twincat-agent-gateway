using System;
using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TwinCatGateway.Client;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Mcp;

[McpServerResourceType]
public sealed class TwinCatResources
{
    private const int MaximumResourceCharacters =
        1024 * 1024;

    private readonly GatewayMcpRuntime? _runtime;
    private readonly ITwinCatGatewayClient? _fixedClient;

    [ActivatorUtilitiesConstructor]
    public TwinCatResources(GatewayMcpRuntime runtime)
    {
        _runtime = runtime
            ?? throw new ArgumentNullException(nameof(runtime));
    }

    public TwinCatResources(ITwinCatGatewayClient client)
    {
        _fixedClient = client
            ?? throw new ArgumentNullException(nameof(client));
    }

    [McpServerResource(
        UriTemplate =
            "twincat-log://{operationId}/build",
        Name = "TwinCAT build log",
        MimeType = "text/plain")]
    [Description(
        "Read the raw Build Output artifact for one operation.")]
    public Task<TextResourceContents> GetBuildLogAsync(
        string operationId,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        return ReadAsync(
            "twincat-log",
            operationId,
            "build",
            server,
            cancellationToken);
    }

    [McpServerResource(
        UriTemplate =
            "twincat-log://{operationId}/xae",
        Name = "TwinCAT XAE log",
        MimeType = "text/plain")]
    [Description(
        "Read the detailed XAE artifact for one operation.")]
    public Task<TextResourceContents> GetXaeLogAsync(
        string operationId,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        return ReadAsync(
            "twincat-log",
            operationId,
            "xae",
            server,
            cancellationToken);
    }

    [McpServerResource(
        UriTemplate =
            "twincat-test://{operationId}/xunit",
        Name = "TwinCAT TcUnit xUnit report",
        MimeType = "application/xml")]
    [Description(
        "Read the fresh xUnit report linked to one test operation.")]
    public Task<TextResourceContents> GetTestReportAsync(
        string operationId,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        return ReadAsync(
            "twincat-test",
            operationId,
            "xunit",
            server,
            cancellationToken);
    }

    [McpServerResource(
        UriTemplate =
            "twincat-diff://{operationId}/project-noise",
        Name = "TwinCAT project noise classification",
        MimeType = "application/json")]
    [Description(
        "Read the focused .tsproj noise-classifier artifact "
        + "without loading the whole project file.")]
    public Task<TextResourceContents> GetProjectNoiseAsync(
        string operationId,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        return ReadAsync(
            "twincat-diff",
            operationId,
            "project-noise",
            server,
            cancellationToken);
    }

    private async Task<TextResourceContents> ReadAsync(
        string scheme,
        string operationId,
        string artifact,
        McpServer? server,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new McpException(
                "operationId is required.");
        }

        string uri =
            $"{scheme}://{operationId}/{artifact}";
        ITwinCatGatewayClient client =
            _fixedClient
            ?? await (_runtime
                    ?? throw new InvalidOperationException(
                        "Gateway MCP runtime is unavailable."))
                .ResolveClientAsync(
                    server,
                    cancellationToken)
                .ConfigureAwait(false);
        GatewayResponse<ResourceContent> response =
            await client.GetResourceAsync(
                    uri,
                    MaximumResourceCharacters,
                    offset: 0,
                    cancellationToken)
                .ConfigureAwait(false);
        if (!response.Ok)
        {
            throw new McpException(
                McpGatewayJson.Serialize(response));
        }

        ResourceContent resource =
            response.Result
            ?? throw new McpException(
                "Gateway returned a successful resource "
                + "response without content.");
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
}
