namespace TwinCatGateway.Contracts;

public sealed class GatewayLifecycleResult<TResult>
{
    public bool Ok { get; set; }

    public TResult? Result { get; set; }

    public GatewayError? Error { get; set; }
}

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
