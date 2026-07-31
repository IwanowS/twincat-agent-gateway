using System;
using TwinCatGateway.Contracts;
using TwinCatGateway.Xae;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class XaeTwinCatSystemStateMapperTests
{
    [Fact]
    public void StartedFlagMapsToFreshRunObservation()
    {
        XaeTwinCatSystemObservation result =
            XaeTwinCatSystemStateMapper.FromStartedFlag(
                started: true,
                "192.168.3.31.1.1",
                ObservedAt);

        Assert.Equal(ObservationSource.Xae, result.Source);
        Assert.Equal(TargetSystemState.Run, result.State);
        Assert.Equal(
            "IsTwinCATStarted=true",
            result.RawState);
        Assert.Equal(
            ObservationFreshness.Fresh,
            result.Freshness);
        Assert.Null(result.Error);
    }

    [Fact]
    public void FalseFlagDoesNotGuessConfigWithoutBenchEvidence()
    {
        XaeTwinCatSystemObservation result =
            XaeTwinCatSystemStateMapper.FromStartedFlag(
                started: false,
                "192.168.3.31.1.1",
                ObservedAt);

        Assert.Equal(TargetSystemState.Unknown, result.State);
        Assert.Equal(
            "IsTwinCATStarted=false",
            result.RawState);
        Assert.Equal(
            ObservationFreshness.Fresh,
            result.Freshness);
    }

    [Fact]
    public void FailedReadIsUnavailableAndNeverSubstitutesAds()
    {
        XaeTwinCatSystemObservation result =
            XaeTwinCatSystemStateMapper.Unavailable(
                "192.168.3.31.1.1",
                ObservedAt,
                "XAE state is unavailable.");

        Assert.Equal(TargetSystemState.Unknown, result.State);
        Assert.Equal(
            ObservationFreshness.Unavailable,
            result.Freshness);
        Assert.Null(result.RawState);
        Assert.Equal(
            ErrorCodes.XaeSystemStateUnavailable,
            result.Error?.Code);
    }

    private static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
}
