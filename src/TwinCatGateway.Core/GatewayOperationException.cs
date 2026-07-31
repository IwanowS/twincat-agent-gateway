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
        Exception? innerException = null,
        GatewayComponent? component = null,
        bool? sideEffectsStarted = null,
        IdentityEvidence? expected = null,
        IdentityEvidence? observed = null)
        : base(message, innerException)
    {
        Code = code;
        Details = details;
        Retryable = retryable;
        Stage = stage;
        RawLogRef = rawLogRef;
        Component = component;
        SideEffectsStarted = sideEffectsStarted;
        Expected = expected;
        Observed = observed;
    }

    public string Code { get; }

    public string? Details { get; }

    public bool Retryable { get; }

    public string? Stage { get; }

    public string? RawLogRef { get; }

    public GatewayComponent? Component { get; }

    public bool? SideEffectsStarted { get; }

    public IdentityEvidence? Expected { get; }

    public IdentityEvidence? Observed { get; }
}
