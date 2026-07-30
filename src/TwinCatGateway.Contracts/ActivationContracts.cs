using System.Collections.Generic;

namespace TwinCatGateway.Contracts;

public enum AutostartBootProjectSelection
{
    Unknown,
    Disabled,
    Enabled,
    PartiallyEnabled,
}

public enum ActivationCompletion
{
    Unknown,
    AppliedAndRunning,
    RestartSkipped,
}

public sealed class ActivateParameters
{
    public string Profile { get; set; } = string.Empty;

    public bool RunAfterActivation { get; set; } = true;

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

    public bool RunAfterActivation { get; set; }

    public ActivationCompletion Completion { get; set; }

    public bool ActiveConfigurationVerified { get; set; }

    public RuntimeMode ObservedRuntimeMode { get; set; }

    public AutostartBootProjectSelection AutostartBootProjects { get; set; }

    public ActivationCompileResult? Compile { get; set; }

    public string? TestOperationId { get; set; }

    public List<ResourceReference> Resources { get; set; } = new();
}

public sealed class ActivationCompileResult
{
    public bool Completed { get; set; }

    public bool Ok { get; set; }

    public long DurationMs { get; set; }

    public int FailedProjects { get; set; }

    public DiagnosticCounts Counts { get; set; } = new();

    public List<BuildDiagnostic> Diagnostics { get; set; } = new();

    public int MoreDiagnostics { get; set; }

    public ResourceReference? Log { get; set; }
}

public sealed class ActivationSummary
{
    public bool Ok { get; set; }

    public string OperationId { get; set; } = string.Empty;

    public string Profile { get; set; } = string.Empty;

    public TargetIdentity Target { get; set; } = new();
}
