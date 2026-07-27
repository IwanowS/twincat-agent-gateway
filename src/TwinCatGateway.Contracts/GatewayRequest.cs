namespace TwinCatGateway.Contracts;

public sealed class GatewayRequest<TParameters>
{
    public int ProtocolVersion { get; set; } = Contracts.ProtocolVersion.Current;

    public string RequestId { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public TParameters? Params { get; set; }

    public bool Wait { get; set; } = true;
}
