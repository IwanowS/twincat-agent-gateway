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
    [Fact]
    public void ToolSurfaceContainsOnlyExactV2NamesAndTypedSchemas()
    {
        var methods = typeof(TwinCatTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => new
            {
                Method = method,
                Tool = method.GetCustomAttribute<McpServerToolAttribute>(),
            })
            .Where(item => item.Tool is not null)
            .ToArray();

        Assert.Equal(
            GatewayMcpCatalog.Tools.Select(tool => tool.Name).OrderBy(name => name),
            methods.Select(item => item.Tool!.Name).OrderBy(name => name));
        foreach (McpToolDefinition expected in GatewayMcpCatalog.Tools)
        {
            var actual = Assert.Single(methods, item => item.Tool!.Name == expected.Name);
            Assert.True(actual.Tool!.UseStructuredContent);
            Assert.Equal(expected.OutputSchemaType, actual.Tool.OutputSchemaType);
            Assert.Equal(expected.ReadOnly, actual.Tool.ReadOnly);
            Assert.Equal(expected.Destructive, actual.Tool.Destructive);
            Assert.Equal(expected.Idempotent, actual.Tool.Idempotent);
            Assert.Equal(expected.OpenWorld, actual.Tool.OpenWorld);
            Assert.Equal(
                expected.Description,
                actual.Method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description);
        }
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
