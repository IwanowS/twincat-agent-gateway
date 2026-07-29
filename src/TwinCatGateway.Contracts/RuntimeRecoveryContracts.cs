namespace TwinCatGateway.Contracts;

public sealed class RecoverToConfigParameters
{
    public string Profile { get; set; } = string.Empty;

    public int? TimeoutSeconds { get; set; }
}

public sealed class RecoverToConfigResult
{
    public bool Ok { get; set; }

    public string OperationId { get; set; } = string.Empty;

    public long DurationMs { get; set; }

    public string Profile { get; set; } = string.Empty;

    public string Solution { get; set; } = string.Empty;

    public TargetIdentity Target { get; set; } = new();

    public RuntimeMode InitialRuntimeMode { get; set; }

    public RuntimeMode ObservedRuntimeMode { get; set; }

    public bool TransitionRequested { get; set; }
}
