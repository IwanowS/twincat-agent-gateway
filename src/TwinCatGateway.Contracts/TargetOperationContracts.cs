using System.Collections.Generic;

namespace TwinCatGateway.Contracts;

public enum TargetTransitionAction
{
    NoOp,
    Config,
}

public sealed class TargetConfigParameters
{
    public string Profile { get; set; } = string.Empty;
}

public sealed class TargetFaultSnapshot
{
    public TargetSystemObservation Target { get; set; } = new();

    public XaeMessagesResult? XaeMessages { get; set; }

    public List<OperationDiagnostic> Diagnostics { get; set; } = new();
}

public sealed class TargetConfigResult
{
    public bool Ok { get; set; }

    public string OperationId { get; set; } = string.Empty;

    public long DurationMs { get; set; }

    public string Profile { get; set; } = string.Empty;

    public TargetIdentity Target { get; set; } = new();

    public TargetTransitionAction Action { get; set; }

    public TargetSystemObservation Before { get; set; } = new();

    public TargetSystemObservation After { get; set; } = new();

    public TargetFaultSnapshot? FaultSnapshot { get; set; }
}
