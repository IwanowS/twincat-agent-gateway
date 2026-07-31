using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class OperationHandle
{
    public string OperationId { get; set; } = string.Empty;

    public OperationState State { get; set; } = OperationState.Queued;
}
