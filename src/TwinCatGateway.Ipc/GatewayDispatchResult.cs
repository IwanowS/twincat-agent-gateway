using System;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Ipc;

public sealed class GatewayDispatchResult
{
    private GatewayDispatchResult(
        bool ok,
        object? result,
        GatewayError? error,
        RuntimeAlert? runtimeAlert,
        Action? afterResponseWritten)
    {
        Ok = ok;
        Result = result;
        Error = error;
        RuntimeAlert = runtimeAlert;
        AfterResponseWritten = afterResponseWritten;
    }

    public bool Ok { get; }

    public object? Result { get; }

    public GatewayError? Error { get; }

    public RuntimeAlert? RuntimeAlert { get; }

    internal Action? AfterResponseWritten { get; }

    public static GatewayDispatchResult Success(
        object? result = null,
        RuntimeAlert? runtimeAlert = null,
        Action? afterResponseWritten = null)
    {
        return new GatewayDispatchResult(
            true,
            result,
            null,
            runtimeAlert,
            afterResponseWritten);
    }

    public static GatewayDispatchResult Success(
        object? result,
        Action afterResponseWritten)
    {
        return Success(
            result,
            runtimeAlert: null,
            afterResponseWritten);
    }

    public static GatewayDispatchResult Failure(
        GatewayError error,
        RuntimeAlert? runtimeAlert = null)
    {
        return new GatewayDispatchResult(
            false,
            null,
            error ?? throw new ArgumentNullException(nameof(error)),
            runtimeAlert,
            afterResponseWritten: null);
    }
}
