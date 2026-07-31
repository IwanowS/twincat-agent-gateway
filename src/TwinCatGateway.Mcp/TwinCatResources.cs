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
    private const int MaximumResourceCharacters =
        1024 * 1024;
    private const string SetupDocumentationUri =
        "twincat-doc://setup";
    private const string ConfigurationDocumentationUri =
        "twincat-doc://configuration";
    private const string SetupDocumentationFileName =
        "SETUP_INSTRUCTIONS.txt";
    private const string ConfigurationDocumentationFileName =
        "CONFIGURATION.md";

    private readonly GatewayMcpRuntime? _runtime;
    private readonly ITwinCatGatewayClient? _fixedClient;
    private readonly string _documentationDirectory;

    [ActivatorUtilitiesConstructor]
    public TwinCatResources(GatewayMcpRuntime runtime)
    {
        _runtime = runtime
            ?? throw new ArgumentNullException(nameof(runtime));
        _documentationDirectory =
            AppContext.BaseDirectory;
    }

    public TwinCatResources(ITwinCatGatewayClient client)
    {
        _fixedClient = client
            ?? throw new ArgumentNullException(nameof(client));
        _documentationDirectory =
            AppContext.BaseDirectory;
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
        UriTemplate = GatewayResourceUris.CurrentGatewayLog,
        Name = "Current TwinCAT Agent Gateway log",
        MimeType = "text/plain")]
    [Description(
        "Get the absolute path of the gateway log segment "
        + "that is currently open.")]
    public Task<TextResourceContents>
        GetCurrentGatewayLogPathAsync(
            McpServer? server = null,
            CancellationToken cancellationToken = default)
    {
        return ReadUriAsync(
            GatewayResourceUris.CurrentGatewayLog,
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

    [McpServerResource(
        UriTemplate = SetupDocumentationUri,
        Name = "TwinCAT Agent Gateway setup instructions",
        MimeType = "text/plain")]
    [Description(
        "Read the installed setup and agent workflow instructions.")]
    public Task<TextResourceContents> GetSetupDocumentationAsync(
        CancellationToken cancellationToken = default)
    {
        return ReadDocumentationAsync(
            _documentationDirectory,
            SetupDocumentationUri,
            SetupDocumentationFileName,
            "text/plain",
            cancellationToken);
    }

    [McpServerResource(
        UriTemplate = ConfigurationDocumentationUri,
        Name = "TwinCAT Agent Gateway configuration reference",
        MimeType = "text/markdown")]
    [Description(
        "Read the complete twincat-gateway.json option reference "
        + "and examples.")]
    public Task<TextResourceContents>
        GetConfigurationDocumentationAsync(
            CancellationToken cancellationToken = default)
    {
        return ReadDocumentationAsync(
            _documentationDirectory,
            ConfigurationDocumentationUri,
            ConfigurationDocumentationFileName,
            "text/markdown",
            cancellationToken);
    }

    private static Task<TextResourceContents>
        ReadDocumentationAsync(
            string documentationDirectory,
            string uri,
            string fileName,
            string mimeType,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = Path.Combine(
            documentationDirectory,
            fileName);
        try
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "Installed MCP documentation was not found.",
                    path);
            }

            return Task.FromResult(
                new TextResourceContents
                {
                    Uri = uri,
                    MimeType = mimeType,
                    Text = File.ReadAllText(path),
                });
        }
        catch (Exception exception)
            when (exception is IOException
                || exception
                    is UnauthorizedAccessException)
        {
            throw new McpException(
                "MCP documentation resource '"
                + uri
                + "' is unavailable: "
                + exception.Message);
        }
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
        return await ReadUriAsync(
                uri,
                server,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<TextResourceContents> ReadUriAsync(
        string uri,
        McpServer? server,
        CancellationToken cancellationToken)
    {
        ITwinCatGatewayClient client =
            _fixedClient
            ?? await (_runtime
                    ?? throw new InvalidOperationException(
                        "Gateway MCP runtime is unavailable."))
                .ResolveClientAsync(
                    server,
                    cancellationToken)
                .ConfigureAwait(false);
        ResourceContent resource;
        try
        {
            resource = await client.GetResourceAsync(
                    uri,
                    MaximumResourceCharacters,
                    offset: 0,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GatewayClientException exception)
        {
            throw new McpException(
                McpGatewayJson.Serialize(exception.Error));
        }
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
