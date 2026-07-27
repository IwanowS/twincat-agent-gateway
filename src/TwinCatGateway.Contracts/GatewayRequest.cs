namespace TwinCatGateway.Contracts;

public sealed class GatewayRequest
{
    public int ProtocolVersion { get; set; }

    public string RequestId { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;
}
