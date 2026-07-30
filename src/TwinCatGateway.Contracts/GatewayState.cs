namespace TwinCatGateway.Contracts;

public enum GatewayState
{
    Starting,
    Disconnected,
    Attaching,
    OpeningSolution,
    Ready,
    Building,
    ClosingXae,
    Activating,
    RecoveringToConfig,
    Testing,
    Faulted,
    Stopping,
}
