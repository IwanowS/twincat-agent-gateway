namespace TwinCatGateway.Contracts;

public enum OperationState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    TimedOut,
    Cancelled,
}
