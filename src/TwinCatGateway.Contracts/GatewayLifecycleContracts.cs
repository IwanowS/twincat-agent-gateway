namespace TwinCatGateway.Contracts;

public sealed class GatewayStartResult
{
    public bool Started { get; set; }

    public bool AlreadyRunning { get; set; }

    public int? ProcessId { get; set; }

    public GatewayStatusResult Status { get; set; } = new();
}
