using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Client;
using TwinCatGateway.Contracts;
using TwinCatGateway.Ipc;
using Xunit;

namespace TwinCatGateway.IpcV2MigrationTests;

public sealed class IpcV2Tests
{
    [Fact]
    public async Task ProtocolSerializesTypedOperationWithoutV1EnvelopeFields()
    {
        GatewayProtocolHandler handler = new(
            (_, _) => Task.FromResult(
                GatewayDispatchResult.Success(
                    new OperationResult<XaeBuildResult>
                    {
                        Ok = true,
                        OperationId = "op-123",
                        Component = GatewayComponent.Xae,
                        Stage = "build",
                        Completion = OperationCompletion.Succeeded,
                        Result = new XaeBuildResult
                        {
                            Ok = true,
                            OperationId = "op-123",
                        },
                    })));

        string json = await handler.HandleAsync(
            "{\"protocolVersion\":" + ProtocolVersion.Current
                + ",\"requestId\":\"req-1\",\"method\":\"xaeBuild\","
                + "\"params\":{},\"wait\":true}",
            CancellationToken.None);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "op-123",
            root.GetProperty("result").GetProperty("operationId").GetString());
        Assert.False(root.TryGetProperty("runtimeAlert", out _));
    }

    [Fact]
    public void ClientSurfaceUsesObjectSpecificV2Results()
    {
        Assert.Equal(
            typeof(Task<OperationResult<XaeBuildResult>>),
            typeof(ITwinCatGatewayClient)
                .GetMethod(nameof(ITwinCatGatewayClient.BuildXaeAsync))!
                .ReturnType);
        Assert.Equal(
            typeof(Task<GatewayStateSnapshot>),
            typeof(ITwinCatGatewayClient)
                .GetMethod(nameof(ITwinCatGatewayClient.GetGatewayStateAsync))!
                .ReturnType);
    }

    [Fact]
    public void CloseRequestRetainsExactProfileIdentity()
    {
        string json = JsonSerializer.Serialize(
            new CloseXaeParameters
            {
                Profile = "fixture",
                SaveMode = XaeSaveMode.Prompt,
            },
            GatewayJson.CreateSerializerOptions());

        Assert.Contains("\"profile\":\"fixture\"", json);
    }
}
