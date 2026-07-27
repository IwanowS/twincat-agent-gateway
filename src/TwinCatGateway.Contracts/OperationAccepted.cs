namespace TwinCatGateway.Contracts;

public sealed class OperationAccepted
{
    public string OperationId { get; set; } = string.Empty;

    public OperationState State { get; set; } = OperationState.Queued;
}
