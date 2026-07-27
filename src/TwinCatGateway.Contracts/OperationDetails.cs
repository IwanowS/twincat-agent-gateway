namespace TwinCatGateway.Contracts;

public sealed class OperationDetails<TResult>
{
    public OperationSummary Operation { get; set; } = new();

    public TResult? Result { get; set; }
}

public sealed class CancelOperationResult
{
    public string OperationId { get; set; } = string.Empty;

    public bool Cancelled { get; set; }

    public OperationState State { get; set; }
}
