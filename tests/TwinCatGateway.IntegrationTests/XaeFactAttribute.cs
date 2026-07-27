using System;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class XaeFactAttribute : FactAttribute
{
    public XaeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_XAE_SOLUTION")))
        {
            Skip =
                "Requires TWINCAT_GATEWAY_XAE_SOLUTION and a running TwinCAT 3.1.4024.17 XAE instance.";
        }
    }
}
