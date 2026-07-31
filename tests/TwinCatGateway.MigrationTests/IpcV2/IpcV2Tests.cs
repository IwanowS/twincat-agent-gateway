using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Client;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Desktop;
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

    [Fact]
    public async Task PreflightFailureReceivesExactJournaledOperationId()
    {
        string logRoot = Path.Combine(
            Path.GetTempPath(),
            "twincat-gateway-s9-" + Guid.NewGuid().ToString("N"));
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
                new LocalLogStore(logRoot),
                journal);

            OperationHandle handle = service.EnqueuePreflightFailure(
                OperationKind.XaeBuild,
                "fixture",
                new GatewayOperationException(
                    ErrorCodes.CapabilityDisabled,
                    "Build capability is disabled.",
                    stage: "xae.build.admission",
                    component: GatewayComponent.Xae,
                    sideEffectsStarted: false));
            OperationResult<object> result =
                await service.WaitForOperationAsync<object>(
                    handle.OperationId,
                    CancellationToken.None);

            Assert.False(result.Ok);
            Assert.False(string.IsNullOrWhiteSpace(result.OperationId));
            Assert.Equal(result.OperationId, result.Error!.OperationId);
            Assert.Equal(
                result.OperationId,
                Assert.Single(
                    journal.ReadAfter(
                        journal.JournalId,
                        0,
                        10,
                        operationId: result.OperationId)
                    .Events,
                    gatewayEvent =>
                        gatewayEvent.Severity == DiagnosticSeverity.Error)
                .OperationId);
        }
        finally
        {
            if (Directory.Exists(logRoot))
            {
                Directory.Delete(logRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task NonWaitingMutationReturnsReceiptBeforeJournalCompletion()
    {
        string logRoot = Path.Combine(Path.GetTempPath(), "twincat-gateway-receipt-" + Guid.NewGuid().ToString("N"));
        try
        {
            OperationStore operations = new();
            GatewayEventJournal journal = new();
            using OperationQueue queue = new(operations, gatewayEventSink: journal);
            GatewayApplicationService service = new(
                "test",
                new GatewayStatusSnapshotStore(GatewayStatusSnapshotStore.CreateInitial("test")),
                operations,
                queue,
                new LocalLogStore(logRoot),
                journal);
            GatewayRequestDispatcher dispatcher = new(
                service,
                new CapabilityEvaluator(new GatewayConfiguration()));
            GatewayProtocolHandler handler = new(dispatcher.DispatchAsync);

            string json = await handler.HandleAsync(
                "{\"protocolVersion\":" + ProtocolVersion.Current
                    + ",\"requestId\":\"req-receipt\",\"method\":\"xaeBuild\","
                    + "\"params\":{\"profile\":\"missing\"},\"wait\":false}",
                CancellationToken.None);

            using JsonDocument document = JsonDocument.Parse(json);
            string operationId = document.RootElement
                .GetProperty("result")
                .GetProperty("operationId")
                .GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(operationId));
            Assert.NotNull(operations.Get(operationId));
        }
        finally
        {
            if (Directory.Exists(logRoot))
            {
                Directory.Delete(logRoot, recursive: true);
            }
        }
    }
}
