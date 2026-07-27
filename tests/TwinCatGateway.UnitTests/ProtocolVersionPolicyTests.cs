using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class ProtocolVersionPolicyTests
{
    [Fact]
    public void CurrentVersionIsSupported()
    {
        Assert.True(ProtocolVersionPolicy.IsSupported(ProtocolVersion.Current));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void UnknownVersionIsRejected(int protocolVersion)
    {
        Assert.False(ProtocolVersionPolicy.IsSupported(protocolVersion));
    }
}
