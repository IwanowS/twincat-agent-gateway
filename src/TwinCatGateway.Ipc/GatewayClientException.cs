using System;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Ipc;

public enum GatewayClientFailureKind
{
    Transport,
    Protocol,
    Gateway,
}

public sealed class GatewayClientException : Exception
{
    public GatewayClientException(
        GatewayClientFailureKind kind,
        GatewayError error,
        Exception? innerException = null)
        : base(
            error?.Message ?? throw new ArgumentNullException(nameof(error)),
            innerException)
    {
        Kind = kind;
        Error = error;
    }

    public GatewayClientFailureKind Kind { get; }

    public GatewayError Error { get; }
}
