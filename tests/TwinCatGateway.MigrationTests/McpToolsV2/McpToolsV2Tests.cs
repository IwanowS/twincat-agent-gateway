using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TwinCatGateway.Contracts;
using TwinCatGateway.Mcp;
using Xunit;

namespace TwinCatGateway.McpToolsV2MigrationTests;

public sealed class McpToolsV2Tests
{
    private static readonly string[] ExpectedTools =
    {
        "gateway_shutdown",
        "gateway_start",
        "twincat_target_config",
        "twincat_target_start_restart",
        "twincat_xae_activate",
        "twincat_xae_build",
        "twincat_xae_close",
        "twincat_xae_open",
        "twincat_xae_sync",
    };

    [Fact]
    public void ToolSurfaceContainsOnlyExactV2NamesAndTypedSchemas()
    {
        McpServerToolAttribute[] tools = typeof(TwinCatTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attribute => attribute is not null)
            .Cast<McpServerToolAttribute>()
            .ToArray();

        Assert.Equal(
            ExpectedTools,
            tools.Select(tool => tool.Name).OrderBy(name => name).ToArray());
        Assert.All(tools, tool => Assert.True(tool.UseStructuredContent));
        Assert.All(tools, tool => Assert.NotNull(tool.OutputSchemaType));
    }

    [Fact]
    public void GatewayLifecycleSchemasDoNotExposeOperationId()
    {
        foreach (Type resultType in new[]
        {
            typeof(GatewayLifecycleResult<GatewayStartResult>),
            typeof(GatewayLifecycleResult<GatewayShutdownResult>),
        })
        {
            Assert.Null(resultType.GetProperty("OperationId"));
        }
    }

    [Fact]
    public void OperationToolResultUsesStructuredContentAndResourceLinkBlocks()
    {
        OperationResult<XaeBuildResult> value = new()
        {
            Ok = true,
            OperationId = "op-123",
            Component = GatewayComponent.Xae,
            Stage = "xae.build",
            Completion = OperationCompletion.Succeeded,
            Resources = new List<ResourceReference>
            {
                new()
                {
                    Uri = "twincat-operation://op-123/build",
                    MimeType = "text/plain",
                },
            },
        };
        MethodInfo createResult = typeof(TwinCatTools)
            .GetMethod("CreateResult", BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(OperationResult<XaeBuildResult>));

        CallToolResult result = (CallToolResult)createResult.Invoke(
            null,
            new object?[] { value, false, value.Resources })!;

        Assert.Equal(
            "op-123",
            result.StructuredContent!.Value.GetProperty("operationId").GetString());
        ResourceLinkBlock link = Assert.Single(result.Content.OfType<ResourceLinkBlock>());
        Assert.Equal("twincat-operation://op-123/build", link.Uri);
        Assert.Equal("text/plain", link.MimeType);
        Assert.False(result.IsError);
    }
}
