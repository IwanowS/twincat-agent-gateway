using System;
using TwinCAT.Ads;
using TwinCatGateway.Ads;
using TwinCatGateway.Contracts;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class AdsRuntimeStatusReaderTests
{
    [Theory]
    [InlineData(AdsState.Invalid, TargetSystemState.Unknown)]
    [InlineData(AdsState.Idle, TargetSystemState.Unknown)]
    [InlineData(AdsState.Reset, TargetSystemState.Run)]
    [InlineData(AdsState.Init, TargetSystemState.Transitioning)]
    [InlineData(AdsState.Start, TargetSystemState.Transitioning)]
    [InlineData(AdsState.Run, TargetSystemState.Run)]
    [InlineData(AdsState.Stop, TargetSystemState.Stop)]
    [InlineData(AdsState.SaveConfig, TargetSystemState.Transitioning)]
    [InlineData(AdsState.LoadConfig, TargetSystemState.Transitioning)]
    [InlineData(AdsState.PowerFailure, TargetSystemState.Unknown)]
    [InlineData(AdsState.PowerGood, TargetSystemState.Unknown)]
    [InlineData(AdsState.Error, TargetSystemState.Exception)]
    [InlineData(AdsState.Shutdown, TargetSystemState.Transitioning)]
    [InlineData(AdsState.Suspend, TargetSystemState.Transitioning)]
    [InlineData(AdsState.Resume, TargetSystemState.Transitioning)]
    [InlineData(AdsState.Config, TargetSystemState.Config)]
    [InlineData(AdsState.Reconfig, TargetSystemState.Config)]
    [InlineData(AdsState.Stopping, TargetSystemState.Transitioning)]
    [InlineData(AdsState.Incompatible, TargetSystemState.Unknown)]
    [InlineData(AdsState.Exception, TargetSystemState.Exception)]
    public void MapsSystemServiceState(
        AdsState adsState,
        TargetSystemState expected)
    {
        Assert.Equal(
            expected,
            AdsStateMapper.MapSystemService((int)adsState));
    }

    [Theory]
    [InlineData(AdsState.Invalid, PlcRuntimeState.Unknown)]
    [InlineData(AdsState.Idle, PlcRuntimeState.Unknown)]
    [InlineData(AdsState.Reset, PlcRuntimeState.Reset)]
    [InlineData(AdsState.Init, PlcRuntimeState.Transitioning)]
    [InlineData(AdsState.Start, PlcRuntimeState.Transitioning)]
    [InlineData(AdsState.Run, PlcRuntimeState.Run)]
    [InlineData(AdsState.Stop, PlcRuntimeState.Stop)]
    [InlineData(AdsState.SaveConfig, PlcRuntimeState.Transitioning)]
    [InlineData(AdsState.LoadConfig, PlcRuntimeState.Transitioning)]
    [InlineData(AdsState.PowerFailure, PlcRuntimeState.Unknown)]
    [InlineData(AdsState.PowerGood, PlcRuntimeState.Unknown)]
    [InlineData(AdsState.Error, PlcRuntimeState.Exception)]
    [InlineData(AdsState.Shutdown, PlcRuntimeState.Transitioning)]
    [InlineData(AdsState.Suspend, PlcRuntimeState.Transitioning)]
    [InlineData(AdsState.Resume, PlcRuntimeState.Transitioning)]
    [InlineData(AdsState.Config, PlcRuntimeState.Unknown)]
    [InlineData(AdsState.Reconfig, PlcRuntimeState.Transitioning)]
    [InlineData(AdsState.Stopping, PlcRuntimeState.Transitioning)]
    [InlineData(AdsState.Incompatible, PlcRuntimeState.Unknown)]
    [InlineData(AdsState.Exception, PlcRuntimeState.Exception)]
    public void MapsPlcRuntimeState(
        AdsState adsState,
        PlcRuntimeState expected)
    {
        Assert.Equal(
            expected,
            AdsStateMapper.MapPlcRuntime((int)adsState));
    }

    [Fact]
    public void SameRawResetHasDeviceSpecificMeaning()
    {
        Assert.Equal(
            TargetSystemState.Run,
            AdsStateMapper.MapSystemService(
                (int)AdsState.Reset));
        Assert.Equal(
            PlcRuntimeState.Reset,
            AdsStateMapper.MapPlcRuntime(
                (int)AdsState.Reset));
    }

    [Fact]
    public void InvalidAmsNetIdReturnsUnknownWithFailureEvidence()
    {
        AdsStateReadResult result =
            AdsStateReader.Read(
                "not-an-ams-net-id",
                TimeSpan.FromMilliseconds(100));

        Assert.False(result.Succeeded);
        Assert.Equal(
            ErrorCodes.AdsStateReadFailed,
            result.Error?.Code);
        Assert.NotNull(result.Failure);
        Assert.Null(result.RawAdsState);
    }

    [XaeFact]
    public void ReadsCurrentRemoteSystemServiceState()
    {
        AdsStateReadResult result =
            AdsStateReader.Read(
                "192.168.3.31.1.1",
                TimeSpan.FromSeconds(3));

        Assert.True(
            result.Succeeded,
            result.Error?.Message);
        Assert.Equal(
            AdsStateReader.SystemServicePort,
            result.Port);
        Assert.NotNull(result.RawAdsState);
        Assert.False(
            string.IsNullOrWhiteSpace(
                result.RawAdsStateName));
        Assert.NotEqual(
            TargetSystemState.Unknown,
            AdsStateMapper.MapSystemService(
                result.RawAdsState!.Value));
    }
}
