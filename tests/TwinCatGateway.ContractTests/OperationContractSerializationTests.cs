using System;
using System.Text.Json;
using TwinCatGateway.Contracts;
using Xunit;

namespace TwinCatGateway.ContractTests;

public sealed class OperationContractSerializationTests
{
    [Fact]
    public void OperationSummaryUsesStableCamelCaseValues()
    {
        OperationSummary operation = new()
        {
            OperationId = "operation-1",
            Kind = OperationKind.RecoverToConfig,
            State = OperationState.TimedOut,
            QueuedAtUtc = new DateTimeOffset(2026, 7, 27, 10, 0, 0, TimeSpan.Zero),
            StartedAtUtc = new DateTimeOffset(2026, 7, 27, 10, 0, 1, TimeSpan.Zero),
            CompletedAtUtc = new DateTimeOffset(2026, 7, 27, 10, 0, 6, TimeSpan.Zero),
            Error = new GatewayError
            {
                Code = ErrorCodes.ComCallTimeout,
                Message = "Timed out.",
            },
            Resources =
            {
                new ResourceReference
                {
                    Uri = "twincat-log://operation-1/xae",
                    OperationId = "operation-1",
                    Kind = ResourceKind.XaeLog,
                },
            },
        };

        string json = JsonSerializer.Serialize(operation, ContractJson.SerializerOptions);
        OperationSummary? result =
            JsonSerializer.Deserialize<OperationSummary>(json, ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal(OperationKind.RecoverToConfig, result.Kind);
        Assert.Equal(OperationState.TimedOut, result.State);
        Assert.Equal(ResourceKind.XaeLog, Assert.Single(result.Resources).Kind);
        Assert.Contains("\"kind\":\"recoverToConfig\"", json);
        Assert.Contains("\"state\":\"timedOut\"", json);
    }
}
