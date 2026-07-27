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

[AttributeUsage(AttributeTargets.Method)]
public sealed class XaeLaunchFactAttribute : FactAttribute
{
    public XaeLaunchFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(
                    "TWINCAT_GATEWAY_XAE_SOLUTION"))
            || !string.Equals(
                Environment.GetEnvironmentVariable(
                    "TWINCAT_GATEWAY_ALLOW_XAE_LAUNCH"),
                "1",
                StringComparison.Ordinal))
        {
            Skip =
                "Requires TWINCAT_GATEWAY_XAE_SOLUTION and TWINCAT_GATEWAY_ALLOW_XAE_LAUNCH=1.";
        }
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class RemoteActivationFactAttribute : FactAttribute
{
    public RemoteActivationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(
                    "TWINCAT_GATEWAY_XAE_SOLUTION"))
            || !string.Equals(
                Environment.GetEnvironmentVariable(
                    "TWINCAT_GATEWAY_ALLOW_REMOTE_ACTIVATION"),
                "1",
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(
                    "TWINCAT_GATEWAY_REMOTE_AMS_NET_ID")))
        {
            Skip =
                "Requires TWINCAT_GATEWAY_XAE_SOLUTION, "
                + "TWINCAT_GATEWAY_ALLOW_REMOTE_ACTIVATION=1, and "
                + "TWINCAT_GATEWAY_REMOTE_AMS_NET_ID.";
        }
    }
}
