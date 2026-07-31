using TwinCatGateway.Contracts;
using Xunit;

namespace TwinCatGateway.ContractTests;

public sealed class GatewayResourceUrisTests
{
    [Theory]
    [InlineData("bench")]
    [InlineData("bench profile")]
    [InlineData("кириллица")]
    public void ProfileSourceUrisRoundTrip(string profile)
    {
        string manifest = GatewayResourceUris.ProfileSources(profile);
        string files = GatewayResourceUris.ProfileSourceFiles(profile);

        Assert.True(
            GatewayResourceUris.TryParseProfileSources(
                manifest,
                out string manifestProfile,
                out bool manifestFiles));
        Assert.Equal(profile, manifestProfile);
        Assert.False(manifestFiles);
        Assert.True(
            GatewayResourceUris.TryParseProfileSources(
                files,
                out string filesProfile,
                out bool isFiles));
        Assert.Equal(profile, filesProfile);
        Assert.True(isFiles);
    }

    [Theory]
    [InlineData("")]
    [InlineData("twincat-profile://bench")]
    [InlineData("twincat-profile:///sources")]
    [InlineData("twincat-profile://bench/other")]
    [InlineData("twincat-profile://bench/sources/")]
    [InlineData("twincat-profile://bench%2fsibling/sources")]
    public void InvalidProfileSourceUrisAreRejected(string uri)
    {
        Assert.False(
            GatewayResourceUris.TryParseProfileSources(
                uri,
                out _,
                out _));
    }
}
