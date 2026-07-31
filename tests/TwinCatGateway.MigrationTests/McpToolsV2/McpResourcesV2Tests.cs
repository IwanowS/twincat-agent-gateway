using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ModelContextProtocol.Server;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Mcp;
using Xunit;

namespace TwinCatGateway.McpToolsV2MigrationTests;

public sealed class McpResourcesV2Tests
{
    private static readonly string[] ExpectedTemplates =
    {
        "twincat-doc://configuration",
        "twincat-doc://mcp",
        "twincat-doc://setup",
        "twincat-gateway://diagnostics",
        "twincat-gateway://state",
        "twincat-log://gateway/current",
        "twincat-operation://{operationId}",
        "twincat-operation://{operationId}/build",
        "twincat-operation://{operationId}/events",
        "twincat-operation://{operationId}/project-noise",
        "twincat-operation://{operationId}/test/xunit",
        "twincat-operation://{operationId}/xae-messages",
        "twincat-plc://profile/{profile}/{runtime}/diagnostics",
        "twincat-plc://profile/{profile}/{runtime}/state",
        "twincat-profile://{profile}/capabilities",
        "twincat-profile://{profile}/sources",
        "twincat-profile://{profile}/sources/files",
        "twincat-target://profile/{profile}/diagnostics",
        "twincat-target://profile/{profile}/state",
        "twincat-xae://profile/{profile}/diagnostics",
        "twincat-xae://profile/{profile}/messages/current",
        "twincat-xae://profile/{profile}/state",
    };

    [Fact]
    public void ResourceSurfaceListsEveryCanonicalV2Template()
    {
        string[] templates = typeof(TwinCatResources)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.GetCustomAttribute<McpServerResourceAttribute>())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.UriTemplate!)
            .OrderBy(uri => uri, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedTemplates, templates);
    }

    [Theory]
    [InlineData("twincat-gateway://state")]
    [InlineData("twincat-profile://fixture/sources/files")]
    [InlineData("twincat-xae://profile/fixture/messages/current")]
    [InlineData("twincat-plc://profile/fixture/plc1/diagnostics")]
    [InlineData("twincat-operation://abc/test/xunit")]
    public void RouterAcceptsCanonicalUris(string uri)
    {
        Assert.Equal(uri, GatewayResourceRouter.Parse(uri).CanonicalUri);
    }

    [Theory]
    [InlineData("twincat-operation://abc/latest")]
    [InlineData("twincat-operation://abc/build?offset=1")]
    [InlineData("twincat-operation://abc/build#fragment")]
    [InlineData("twincat-profile://fixture/%2e%2e/state")]
    [InlineData("twincat-profile://fixture%2Fsibling/sources")]
    [InlineData("twincat-profile://fixture/%ZZ")]
    [InlineData("twincat-log://abc/build")]
    public void RouterRejectsNonCanonicalOrUnknownUris(string uri)
    {
        Assert.Throws<ArgumentException>(() => GatewayResourceRouter.Parse(uri));
    }

    [Fact]
    public void MissingOperationAndArtifactReturnResourceNotFound()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "twincat-gateway-resource-" + Guid.NewGuid().ToString("N"));
        try
        {
            OperationStore operations = new();
            GatewayEventJournal journal = new();
            using OperationQueue queue = new(operations, gatewayEventSink: journal);
            GatewayApplicationService service = new(
                "test",
                new GatewayStatusSnapshotStore(
                    GatewayStatusSnapshotStore.CreateInitial("test")),
                operations,
                queue,
                new LocalLogStore(root),
                journal);

            GatewayOperationException missingOperation = Assert.Throws<GatewayOperationException>(
                () => service.GetResource("twincat-operation://abc", 4096, 0));
            GatewayOperationException missingArtifact = Assert.Throws<GatewayOperationException>(
                () => service.GetResource("twincat-operation://abc/build", 4096, 0));

            Assert.Equal(ErrorCodes.ResourceNotFound, missingOperation.Code);
            Assert.Equal(ErrorCodes.ResourceNotFound, missingArtifact.Code);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
