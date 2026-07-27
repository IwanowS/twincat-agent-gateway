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
    Exception,
}

public sealed class TargetIdentity
{
    public string? Name { get; set; }

    public string? AmsNetId { get; set; }
}

public sealed class ComponentHealth
{
    public bool Healthy { get; set; }

    public string? Message { get; set; }

    public List<ResourceReference> Resources { get; set; } = new();
}
