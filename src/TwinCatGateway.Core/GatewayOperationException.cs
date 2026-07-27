using System;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class GatewayOperationException : Exception
{
    public GatewayOperationException(
        string code,
        string message,
        bool retryable = false,
        string? stage = null,
        string? rawLogRef = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Retryable = retryable;
        Stage = stage;
        RawLogRef = rawLogRef;
    }

    public string Code { get; }

    public bool Retryable { get; }

    public string? Stage { get; }

    public string? RawLogRef { get; }
}
