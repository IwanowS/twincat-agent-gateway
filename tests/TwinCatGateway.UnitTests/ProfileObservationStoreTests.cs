using System;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class ProfileObservationStoreTests
{
    [Fact]
    public void FreshTargetReadBecomesStaleOnFailureAndRecovers()
    {
        ProfileObservationStore store = CreateStore();
        store.PublishTarget(Target(TargetSystemState.Run));

        ProfileObservationSnapshot failed =
            store.MarkTargetReadFailed(
                ObservedAt.AddSeconds(1),
                ReadError());

        Assert.Equal(
            ObservationFreshness.Stale,
            failed.Target.Freshness);
        Assert.Equal(5, failed.Target.RawAdsState);
        Assert.Equal(TargetSystemState.Run, failed.Target.State);
        Assert.Equal(
            ErrorCodes.AdsStateReadFailed,
            failed.Target.Error?.Code);

        ProfileObservationSnapshot recovered =
            store.PublishTarget(
                Target(TargetSystemState.Config));

        Assert.Equal(
            ObservationFreshness.Fresh,
            recovered.Target.Freshness);
        Assert.Null(recovered.Target.Error);
    }

    [Fact]
    public void InitialReadFailureIsUnavailable()
    {
        ProfileObservationStore store = CreateStore();

        ProfileObservationSnapshot result =
            store.MarkTargetReadFailed(
                ObservedAt,
                ReadError());

        Assert.Equal(
            ObservationFreshness.Unavailable,
            result.Target.Freshness);
        Assert.Equal(
            TargetSystemState.Unknown,
            result.Target.State);
        Assert.Null(result.Target.RawAdsState);
    }

    [Fact]
    public void DivergenceRequiresFreshMatchingKnownObservations()
    {
        ProfileObservationStore store = CreateStore();
        store.PublishTarget(Target(TargetSystemState.Config));

        ProfileObservationSnapshot diverged =
            store.PublishXae(
                Xae(
                    TargetSystemState.Run,
                    AmsNetId));

        Assert.NotNull(diverged.Divergence);
        Assert.Equal(
            TargetSystemState.Run,
            diverged.Divergence?.XaeObserved);
        Assert.Equal(
            TargetSystemState.Config,
            diverged.Divergence
                ?.SystemServiceObserved);

        ProfileObservationSnapshot stale =
            store.MarkTargetReadFailed(
                ObservedAt.AddSeconds(1),
                ReadError());
        Assert.Null(stale.Divergence);

        store.PublishTarget(Target(TargetSystemState.Config));
        ProfileObservationSnapshot mismatched =
            store.PublishXae(
                Xae(
                    TargetSystemState.Run,
                    "1.2.3.4.5.6"));
        Assert.Null(mismatched.Divergence);

        ProfileObservationSnapshot unknown =
            store.PublishXae(
                Xae(
                    TargetSystemState.Unknown,
                    AmsNetId));
        Assert.Null(unknown.Divergence);
    }

    [Fact]
    public void PlcFailureDoesNotChangeTargetState()
    {
        ProfileObservationStore store = CreateStore();
        store.ConfigureRuntimes(
            new[]
            {
                new PlcRuntimeTarget(
                    "plc-851",
                    "MachinePlc",
                    instance: null,
                    adsPort: 851),
            });
        store.PublishTarget(Target(TargetSystemState.Run));
        store.PublishPlc(Plc(PlcRuntimeState.Exception));

        ProfileObservationSnapshot snapshot = store.Read();

        Assert.Equal(
            TargetSystemState.Run,
            snapshot.Target.State);
        Assert.Equal(
            PlcRuntimeState.Exception,
            Assert.Single(snapshot.PlcRuntimes).State);
    }

    [Fact]
    public void ConfigMakesPlcExplicitlyUnavailable()
    {
        ProfileObservationStore store = CreateStore();
        store.ConfigureRuntimes(
            new[]
            {
                new PlcRuntimeTarget(
                    "plc-851",
                    "MachinePlc",
                    instance: null,
                    adsPort: 851),
            });
        store.PublishPlc(Plc(PlcRuntimeState.Run));

        ProfileObservationSnapshot snapshot =
            store.MarkPlcNotObserved(
                "plc-851",
                851,
                ObservedAt.AddSeconds(1));
        PlcRuntimeObservation plc =
            Assert.Single(snapshot.PlcRuntimes);

        Assert.Equal(
            ObservationFreshness.Unavailable,
            plc.Freshness);
        Assert.Equal(PlcRuntimeState.Unknown, plc.State);
        Assert.Null(plc.RawAdsState);
        Assert.Equal(
            ErrorCodes.PlcStateNotObserved,
            plc.Error?.Code);
    }

    private static ProfileObservationStore CreateStore()
    {
        return new ProfileObservationStore(
            "bench",
            AmsNetId);
    }

    private static TargetSystemObservation Target(
        TargetSystemState state)
    {
        return new TargetSystemObservation
        {
            Profile = "bench",
            AmsNetId = AmsNetId,
            Port = 10000,
            RawAdsState = 5,
            RawAdsStateName = state.ToString(),
            RawDeviceState = 2,
            State = state,
            ObservedAtUtc = ObservedAt,
            Freshness = ObservationFreshness.Fresh,
        };
    }

    private static XaeTwinCatSystemObservation Xae(
        TargetSystemState state,
        string target)
    {
        return new XaeTwinCatSystemObservation
        {
            State = state,
            RawState = state.ToString(),
            SelectedTarget = target,
            ObservedAtUtc = ObservedAt,
            Freshness = ObservationFreshness.Fresh,
        };
    }

    private static PlcRuntimeObservation Plc(
        PlcRuntimeState state)
    {
        return new PlcRuntimeObservation
        {
            Profile = "bench",
            RuntimeId = "plc-851",
            Project = "MachinePlc",
            AmsNetId = AmsNetId,
            Port = 851,
            RawAdsState = 5,
            RawAdsStateName = state.ToString(),
            RawDeviceState = 2,
            State = state,
            ObservedAtUtc = ObservedAt,
            Freshness = ObservationFreshness.Fresh,
        };
    }

    private static ObservationError ReadError()
    {
        return new ObservationError
        {
            Code = ErrorCodes.AdsStateReadFailed,
            Message = "ADS state read failed.",
            Retryable = true,
        };
    }

    private const string AmsNetId = "192.168.3.31.1.1";
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
}
