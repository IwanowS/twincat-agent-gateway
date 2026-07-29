using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TwinCatGateway.Client;
using TwinCatGateway.Contracts;
using TwinCatGateway.Mcp;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class McpAdapterTests
{
    private static readonly string[] ExpectedToolNames =
    {
        "gateway_shutdown",
        "gateway_start",
        "twincat_activate",
        "twincat_build",
        "twincat_get_diagnostics",
        "twincat_get_test_results",
        "twincat_recover_to_config",
        "twincat_status",
        "twincat_sync",
    };

    private static readonly string[] ExpectedResourceTemplates =
    {
        "twincat-diff://{operationId}/project-noise",
        "twincat-log://{operationId}/build",
        "twincat-log://{operationId}/xae",
        "twincat-test://{operationId}/xunit",
    };

    private static readonly string[] ExpectedChangedPaths =
    {
        @"C:\Project\MAIN.TcPOU",
    };

    [Fact]
    public async Task StdioServerAdvertisesMvpSurface()
    {
        string serverExecutable =
            Path.ChangeExtension(
                typeof(TwinCatTools).Assembly.Location,
                ".exe");
        Assert.True(File.Exists(serverExecutable));
        StdioClientTransport transport = new(
            new StdioClientTransportOptions
            {
                Command = serverExecutable,
                Arguments =
                [
                    "--pipe",
                    "mcp-schema-test-no-gateway",
                ],
                Name = "TwinCAT Gateway MCP schema test",
                ShutdownTimeout = TimeSpan.FromSeconds(5),
            });
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(15));
        await using var client =
            await McpClient.CreateAsync(
                transport,
                cancellationToken: timeout.Token);

        var tools =
            await client.ListToolsAsync(
                cancellationToken: timeout.Token);
        var resources =
            await client.ListResourceTemplatesAsync(
                cancellationToken: timeout.Token);

        Assert.Equal(
            ExpectedToolNames,
            tools
                .Select(tool => tool.Name)
                .OrderBy(
                    name => name,
                    StringComparer.Ordinal));
        Assert.Equal(
            ExpectedResourceTemplates,
            resources
                .Select(resource => resource.UriTemplate)
                .OrderBy(
                    uri => uri,
                    StringComparer.Ordinal));
    }

    [Fact]
    public void ToolSurfaceMatchesMvpContract()
    {
        MethodInfo[] methods =
            typeof(TwinCatTools).GetMethods(
                BindingFlags.Instance
                | BindingFlags.Public);
        McpServerToolAttribute[] attributes =
            methods
                .Select(
                    method =>
                        method.GetCustomAttribute<
                            McpServerToolAttribute>())
                .Where(
                    attribute =>
                        attribute is not null)
                .Cast<McpServerToolAttribute>()
                .ToArray();

        Assert.Equal(
            ExpectedToolNames,
            attributes
                .Select(attribute => attribute.Name)
                .OrderBy(name => name, StringComparer.Ordinal));
        McpServerToolAttribute activation =
            Assert.Single(
                attributes,
                attribute =>
                    attribute.Name
                    == "twincat_activate");
        Assert.True(activation.Destructive);
        Assert.False(activation.ReadOnly);
        McpServerToolAttribute shutdown =
            Assert.Single(
                attributes,
                attribute =>
                    attribute.Name
                    == "gateway_shutdown");
        Assert.True(shutdown.Destructive);
        Assert.False(shutdown.ReadOnly);
        McpServerToolAttribute synchronization =
            Assert.Single(
                attributes,
                attribute =>
                    attribute.Name == "twincat_sync");
        Assert.True(synchronization.Destructive);
        Assert.False(synchronization.ReadOnly);
        McpServerToolAttribute recovery =
            Assert.Single(
                attributes,
                attribute =>
                    attribute.Name
                    == "twincat_recover_to_config");
        Assert.True(recovery.Destructive);
        Assert.True(recovery.Idempotent);
        Assert.False(recovery.ReadOnly);
        Assert.All(
            attributes,
            attribute => Assert.False(attribute.OpenWorld));
    }

    [Fact]
    public void BuildSchemaContainsOnlyCompactAgentArguments()
    {
        FakeGatewayClient client = new();
        TwinCatTools target = new(
            client,
            new GatewayOperationPoller(client));
        MethodInfo method =
            typeof(TwinCatTools).GetMethod(
                nameof(TwinCatTools.BuildAsync))
            ?? throw new InvalidOperationException(
                "Build tool method was not found.");
        McpServerTool tool =
            McpServerTool.Create(method, target);
        JsonElement properties =
            tool.ProtocolTool.InputSchema.GetProperty(
                "properties");

        Assert.Equal(8, properties.EnumerateObject().Count());
        Assert.True(properties.TryGetProperty("profile", out _));
        Assert.True(properties.TryGetProperty("changedPaths", out _));
        Assert.True(
            properties.TryGetProperty(
                "discardDirtyDocuments",
                out _));
        Assert.False(
            properties.TryGetProperty(
                "cancellationToken",
                out _));
    }

    [Fact]
    public void GatewayStartSchemaContainsOnlyTimeout()
    {
        FakeGatewayClient client = new();
        TwinCatTools target = new(
            client,
            new GatewayOperationPoller(client));
        MethodInfo method =
            typeof(TwinCatTools).GetMethod(
                nameof(TwinCatTools.StartGatewayAsync))
            ?? throw new InvalidOperationException(
                "Gateway start tool method was not found.");
        McpServerTool tool =
            McpServerTool.Create(method, target);
        JsonElement properties =
            tool.ProtocolTool.InputSchema.GetProperty(
                "properties");

        Assert.Single(properties.EnumerateObject());
        Assert.True(
            properties.TryGetProperty(
                "timeoutSeconds",
                out _));
        Assert.False(
            properties.TryGetProperty(
                "server",
                out _));
    }

    [Fact]
    public void GatewayShutdownSchemaContainsNoAgentArguments()
    {
        FakeGatewayClient client = new();
        TwinCatTools target = new(
            client,
            new GatewayOperationPoller(client));
        MethodInfo method =
            typeof(TwinCatTools).GetMethod(
                nameof(TwinCatTools.ShutdownGatewayAsync))
            ?? throw new InvalidOperationException(
                "Gateway shutdown tool method was not found.");
        McpServerTool tool =
            McpServerTool.Create(method, target);
        JsonElement properties =
            tool.ProtocolTool.InputSchema.GetProperty(
                "properties");

        Assert.Empty(properties.EnumerateObject());
    }

    [Fact]
    public async Task GatewayShutdownReturnsPolicyCheckedGatewayResponse()
    {
        FakeGatewayClient client = new();
        TwinCatTools tools = new(
            client,
            new GatewayOperationPoller(client));

        string result = await tools.ShutdownGatewayAsync();

        using JsonDocument json = JsonDocument.Parse(result);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(
            json.RootElement
                .GetProperty("result")
                .GetProperty("shutdownRequested")
                .GetBoolean());
    }

    [Fact]
    public void ResourceSurfaceMatchesMvpContract()
    {
        string?[] templates =
            typeof(TwinCatResources)
                .GetMethods(
                    BindingFlags.Instance
                    | BindingFlags.Public)
                .Select(
                    method =>
                        method.GetCustomAttribute<
                            McpServerResourceAttribute>())
                .Where(
                    attribute =>
                        attribute is not null)
                .Select(
                    attribute =>
                        attribute!.UriTemplate)
                .OrderBy(
                    uri => uri,
                    StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(
            ExpectedResourceTemplates,
            templates);
    }

    [Fact]
    public async Task BuildMapsParametersAndReturnsTerminalResponse()
    {
        FakeGatewayClient client = new()
        {
            BuildAccepted = Accepted("build-1"),
        };
        client.BuildOperations.Enqueue(
            Operation(
                "build-1",
                OperationState.Succeeded,
                new BuildResult
                {
                    Ok = true,
                    Counts = new DiagnosticCounts(),
                }));
        TwinCatTools tools = new(
            client,
            new GatewayOperationPoller(client));

        string result = await tools.BuildAsync(
            "fixture",
            action: "clean",
            changedPaths:
            [
                @"C:\Project\MAIN.TcPOU",
            ],
            timeoutSeconds: 5);

        Assert.Equal("fixture", client.Build?.Profile);
        Assert.Equal(BuildAction.Clean, client.Build?.Action);
        Assert.Equal(5, client.Build?.TimeoutSeconds);
        Assert.Equal(
            ExpectedChangedPaths,
            client.Build?.ChangedPaths);
        using JsonDocument json = JsonDocument.Parse(result);
        Assert.Equal(
            "succeeded",
            json.RootElement.GetProperty("result")
                .GetProperty("operation")
                .GetProperty("state")
                .GetString());
    }

    [Fact]
    public async Task DiagnosticsMapsCursorAndSeverity()
    {
        FakeGatewayClient client = new()
        {
            DiagnosticsResponse =
                new GatewayResponse<
                    GatewayDiagnosticsResult>
                {
                    Ok = true,
                    Result =
                        new GatewayDiagnosticsResult(),
                },
        };
        TwinCatTools tools = new(
            client,
            new GatewayOperationPoller(client));

        await tools.GetDiagnosticsAsync(
            eventStreamId: "stream-1",
            afterCursor: 42,
            maximumEvents: 7,
            minimumSeverity: "error");

        Assert.Equal(
            "stream-1",
            client.Diagnostics?.EventStreamId);
        Assert.Equal(
            42,
            client.Diagnostics?.AfterEventCursor);
        Assert.Equal(
            7,
            client.Diagnostics?.MaximumEvents);
        Assert.Equal(
            DiagnosticSeverity.Error,
            client.Diagnostics?.MinimumSeverity);
    }

    [Fact]
    public async Task SynchronizeMapsForceSyncArguments()
    {
        FakeGatewayClient client = new()
        {
            SynchronizeAccepted = Accepted("sync-1"),
        };
        client.SynchronizeOperations.Enqueue(
            Operation(
                "sync-1",
                OperationState.Succeeded,
                new SynchronizeResult
                {
                    Ok = true,
                    Scope = SynchronizationScope.TwinCatProject,
                }));
        TwinCatTools tools = new(
            client,
            new GatewayOperationPoller(client));

        await tools.SynchronizeAsync(
            "fixture",
            changedPaths: ExpectedChangedPaths,
            discardDirtyDocuments: true,
            timeoutSeconds: 5);

        Assert.Equal("fixture", client.Synchronize?.Profile);
        Assert.True(
            client.Synchronize?.DiscardDirtyDocuments);
        Assert.Equal(
            ExpectedChangedPaths,
            client.Synchronize?.ChangedPaths);
    }

    [Fact]
    public async Task ResourcePreservesContentAndPagingMetadata()
    {
        const string operationId =
            "0123456789abcdef0123456789abcdef";
        FakeGatewayClient client = new()
        {
            ResourceResponse =
                new GatewayResponse<ResourceContent>
                {
                    Ok = true,
                    Result = new ResourceContent
                    {
                        Uri =
                            "twincat-log://"
                            + operationId
                            + "/build",
                        ContentType = "text/plain",
                        Content = "build output",
                        Offset = 0,
                        NextOffset = 12,
                        Truncated = true,
                    },
                },
        };
        TwinCatResources resources = new(client);

        TextResourceContents result =
            await resources.GetBuildLogAsync(
                operationId);

        Assert.Equal("build output", result.Text);
        Assert.Equal(
            1024 * 1024,
            client.ResourceMaximumCharacters);
        Assert.Equal(0, client.ResourceOffset);
        Assert.NotNull(result.Meta);
        Assert.True(
            result.Meta["gatewayTruncated"]?.GetValue<bool>());
        Assert.Equal(
            12,
            result.Meta["gatewayNextOffset"]
                ?.GetValue<long>());
    }

    [Fact]
    public async Task ResourceFailureIsReportedAsMcpError()
    {
        FakeGatewayClient client = new()
        {
            ResourceResponse =
                new GatewayResponse<ResourceContent>
                {
                    Ok = false,
                    Error = new GatewayError
                    {
                        Code = "RESOURCE_NOT_FOUND",
                        Message = "No resource.",
                    },
                },
        };
        TwinCatResources resources = new(client);

        McpException exception =
            await Assert.ThrowsAsync<McpException>(
                () => resources.GetBuildLogAsync(
                    "0123456789abcdef0123456789abcdef"));

        Assert.Contains(
            "RESOURCE_NOT_FOUND",
            exception.Message);
    }

    private static GatewayResponse<OperationAccepted>
        Accepted(string operationId)
    {
        return new GatewayResponse<OperationAccepted>
        {
            Ok = true,
            Result = new OperationAccepted
            {
                OperationId = operationId,
            },
        };
    }

    private static GatewayResponse<
        OperationDetails<TResult>> Operation<TResult>(
            string operationId,
            OperationState state,
            TResult? result = default)
    {
        return new GatewayResponse<
            OperationDetails<TResult>>
        {
            Ok = true,
            Result = new OperationDetails<TResult>
            {
                Operation = new OperationSummary
                {
                    OperationId = operationId,
                    State = state,
                },
                Result = result,
            },
        };
    }

    private sealed class FakeGatewayClient
        : ITwinCatGatewayClient
    {
        public GatewayResponse<OperationAccepted>
            BuildAccepted { get; set; } =
                Accepted("build-1");

        public Queue<GatewayResponse<
            OperationDetails<BuildResult>>>
            BuildOperations { get; } = new();

        public GatewayResponse<OperationAccepted>
            SynchronizeAccepted { get; set; } =
                Accepted("sync-1");

        public Queue<GatewayResponse<
            OperationDetails<SynchronizeResult>>>
            SynchronizeOperations { get; } = new();

        public GatewayResponse<GatewayDiagnosticsResult>
            DiagnosticsResponse { get; set; } =
                new()
                {
                    Ok = true,
                    Result =
                        new GatewayDiagnosticsResult(),
                };

        public GatewayResponse<ResourceContent>
            ResourceResponse { get; set; } =
                new()
                {
                    Ok = true,
                    Result = new ResourceContent(),
                };

        public BuildParameters? Build { get; private set; }

        public SynchronizeParameters? Synchronize { get; private set; }

        public GetDiagnosticsParameters? Diagnostics
        {
            get;
            private set;
        }

        public int ResourceMaximumCharacters
        {
            get;
            private set;
        }

        public long ResourceOffset { get; private set; }

        public Task<GatewayResponse<GatewayStatusResult>>
            GetStatusAsync(
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new GatewayResponse<GatewayStatusResult>
                {
                    Ok = true,
                    Result = new GatewayStatusResult(),
                });
        }

        public Task<GatewayResponse<GatewayShutdownResult>>
            ShutdownAsync(
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new GatewayResponse<GatewayShutdownResult>
                {
                    Ok = true,
                    Result = new GatewayShutdownResult
                    {
                        ShutdownRequested = true,
                    },
                });
        }

        public Task<GatewayResponse<HealthResult>>
            GetHealthAsync(
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<GatewayDiagnosticsResult>>
            GetDiagnosticsAsync(
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<GatewayDiagnosticsResult>>
            GetDiagnosticsAsync(
                GetDiagnosticsParameters parameters,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Diagnostics = parameters;
            return Task.FromResult(DiagnosticsResponse);
        }

        public Task<GatewayResponse<OperationAccepted>>
            StartBuildAsync(
                BuildParameters parameters,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Build = parameters;
            return Task.FromResult(BuildAccepted);
        }

        public Task<GatewayResponse<OperationAccepted>>
            StartActivationAsync(
                ActivateParameters parameters,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<OperationAccepted>>
            StartSynchronizationAsync(
                SynchronizeParameters parameters,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Synchronize = parameters;
            return Task.FromResult(SynchronizeAccepted);
        }

        public Task<GatewayResponse<
            OperationDetails<TResult>>>
            GetOperationAsync<TResult>(
                string operationId,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object response =
                typeof(TResult) == typeof(SynchronizeResult)
                    ? SynchronizeOperations.Dequeue()
                    : BuildOperations.Dequeue();
            return Task.FromResult(
                (GatewayResponse<
                    OperationDetails<TResult>>)response);
        }

        public Task<GatewayResponse<
            OperationDetails<TestResult>>>
            GetTestResultsAsync(
                string operationId,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<CancelOperationResult>>
            CancelOperationAsync(
                string operationId,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GatewayResponse<ResourceContent>>
            GetResourceAsync(
                string uri,
                int maximumCharacters = 64 * 1024,
                long offset = 0,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResourceMaximumCharacters = maximumCharacters;
            ResourceOffset = offset;
            return Task.FromResult(ResourceResponse);
        }
    }
}
