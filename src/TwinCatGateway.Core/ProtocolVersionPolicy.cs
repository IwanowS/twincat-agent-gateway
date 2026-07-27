using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public static class ProtocolVersionPolicy
{
    public static bool IsSupported(int protocolVersion)
    {
        return protocolVersion == ProtocolVersion.Current;
    }
}
