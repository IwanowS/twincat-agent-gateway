using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class StoredOperation
{
    internal StoredOperation(OperationRecord summary, object? result)
    {
        Summary = summary;
        Result = result;
    }

    public OperationRecord Summary { get; }

    public object? Result { get; }
}
