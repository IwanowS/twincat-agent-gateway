using System;
using TwinCAT.Ads;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Ads;

public sealed class AdsStateReadResult
{
    internal AdsStateReadResult(
        string amsNetId,
        int port,
        DateTimeOffset observedAtUtc,
        int? rawAdsState,
        string? rawAdsStateName,
        int? rawDeviceState,
        ObservationError? error,
        Exception? failure)
    {
        AmsNetId = amsNetId;
        Port = port;
        ObservedAtUtc = observedAtUtc;
        RawAdsState = rawAdsState;
        RawAdsStateName = rawAdsStateName;
        RawDeviceState = rawDeviceState;
        Error = error;
        Failure = failure;
    }

    public string AmsNetId { get; }

    public int Port { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public int? RawAdsState { get; }

    public string? RawAdsStateName { get; }

    public int? RawDeviceState { get; }

    public ObservationError? Error { get; }

    public Exception? Failure { get; }

    public bool Succeeded => Error is null;
}

public static class AdsStateReader
{
    public const int SystemServicePort = 10000;

    public static AdsStateReadResult Read(
        string amsNetId,
        TimeSpan timeout)
    {
        return Read(
            amsNetId,
            SystemServicePort,
            timeout);
    }

    public static AdsStateReadResult Read(
        string amsNetId,
        int port,
        TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(amsNetId))
        {
            throw new ArgumentException(
                "Target AMS NetId is required.",
                nameof(amsNetId));
        }

        if (port <= 0 || port > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        if (timeout <= TimeSpan.Zero
            || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout));
        }

        using AdsClient client = new();
        return Read(
            client,
            amsNetId,
            port,
            timeout,
            connect: true);
    }

    internal static AdsStateReadResult Read(
        AdsClient client,
        string amsNetId,
        int port,
        TimeSpan timeout,
        bool connect)
    {
        DateTimeOffset observedAtUtc =
            DateTimeOffset.UtcNow;
        try
        {
            client.Timeout =
                checked((int)timeout.TotalMilliseconds);
            if (connect)
            {
                client.Connect(
                    amsNetId,
                    port);
            }

            AdsErrorCode error = client.TryReadState(
                out StateInfo state);
            if (error != AdsErrorCode.NoError)
            {
                return Failed(
                    amsNetId,
                    port,
                    observedAtUtc,
                    $"ADS state read failed with {error}.",
                    failure: null);
            }

            return new AdsStateReadResult(
                amsNetId,
                port,
                observedAtUtc,
                (int)state.AdsState,
                state.AdsState.ToString(),
                state.DeviceState,
                error: null,
                failure: null);
        }
        catch (Exception exception)
        {
            return Failed(
                amsNetId,
                port,
                observedAtUtc,
                "ADS state read failed with "
                    + $"{exception.GetType().Name}.",
                exception);
        }
    }

    private static AdsStateReadResult Failed(
        string amsNetId,
        int port,
        DateTimeOffset observedAtUtc,
        string message,
        Exception? failure)
    {
        return new AdsStateReadResult(
            amsNetId,
            port,
            observedAtUtc,
            rawAdsState: null,
            rawAdsStateName: null,
            rawDeviceState: null,
            new ObservationError
            {
                Code = ErrorCodes.AdsStateReadFailed,
                Message = message,
                Retryable = true,
            },
            failure);
    }
}

public static class AdsStateMapper
{
    public static TargetSystemState MapSystemService(
        int rawAdsState)
    {
        AdsState state = (AdsState)rawAdsState;
        switch (state)
        {
            case AdsState.Reset:
            case AdsState.Run:
                return TargetSystemState.Run;
            case AdsState.Config:
            case AdsState.Reconfig:
                return TargetSystemState.Config;
            case AdsState.Stop:
                return TargetSystemState.Stop;
            case AdsState.Error:
            case AdsState.Exception:
                return TargetSystemState.Exception;
            case AdsState.Init:
            case AdsState.Start:
            case AdsState.SaveConfig:
            case AdsState.LoadConfig:
            case AdsState.Shutdown:
            case AdsState.Suspend:
            case AdsState.Resume:
            case AdsState.Stopping:
                return TargetSystemState.Transitioning;
            default:
                return TargetSystemState.Unknown;
        }
    }

    public static PlcRuntimeState MapPlcRuntime(
        int rawAdsState)
    {
        AdsState state = (AdsState)rawAdsState;
        switch (state)
        {
            case AdsState.Run:
                return PlcRuntimeState.Run;
            case AdsState.Stop:
                return PlcRuntimeState.Stop;
            case AdsState.Reset:
                return PlcRuntimeState.Reset;
            case AdsState.Error:
            case AdsState.Exception:
                return PlcRuntimeState.Exception;
            case AdsState.Init:
            case AdsState.Start:
            case AdsState.SaveConfig:
            case AdsState.LoadConfig:
            case AdsState.Shutdown:
            case AdsState.Suspend:
            case AdsState.Resume:
            case AdsState.Reconfig:
            case AdsState.Stopping:
                return PlcRuntimeState.Transitioning;
            default:
                return PlcRuntimeState.Unknown;
        }
    }
}
