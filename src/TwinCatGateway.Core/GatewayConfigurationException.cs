using System;

namespace TwinCatGateway.Core;

public sealed class GatewayConfigurationException : Exception
{
    public GatewayConfigurationException(
        string code,
        string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
