using System.Text.Json;
using TwinCatGateway.Contracts;
using Xunit;

namespace TwinCatGateway.ContractTests;

public sealed class GatewayRequestSerializationTests
{
    [Fact]
    public void RequestRoundTripsWithoutLosingEnvelopeOrParameters()
    {
        GatewayRequest<ExampleParameters> request = new()
        {
            RequestId = "request-1",
            Method = "build",
            Params = new ExampleParameters
            {
                Profile = "default",
            },
            Wait = false,
        };

        string json = JsonSerializer.Serialize(request, ContractJson.SerializerOptions);
        GatewayRequest<ExampleParameters>? result =
            JsonSerializer.Deserialize<GatewayRequest<ExampleParameters>>(
                json,
                ContractJson.SerializerOptions);

        Assert.NotNull(result);
        Assert.Equal(request.ProtocolVersion, result.ProtocolVersion);
        Assert.Equal(request.RequestId, result.RequestId);
        Assert.Equal(request.Method, result.Method);
        Assert.Equal(request.Params.Profile, result.Params?.Profile);
        Assert.False(result.Wait);
        Assert.Contains("\"protocolVersion\":1", json);
        Assert.Contains("\"params\":", json);
    }

    private sealed class ExampleParameters
    {
        public string Profile { get; set; } = string.Empty;
    }
}
