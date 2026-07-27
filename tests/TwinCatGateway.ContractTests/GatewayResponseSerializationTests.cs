using System.Text.Json;
using TwinCatGateway.Contracts;
using Xunit;

namespace TwinCatGateway.ContractTests;

public sealed class GatewayResponseSerializationTests
{
    [Fact]
    public void SuccessfulResponseRoundTrips()
    {
        GatewayResponse<ExampleResult> response = new()
        {
            RequestId = "request-1",
            Ok = true,
            Result = new ExampleResult
            {
                State = "ready",
            },
        };

        string json = JsonSerializer.Serialize(response, ContractJson.SerializerOptions);
        GatewayResponse<ExampleResult>? result =
            JsonSerializer.Deserialize<GatewayResponse<ExampleResult>>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal(ProtocolVersion.Current, result.ProtocolVersion);
        Assert.Equal(response.RequestId, result.RequestId);
        Assert.True(result.Ok);
        Assert.Equal("ready", result.Result?.State);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ErrorResponsePreservesStableDiagnosticFields()
    {
        GatewayResponse<EmptyParameters> response = new()
        {
            RequestId = "request-2",
            Ok = false,
            Error = new GatewayError
            {
                Code = ErrorCodes.ComCallTimeout,
                Message = "XAE did not accept the call before the deadline.",
                Retryable = true,
                OperationId = "operation-1",
                Stage = "build.wait",
                RawLogRef = "twincat-log://operation-1/xae",
            },
        };

        string json = JsonSerializer.Serialize(response, ContractJson.SerializerOptions);
        GatewayResponse<EmptyParameters>? result =
            JsonSerializer.Deserialize<GatewayResponse<EmptyParameters>>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.False(result.Ok);
        Assert.Null(result.Result);
        Assert.Equal(ErrorCodes.ComCallTimeout, result.Error?.Code);
        Assert.True(result.Error?.Retryable);
        Assert.Equal("operation-1", result.Error?.OperationId);
        Assert.Equal("build.wait", result.Error?.Stage);
        Assert.Equal("twincat-log://operation-1/xae", result.Error?.RawLogRef);
    }

    private sealed class ExampleResult
    {
        public string State { get; set; } = string.Empty;
    }
}
