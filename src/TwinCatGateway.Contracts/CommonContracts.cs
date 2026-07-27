using System;
using System.Collections.Generic;

namespace TwinCatGateway.Contracts;

public enum DetailLevel
{
    Compact,
    Detailed,
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public enum RuntimeMode
{
    Unknown,
    Config,
    Run,
    Stop,
}

public sealed class TargetIdentity
{
    public string? Name { get; set; }

    public string? AmsNetId { get; set; }
}

public sealed class OperationTimelineEntry
{
    public DateTimeOffset TimestampUtc { get; set; }

    public string Stage { get; set; } = string.Empty;

    public DiagnosticSeverity Severity { get; set; }

    public string Message { get; set; } = string.Empty;
}

public sealed class ComponentHealth
{
    public bool Healthy { get; set; }

    public string? Message { get; set; }

    public List<ResourceReference> Resources { get; set; } = new();
}
