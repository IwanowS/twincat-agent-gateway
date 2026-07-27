namespace TwinCatGateway.Contracts;

public enum GatewayState
{
    Starting,
    Disconnected,
    Attaching,
    OpeningSolution,
    Ready,
    Building,
    Activating,
    RecoveringToConfig,
    Faulted,
    Stopping,
}
