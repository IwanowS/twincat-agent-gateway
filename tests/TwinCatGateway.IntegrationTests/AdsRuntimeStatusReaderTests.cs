using System;
using TwinCAT.Ads;
using TwinCatGateway.Ads;
using TwinCatGateway.Contracts;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class AdsRuntimeStatusReaderTests
{
    [Theory]
    [InlineData(AdsState.Run, true, RuntimeMode.Run)]
    [InlineData(AdsState.Config, false, RuntimeMode.Config)]
    [InlineData(AdsState.Reconfig, false, RuntimeMode.Config)]
    [InlineData(AdsState.Stop, false, RuntimeMode.Stop)]
    [InlineData(AdsState.Exception, true, RuntimeMode.Exception)]
    [InlineData(AdsState.Error, true, RuntimeMode.Exception)]
    [InlineData(AdsState.Init, null, RuntimeMode.Unknown)]
    public void MapsAdsStateWithoutInferringTransitions(
        AdsState adsState,
        bool? started,
        RuntimeMode mode)
    {
        TwinCatStatus status =
            AdsRuntimeStatusReader.MapStatus(adsState);

        Assert.Equal(started, status.Started);
        Assert.Equal(mode, status.Mode);
    }

    [Fact]
    public void InvalidAmsNetIdReturnsUnknownWithFailureEvidence()
    {
        AdsRuntimeStatusReadResult result =
            AdsRuntimeStatusReader.Read(
                "not-an-ams-net-id",
                TimeSpan.FromMilliseconds(100));

        Assert.Null(result.Status.Started);
        Assert.Equal(RuntimeMode.Unknown, result.Status.Mode);
        Assert.NotNull(result.Diagnostics.ErrorCode);
        Assert.NotNull(result.Failure);
    }

    [XaeFact]
    public void ReadsCurrentRemoteSystemServiceState()
    {
        AdsRuntimeStatusReadResult result =
            AdsRuntimeStatusReader.Read(
                "192.168.3.31.1.1",
                TimeSpan.FromSeconds(3));

        Assert.Null(result.Diagnostics.ErrorCode);
        Assert.Equal(
            AdsRuntimeStatusReader.SystemServicePort,
            result.Diagnostics.Port);
        Assert.False(
            string.IsNullOrWhiteSpace(
                result.Diagnostics.AdsState));
        Assert.NotEqual(
            RuntimeMode.Unknown,
            result.Status.Mode);
    }
}
