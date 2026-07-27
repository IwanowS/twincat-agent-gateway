using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class StoredOperation
{
    internal StoredOperation(OperationSummary summary, object? result)
    {
        Summary = summary;
        Result = result;
    }

    public OperationSummary Summary { get; }

    public object? Result { get; }
}
