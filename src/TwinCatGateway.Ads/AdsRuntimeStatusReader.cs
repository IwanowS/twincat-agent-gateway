using System;
using TwinCAT.Ads;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Ads;

public sealed class AdsRuntimeStatusReadResult
{
    public AdsRuntimeStatusReadResult(
        TwinCatStatus status,
        AdsRuntimeDiagnostics diagnostics,
        Exception? failure = null)
    {
        Status = status;
        Diagnostics = diagnostics;
        Failure = failure;
    }

    public TwinCatStatus Status { get; }

    public AdsRuntimeDiagnostics Diagnostics { get; }

    public Exception? Failure { get; }
}

public static class AdsRuntimeStatusReader
{
    public const int SystemServicePort = 10000;

    public static AdsRuntimeStatusReadResult Read(
        string amsNetId,
        TimeSpan timeout)
    {
        return Read(
            amsNetId,
            SystemServicePort,
            timeout);
    }

    public static AdsRuntimeStatusReadResult Read(
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

    internal static AdsRuntimeStatusReadResult Read(
        AdsClient client,
        string amsNetId,
        int port,
        TimeSpan timeout,
        bool connect)
    {
        DateTimeOffset readAtUtc = DateTimeOffset.UtcNow;
        AdsRuntimeDiagnostics diagnostics = new()
        {
            AmsNetId = amsNetId,
            Port = port,
            ReadAtUtc = readAtUtc,
        };
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
                diagnostics.ErrorCode = error.ToString();
                return Unknown(diagnostics);
            }

            diagnostics.AdsState = state.AdsState.ToString();
            diagnostics.DeviceState = state.DeviceState;
            return new AdsRuntimeStatusReadResult(
                MapStatus(state.AdsState),
                diagnostics);
        }
        catch (Exception exception)
        {
            diagnostics.ErrorCode =
                exception.GetType().Name;
            return Unknown(diagnostics, exception);
        }
    }

    internal static TwinCatStatus MapStatus(
        AdsState state)
    {
        switch (state)
        {
            case AdsState.Run:
                return new TwinCatStatus
                {
                    Started = true,
                    Mode = RuntimeMode.Run,
                };
            case AdsState.Config:
            case AdsState.Reconfig:
                return new TwinCatStatus
                {
                    Started = false,
                    Mode = RuntimeMode.Config,
                };
            case AdsState.Stop:
            case AdsState.Stopping:
            case AdsState.Shutdown:
                return new TwinCatStatus
                {
                    Started = false,
                    Mode = RuntimeMode.Stop,
                };
            case AdsState.Error:
            case AdsState.Exception:
                return new TwinCatStatus
                {
                    Started = true,
                    Mode = RuntimeMode.Exception,
                };
            default:
                return new TwinCatStatus
                {
                    Started = null,
                    Mode = RuntimeMode.Unknown,
                };
        }
    }

    private static AdsRuntimeStatusReadResult Unknown(
        AdsRuntimeDiagnostics diagnostics,
        Exception? failure = null)
    {
        return new AdsRuntimeStatusReadResult(
            new TwinCatStatus
            {
                Started = null,
                Mode = RuntimeMode.Unknown,
            },
            diagnostics,
            failure);
    }
}
