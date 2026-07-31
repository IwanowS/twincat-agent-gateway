using System;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Ipc;

public sealed class GatewayDispatchResult
{
    private GatewayDispatchResult(
        bool ok,
        object? result,
        GatewayError? error,
        Action? afterResponseWritten)
    {
        Ok = ok;
        Result = result;
        Error = error;
        AfterResponseWritten = afterResponseWritten;
    }

    public bool Ok { get; }

    public object? Result { get; }

    public GatewayError? Error { get; }

    internal Action? AfterResponseWritten { get; }

    public static GatewayDispatchResult Success(
        object? result = null,
        Action? afterResponseWritten = null)
    {
        return new GatewayDispatchResult(
            true,
            result,
            null,
            afterResponseWritten);
    }

    public static GatewayDispatchResult Failure(GatewayError error)
    {
        return new GatewayDispatchResult(
            false,
            null,
            error ?? throw new ArgumentNullException(nameof(error)),
            afterResponseWritten: null);
    }
}
