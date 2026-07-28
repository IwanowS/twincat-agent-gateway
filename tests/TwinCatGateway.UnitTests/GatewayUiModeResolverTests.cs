using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class GatewayUiModeResolverTests
{
    [Theory]
    [InlineData(
        GatewayUiMode.Auto,
        GatewayLaunchSource.Manual,
        GatewayUiMode.Window)]
    [InlineData(
        GatewayUiMode.Auto,
        GatewayLaunchSource.Agent,
        GatewayUiMode.Tray)]
    [InlineData(
        GatewayUiMode.Window,
        GatewayLaunchSource.Agent,
        GatewayUiMode.Window)]
    [InlineData(
        GatewayUiMode.Tray,
        GatewayLaunchSource.Manual,
        GatewayUiMode.Tray)]
    public void ResolveReturnsEffectivePresentation(
        GatewayUiMode configuredMode,
        GatewayLaunchSource launchSource,
        GatewayUiMode expected)
    {
        Assert.Equal(
            expected,
            GatewayUiModeResolver.Resolve(
                configuredMode,
                launchSource));
    }
}
