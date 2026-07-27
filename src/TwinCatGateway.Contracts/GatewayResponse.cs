namespace TwinCatGateway.Contracts;

public sealed class GatewayResponse<TResult>
{
    public int ProtocolVersion { get; set; } = Contracts.ProtocolVersion.Current;

    public string RequestId { get; set; } = string.Empty;

    public bool Ok { get; set; }

    public TResult? Result { get; set; }

    public GatewayError? Error { get; set; }
}
