using System;
using System.Collections.Generic;

namespace TwinCatGateway.Contracts;

public enum OperationCompletion
{
    Succeeded,
    Failed,
    TimedOut,
    Cancelled,
}

public sealed class IdentityEvidence
{
    public string? Profile { get; set; }

    public string? Solution { get; set; }

    public string? AmsNetId { get; set; }

    public int? Port { get; set; }

    public int? ProcessId { get; set; }

    public string? RuntimeId { get; set; }
}

public sealed class OperationDiagnostic
{
    public string Code { get; set; } = string.Empty;

    public GatewayComponent Component { get; set; }

    public string Stage { get; set; } = string.Empty;

    public DiagnosticSeverity Severity { get; set; }

    public string Message { get; set; } = string.Empty;

    public DateTimeOffset? OccurredAtUtc { get; set; }

    public List<ResourceReference> Resources { get; set; } = new();
}

public sealed class OperationStageResult<TResult>
{
    public string OperationId { get; set; } = string.Empty;

    public GatewayComponent Component { get; set; }

    public string Stage { get; set; } = string.Empty;

    public OperationCompletion Completion { get; set; }

    public bool SideEffectsStarted { get; set; }

    public TResult? Result { get; set; }

    public GatewayError? Error { get; set; }

    public List<OperationDiagnostic> Diagnostics { get; set; } = new();

    public List<ResourceReference> Resources { get; set; } = new();
}

public sealed class OperationResult<TResult>
{
    public bool Ok { get; set; }

    public string OperationId { get; set; } = string.Empty;

    public GatewayComponent Component { get; set; }

    public string Stage { get; set; } = string.Empty;

    public OperationCompletion Completion { get; set; }

    public bool SideEffectsStarted { get; set; }

    public TResult? Result { get; set; }

    public GatewayError? Error { get; set; }

    public List<OperationDiagnostic> Diagnostics { get; set; } = new();

    public List<ResourceReference> Resources { get; set; } = new();
}
