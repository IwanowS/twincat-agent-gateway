using System;
using System.Collections.Generic;

namespace TwinCatGateway.Contracts;

public sealed class GatewayEvent
{
    public long Cursor { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    public string Type { get; set; } = string.Empty;

    public DiagnosticSeverity Severity { get; set; }

    public string? OperationId { get; set; }

    public OperationKind? OperationKind { get; set; }

    public string? Stage { get; set; }

    public string Message { get; set; } = string.Empty;

    public GatewayError? Error { get; set; }

    public List<ResourceReference> Resources { get; set; } = new();

    public Dictionary<string, string> Properties { get; set; } = new();
}
