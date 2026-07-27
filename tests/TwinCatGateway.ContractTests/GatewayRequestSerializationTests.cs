using System.Text.Json;
using TwinCatGateway.Contracts;
using Xunit;

namespace TwinCatGateway.ContractTests;

public sealed class GatewayRequestSerializationTests
{
    [Fact]
    public void RequestRoundTripsWithoutLosingEnvelopeFields()
    {
        GatewayRequest request = new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            RequestId = "request-1",
            Method = "status",
        };

        string json = JsonSerializer.Serialize(request);
        GatewayRequest? result = JsonSerializer.Deserialize<GatewayRequest>(json);

        Assert.NotNull(result);
        Assert.Equal(request.ProtocolVersion, result.ProtocolVersion);
        Assert.Equal(request.RequestId, result.RequestId);
        Assert.Equal(request.Method, result.Method);
    }
}
