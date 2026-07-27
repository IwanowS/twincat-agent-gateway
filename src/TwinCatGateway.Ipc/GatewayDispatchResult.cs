using System;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Ipc;

public sealed class GatewayDispatchResult
{
    private GatewayDispatchResult(bool ok, object? result, GatewayError? error)
    {
        Ok = ok;
        Result = result;
        Error = error;
    }

    public bool Ok { get; }

    public object? Result { get; }

    public GatewayError? Error { get; }

    public static GatewayDispatchResult Success(object? result = null)
    {
        return new GatewayDispatchResult(true, result, null);
    }

    public static GatewayDispatchResult Failure(GatewayError error)
    {
        return new GatewayDispatchResult(
            false,
            null,
            error ?? throw new ArgumentNullException(nameof(error)));
    }
}
