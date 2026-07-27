using System;
using System.Diagnostics;

namespace TwinCatGateway.Core;

public interface IOperationExceptionSink
{
    void Record(string operationId, Exception exception);
}

public sealed class TraceOperationExceptionSink : IOperationExceptionSink
{
    public static TraceOperationExceptionSink Instance { get; } = new();

    private TraceOperationExceptionSink()
    {
    }

    public void Record(string operationId, Exception exception)
    {
        Trace.TraceError(
            "Operation '{0}' failed: {1}",
            operationId,
            exception);
    }
}
