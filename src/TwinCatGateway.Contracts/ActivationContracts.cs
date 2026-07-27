using System.Collections.Generic;

namespace TwinCatGateway.Contracts;

public sealed class ActivateParameters
{
    public string Profile { get; set; } = string.Empty;

    public bool? WaitForTcUnit { get; set; }

    public int? TimeoutSeconds { get; set; }
}

public sealed class ActivationResult
{
    public bool Ok { get; set; }

    public string OperationId { get; set; } = string.Empty;

    public long DurationMs { get; set; }

    public string Profile { get; set; } = string.Empty;

    public string Solution { get; set; } = string.Empty;

    public TargetIdentity Target { get; set; } = new();

    public bool RecoveryAttempted { get; set; }

    public string? TestOperationId { get; set; }

    public List<ResourceReference> Resources { get; set; } = new();
}

public sealed class ActivationSummary
{
    public bool Ok { get; set; }

    public string OperationId { get; set; } = string.Empty;

    public string Profile { get; set; } = string.Empty;

    public TargetIdentity Target { get; set; } = new();
}
