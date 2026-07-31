namespace TwinCatGateway.Contracts;

public sealed class GatewayStartResult
{
    public bool Started { get; set; }

    public bool AlreadyRunning { get; set; }

    public int? ProcessId { get; set; }

    public GatewayStateSnapshot Status { get; set; } = new();
}

public sealed class GatewayShutdownResult
{
    public bool ShutdownRequested { get; set; }
}
