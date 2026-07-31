using System;

namespace TwinCatGateway.Core;

public sealed class OperationCancellationService
{
    private readonly OperationQueue _queue;

    public OperationCancellationService(OperationQueue queue)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public OperationCancellationResult Cancel(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new ArgumentException(
                "Operation ID is required.",
                nameof(operationId));
        }

        return _queue.Cancel(operationId);
    }
}
