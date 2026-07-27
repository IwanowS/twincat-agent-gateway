using System;
using System.Collections.Generic;

namespace TwinCatGateway.Contracts;

public sealed class OperationSummary
{
    public string OperationId { get; set; } = string.Empty;

    public OperationKind Kind { get; set; }

    public OperationState State { get; set; }

    public DateTimeOffset QueuedAtUtc { get; set; }

    public DateTimeOffset? StartedAtUtc { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public GatewayError? Error { get; set; }

    public List<ResourceReference> Resources { get; set; } = new();
}
