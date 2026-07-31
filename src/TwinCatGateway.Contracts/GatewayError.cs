using System.Collections.Generic;

namespace TwinCatGateway.Contracts;

public sealed class GatewayError
{
    public string Code { get; set; } = ErrorCodes.GatewayNotReady;

    public string Message { get; set; } = string.Empty;

    public string? Details { get; set; }

    public bool Retryable { get; set; }

    public string? OperationId { get; set; }

    public string? Stage { get; set; }

    public string? RawLogRef { get; set; }

    public GatewayComponent? Component { get; set; }

    public bool? SideEffectsStarted { get; set; }

    public IdentityEvidence? Expected { get; set; }

    public IdentityEvidence? Observed { get; set; }

    public List<ResourceReference> Resources { get; set; } = new();
}
