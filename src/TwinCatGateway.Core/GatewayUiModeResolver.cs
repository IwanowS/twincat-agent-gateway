using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public static class GatewayUiModeResolver
{
    public static GatewayUiMode Resolve(
        GatewayUiMode configuredMode,
        GatewayLaunchSource launchSource)
    {
        if (configuredMode != GatewayUiMode.Auto)
        {
            return configuredMode;
        }

        return launchSource == GatewayLaunchSource.Agent
            ? GatewayUiMode.Tray
            : GatewayUiMode.Window;
    }
}
