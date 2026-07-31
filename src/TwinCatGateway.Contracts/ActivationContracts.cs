using System.Collections.Generic;

namespace TwinCatGateway.Contracts;

public enum AutostartBootProjectSelection
{
    Unknown,
    Disabled,
    Enabled,
    PartiallyEnabled,
}

public enum ActivationFinalTargetMode
{
    Run,
    Unchanged,
}

public enum VerificationMode
{
    None,
    TcUnit,
}

public sealed class ActivateParameters
{
    public string Profile { get; set; } = string.Empty;

    public ActivationFinalTargetMode FinalTargetMode { get; set; } =
        ActivationFinalTargetMode.Run;

    public VerificationMode Verification { get; set; } =
        VerificationMode.None;

    public List<string> ChangedPaths { get; set; } = new();

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

    public OperationStageResult<SynchronizeResult> Sync { get; set; } = new();

    public OperationStageResult<ActivationCompileResult> Compile { get; set; } =
        new();

    public OperationStageResult<ActivationDeployResult> Deploy { get; set; } =
        new();

    public OperationStageResult<ActivationTargetTransitionResult>
        TargetTransition { get; set; } = new();

    public OperationStageResult<TestResult> Verification { get; set; } = new();

    public List<ResourceReference> Resources { get; set; } = new();
}

public sealed class ActivationCompileResult
{
    public bool Ok { get; set; }

    public long DurationMs { get; set; }

    public int FailedProjects { get; set; }

    public DiagnosticCounts Counts { get; set; } = new();

    public List<BuildDiagnostic> Diagnostics { get; set; } = new();

    public int MoreDiagnostics { get; set; }

    public ResourceReference? Log { get; set; }
}

public sealed class ActivationDeployResult
{
    public bool ConfigurationStored { get; set; }

    public bool PhysicalActivationVerified { get; set; }

    public AutostartBootProjectSelection AutostartBootProjects { get; set; }
}

public sealed class ActivationTargetTransitionResult
{
    public ActivationFinalTargetMode RequestedMode { get; set; }

    public TargetSystemObservation? Observation { get; set; }
}

public sealed class ActivationSummary
{
    public bool Ok { get; set; }

    public string OperationId { get; set; } = string.Empty;

    public string Profile { get; set; } = string.Empty;

    public TargetIdentity Target { get; set; } = new();
}
