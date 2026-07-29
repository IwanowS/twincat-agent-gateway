using System;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class GatewayOperationException : Exception
{
    public GatewayOperationException(
        string code,
        string message,
        string? details = null,
        bool retryable = false,
        string? stage = null,
        string? rawLogRef = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Details = details;
        Retryable = retryable;
        Stage = stage;
        RawLogRef = rawLogRef;
    }

    public string Code { get; }

    public string? Details { get; }

    public bool Retryable { get; }

    public string? Stage { get; }

    public string? RawLogRef { get; }
}
