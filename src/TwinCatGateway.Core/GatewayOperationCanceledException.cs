using System;
using System.Threading;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class GatewayOperationCanceledException
    : OperationCanceledException
{
    public GatewayOperationCanceledException(
        string code,
        string message,
        string stage,
        GatewayComponent component,
        bool sideEffectsStarted,
        OperationCanceledException innerException)
        : base(message, innerException, innerException.CancellationToken)
    {
        Code = code;
        Stage = stage;
        Component = component;
        SideEffectsStarted = sideEffectsStarted;
    }

    public string Code { get; }

    public string Stage { get; }

    public GatewayComponent Component { get; }

    public bool SideEffectsStarted { get; }
}
