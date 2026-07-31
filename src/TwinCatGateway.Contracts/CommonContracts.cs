namespace TwinCatGateway.Contracts;

public enum DetailLevel
{
    Compact,
    Full,
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed class TargetIdentity
{
    public string? Name { get; set; }

    public string? AmsNetId { get; set; }
}
