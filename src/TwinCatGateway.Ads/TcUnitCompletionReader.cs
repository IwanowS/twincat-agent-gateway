using System;
using TwinCAT.Ads;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Ads;

public enum TcUnitCompletionFailureKind
{
    None,
    AdsUnavailable,
    CompletionSymbolUnavailable,
    SuiteCountSymbolUnavailable,
}

public sealed class TcUnitCompletionReadResult
{
    public bool? Finished { get; set; }

    public int? InitializedSuites { get; set; }

    public TcUnitCompletionFailureKind FailureKind { get; set; }

    public string? AdsErrorCode { get; set; }

    public string? FailedSymbol { get; set; }

    public DateTimeOffset ReadAtUtc { get; set; }

    public Exception? Failure { get; set; }
}

public sealed class TcUnitCompletionReader
{
    private readonly ITcUnitAdsConnectionFactory _connections;

    public TcUnitCompletionReader()
        : this(TcUnitAdsConnectionFactory.Instance)
    {
    }

    internal TcUnitCompletionReader(
        ITcUnitAdsConnectionFactory connections)
    {
        _connections = connections
            ?? throw new ArgumentNullException(
                nameof(connections));
    }

    public TcUnitCompletionReadResult Read(
        string amsNetId,
        TcUnitProfile profile,
        TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(amsNetId))
        {
            throw new ArgumentException(
                "Target AMS NetId is required.",
                nameof(amsNetId));
        }

        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        if (profile.AdsPort <= 0
            || profile.AdsPort > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(
                profile.FinishedSymbol)
            || string.IsNullOrWhiteSpace(
                profile.SuiteCountSymbol))
        {
            throw new ArgumentException(
                "Both fixed TcUnit symbols are required.",
                nameof(profile));
        }

        if (timeout <= TimeSpan.Zero
            || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout));
        }

        DateTimeOffset readAtUtc = DateTimeOffset.UtcNow;
        try
        {
            using ITcUnitAdsConnection connection =
                _connections.Connect(
                    amsNetId,
                    profile.AdsPort,
                    checked((int)timeout.TotalMilliseconds));
            AdsErrorCode finishedError =
                connection.TryReadBoolean(
                    profile.FinishedSymbol,
                    out bool finished);
            if (finishedError != AdsErrorCode.NoError)
            {
                return Failure(
                    finishedError,
                    profile.FinishedSymbol,
                    TcUnitCompletionFailureKind
                        .CompletionSymbolUnavailable,
                    readAtUtc);
            }

            AdsErrorCode suiteCountError =
                connection.TryReadUInt16(
                    profile.SuiteCountSymbol,
                    out ushort initializedSuites);
            if (suiteCountError != AdsErrorCode.NoError)
            {
                return Failure(
                    suiteCountError,
                    profile.SuiteCountSymbol,
                    TcUnitCompletionFailureKind
                        .SuiteCountSymbolUnavailable,
                    readAtUtc);
            }

            return new TcUnitCompletionReadResult
            {
                Finished = finished,
                InitializedSuites = initializedSuites,
                FailureKind =
                    TcUnitCompletionFailureKind.None,
                ReadAtUtc = readAtUtc,
            };
        }
        catch (Exception exception)
        {
            return new TcUnitCompletionReadResult
            {
                FailureKind =
                    TcUnitCompletionFailureKind.AdsUnavailable,
                AdsErrorCode =
                    exception.GetType().Name,
                ReadAtUtc = readAtUtc,
                Failure = exception,
            };
        }
    }

    private static TcUnitCompletionReadResult Failure(
        AdsErrorCode error,
        string symbol,
        TcUnitCompletionFailureKind symbolFailureKind,
        DateTimeOffset readAtUtc)
    {
        bool missingSymbol =
            error == AdsErrorCode.DeviceSymbolNotFound
            || error == AdsErrorCode.DeviceSymbolNotActive;
        return new TcUnitCompletionReadResult
        {
            FailureKind = missingSymbol
                ? symbolFailureKind
                : TcUnitCompletionFailureKind.AdsUnavailable,
            AdsErrorCode = error.ToString(),
            FailedSymbol = symbol,
            ReadAtUtc = readAtUtc,
        };
    }
}

internal interface ITcUnitAdsConnectionFactory
{
    ITcUnitAdsConnection Connect(
        string amsNetId,
        int port,
        int timeoutMilliseconds);
}

internal interface ITcUnitAdsConnection : IDisposable
{
    AdsErrorCode TryReadBoolean(
        string symbol,
        out bool value);

    AdsErrorCode TryReadUInt16(
        string symbol,
        out ushort value);
}

internal sealed class TcUnitAdsConnectionFactory
    : ITcUnitAdsConnectionFactory
{
    public static readonly TcUnitAdsConnectionFactory
        Instance = new();

    private TcUnitAdsConnectionFactory()
    {
    }

    public ITcUnitAdsConnection Connect(
        string amsNetId,
        int port,
        int timeoutMilliseconds)
    {
        AdsClient client = new()
        {
            Timeout = timeoutMilliseconds,
        };
        try
        {
            client.Connect(amsNetId, port);
            return new TcUnitAdsConnection(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

internal sealed class TcUnitAdsConnection
    : ITcUnitAdsConnection
{
    private readonly AdsClient _client;

    public TcUnitAdsConnection(AdsClient client)
    {
        _client = client
            ?? throw new ArgumentNullException(
                nameof(client));
    }

    public AdsErrorCode TryReadBoolean(
        string symbol,
        out bool value)
    {
        return _client.TryReadValue(
            symbol,
            out value);
    }

    public AdsErrorCode TryReadUInt16(
        string symbol,
        out ushort value)
    {
        return _client.TryReadValue(
            symbol,
            out value);
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
