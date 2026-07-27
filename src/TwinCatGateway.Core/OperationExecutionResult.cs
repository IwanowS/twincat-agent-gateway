using System;
using System.Collections.Generic;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class OperationExecutionResult
{
    private OperationExecutionResult(
        bool succeeded,
        object? result,
        GatewayError? error,
        IReadOnlyList<ResourceReference> resources)
    {
        Succeeded = succeeded;
        Result = result;
        Error = error;
        Resources = resources;
    }

    public bool Succeeded { get; }

    public object? Result { get; }

    public GatewayError? Error { get; }

    public IReadOnlyList<ResourceReference> Resources { get; }

    public static OperationExecutionResult Success(
        object? result = null,
        IReadOnlyList<ResourceReference>? resources = null)
    {
        return new OperationExecutionResult(
            true,
            result,
            null,
            resources ?? Array.Empty<ResourceReference>());
    }

    public static OperationExecutionResult Failure(
        GatewayError error,
        object? result = null,
        IReadOnlyList<ResourceReference>? resources = null)
    {
        if (error is null)
        {
            throw new ArgumentNullException(nameof(error));
        }

        return new OperationExecutionResult(
            false,
            result,
            error,
            resources ?? Array.Empty<ResourceReference>());
    }
}
