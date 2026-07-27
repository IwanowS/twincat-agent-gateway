using System;
using TwinCAT.Ads;
using TwinCatGateway.Ads;
using TwinCatGateway.Contracts;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class TcUnitCompletionReaderTests
{
    [Fact]
    public void ReadsOnlyConfiguredCompletionEvidence()
    {
        FakeConnection connection = new()
        {
            Finished = true,
            InitializedSuites = 7,
        };
        TcUnitProfile profile = CreateProfile();
        TcUnitCompletionReader reader = new(
            new FakeConnectionFactory(connection));

        TcUnitCompletionReadResult result = reader.Read(
            "192.168.3.31.1.1",
            profile,
            TimeSpan.FromSeconds(1));

        Assert.True(result.Finished);
        Assert.Equal(7, result.InitializedSuites);
        Assert.Equal(
            TcUnitCompletionFailureKind.None,
            result.FailureKind);
        Assert.Equal(
            profile.FinishedSymbol,
            connection.BooleanSymbol);
        Assert.Equal(
            profile.SuiteCountSymbol,
            connection.UInt16Symbol);
        Assert.True(connection.Disposed);
    }

    [Fact]
    public void MissingCompletionSymbolIsDistinctFromAdsFailure()
    {
        FakeConnection connection = new()
        {
            BooleanError =
                AdsErrorCode.DeviceSymbolNotFound,
        };
        TcUnitProfile profile = CreateProfile();
        TcUnitCompletionReader reader = new(
            new FakeConnectionFactory(connection));

        TcUnitCompletionReadResult result = reader.Read(
            "192.168.3.31.1.1",
            profile,
            TimeSpan.FromSeconds(1));

        Assert.Equal(
            TcUnitCompletionFailureKind
                .CompletionSymbolUnavailable,
            result.FailureKind);
        Assert.Equal(
            profile.FinishedSymbol,
            result.FailedSymbol);
        Assert.Equal(
            AdsErrorCode.DeviceSymbolNotFound.ToString(),
            result.AdsErrorCode);
        Assert.Null(connection.UInt16Symbol);
    }

    [Fact]
    public void MissingSuiteCountSymbolIsReportedPrecisely()
    {
        FakeConnection connection = new()
        {
            UInt16Error =
                AdsErrorCode.DeviceSymbolNotActive,
        };
        TcUnitProfile profile = CreateProfile();
        TcUnitCompletionReader reader = new(
            new FakeConnectionFactory(connection));

        TcUnitCompletionReadResult result = reader.Read(
            "192.168.3.31.1.1",
            profile,
            TimeSpan.FromSeconds(1));

        Assert.Equal(
            TcUnitCompletionFailureKind
                .SuiteCountSymbolUnavailable,
            result.FailureKind);
        Assert.Equal(
            profile.SuiteCountSymbol,
            result.FailedSymbol);
    }

    [Fact]
    public void RouteFailureDoesNotMasqueradeAsMissingSymbol()
    {
        FakeConnection connection = new()
        {
            BooleanError =
                AdsErrorCode.TargetMachineNotFound,
        };
        TcUnitCompletionReader reader = new(
            new FakeConnectionFactory(connection));

        TcUnitCompletionReadResult result = reader.Read(
            "192.168.3.31.1.1",
            CreateProfile(),
            TimeSpan.FromSeconds(1));

        Assert.Equal(
            TcUnitCompletionFailureKind.AdsUnavailable,
            result.FailureKind);
        Assert.Equal(
            AdsErrorCode.TargetMachineNotFound.ToString(),
            result.AdsErrorCode);
    }

    [Fact]
    public void ConnectionExceptionReturnsAdsUnavailable()
    {
        InvalidOperationException failure =
            new("route unavailable");
        TcUnitCompletionReader reader = new(
            new ThrowingConnectionFactory(failure));

        TcUnitCompletionReadResult result = reader.Read(
            "192.168.3.31.1.1",
            CreateProfile(),
            TimeSpan.FromSeconds(1));

        Assert.Equal(
            TcUnitCompletionFailureKind.AdsUnavailable,
            result.FailureKind);
        Assert.Same(failure, result.Failure);
        Assert.Equal(
            nameof(InvalidOperationException),
            result.AdsErrorCode);
    }

    [XaeFact]
    public void ReadsConfiguredSymbolsFromRemoteFixture()
    {
        TcUnitCompletionReader reader = new();

        TcUnitCompletionReadResult result = reader.Read(
            "192.168.3.31.1.1",
            CreateProfile(),
            TimeSpan.FromSeconds(3));

        Assert.Equal(
            TcUnitCompletionFailureKind.None,
            result.FailureKind);
        Assert.NotNull(result.Finished);
        Assert.True(result.InitializedSuites >= 0);
    }

    private static TcUnitProfile CreateProfile()
    {
        return new TcUnitProfile
        {
            AdsPort = 851,
            FinishedSymbol =
                "GVL_TcUnit.TcUnitRunner."
                + "AllTestSuitesFinished",
            SuiteCountSymbol =
                "GVL_TcUnit."
                + "NumberOfInitializedTestSuites",
            ReportPath = @"C:\Reports\tcunit.xml",
        };
    }

    private sealed class FakeConnectionFactory
        : ITcUnitAdsConnectionFactory
    {
        private readonly ITcUnitAdsConnection _connection;

        public FakeConnectionFactory(
            ITcUnitAdsConnection connection)
        {
            _connection = connection;
        }

        public ITcUnitAdsConnection Connect(
            string amsNetId,
            int port,
            int timeoutMilliseconds)
        {
            Assert.Equal("192.168.3.31.1.1", amsNetId);
            Assert.Equal(851, port);
            Assert.Equal(1000, timeoutMilliseconds);
            return _connection;
        }
    }

    private sealed class ThrowingConnectionFactory
        : ITcUnitAdsConnectionFactory
    {
        private readonly Exception _failure;

        public ThrowingConnectionFactory(Exception failure)
        {
            _failure = failure;
        }

        public ITcUnitAdsConnection Connect(
            string amsNetId,
            int port,
            int timeoutMilliseconds)
        {
            throw _failure;
        }
    }

    private sealed class FakeConnection
        : ITcUnitAdsConnection
    {
        public bool Finished { get; set; }

        public ushort InitializedSuites { get; set; }

        public AdsErrorCode BooleanError { get; set; } =
            AdsErrorCode.NoError;

        public AdsErrorCode UInt16Error { get; set; } =
            AdsErrorCode.NoError;

        public string? BooleanSymbol { get; private set; }

        public string? UInt16Symbol { get; private set; }

        public bool Disposed { get; private set; }

        public AdsErrorCode TryReadBoolean(
            string symbol,
            out bool value)
        {
            BooleanSymbol = symbol;
            value = Finished;
            return BooleanError;
        }

        public AdsErrorCode TryReadUInt16(
            string symbol,
            out ushort value)
        {
            UInt16Symbol = symbol;
            value = InitializedSuites;
            return UInt16Error;
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
