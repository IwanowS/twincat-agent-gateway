using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Ads;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Desktop;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class TcUnitRunExecutorTests
{
    [Fact]
    public async Task CollectsFreshStableReportAfterAdsCompletion()
    {
        using ExecutorFixture fixture = new();
        File.WriteAllText(
            fixture.ReportPath,
            "<testsuite name=\"Old\" />");
        bool reportWritten = false;
        fixture.Reader.Results.Enqueue(
            () =>
            {
                File.WriteAllText(
                    fixture.ReportPath,
                    SuccessfulReport);
                reportWritten = true;
                return Completed(initializedSuites: 1);
            });
        TcUnitRunPreparation preparation =
            fixture.Executor.Prepare(
                "activation-1");

        TestResult result =
            await fixture.Executor.ExecuteAsync(
                "test-1",
                "activation-1",
                preparation,
                CancellationToken.None);

        Assert.True(reportWritten);
        Assert.True(result.Ok);
        Assert.Equal(2, result.Counts.Tests);
        Assert.Equal(2, result.Counts.Passed);
        Assert.Equal(1, result.InitializedSuites);
        Assert.NotNull(result.Report);
        ResourceContent report = fixture.Logs.Read(
            result.Report!.Uri);
        Assert.Contains(
            "Starts",
            report.Content);
        Assert.Equal(
            new[]
            {
                GatewayEventTypes
                    .TcUnitCompletionObserved,
                GatewayEventTypes
                    .TcUnitReportProduced,
            },
            fixture.Events.ReadAfter(null, 0, 20)
                .Events.Select(
                    gatewayEvent => gatewayEvent.Type));
    }

    [Fact]
    public async Task StaleReportIsNotAccepted()
    {
        using ExecutorFixture fixture = new();
        File.WriteAllText(
            fixture.ReportPath,
            SuccessfulReport);
        fixture.Reader.Fallback =
            () => Completed(initializedSuites: 1);
        TcUnitRunPreparation preparation =
            fixture.Executor.Prepare(
                "activation-1");

        GatewayOperationException exception =
            await Assert.ThrowsAsync<
                GatewayOperationException>(
                () => fixture.Executor.ExecuteAsync(
                    "test-1",
                    "activation-1",
                    preparation,
                    CancellationToken.None));

        Assert.Equal(
            ErrorCodes.TestReportNotProduced,
            exception.Code);
    }

    [Fact]
    public async Task MissingSymbolHasDistinctErrorCode()
    {
        using ExecutorFixture fixture = new();
        fixture.Reader.Fallback = () =>
            new TcUnitCompletionReadResult
            {
                FailureKind =
                    TcUnitCompletionFailureKind
                        .CompletionSymbolUnavailable,
                AdsErrorCode =
                    "DeviceSymbolNotFound",
                FailedSymbol =
                    fixture.Profile.TcUnit!
                        .FinishedSymbol,
            };
        TcUnitRunPreparation preparation =
            fixture.Executor.Prepare(
                "activation-1");

        GatewayOperationException exception =
            await Assert.ThrowsAsync<
                GatewayOperationException>(
                () => fixture.Executor.ExecuteAsync(
                    "test-1",
                    "activation-1",
                    preparation,
                    CancellationToken.None));

        Assert.Equal(
            ErrorCodes
                .TestCompletionSymbolUnavailable,
            exception.Code);
    }

    [Fact]
    public async Task AdsFailureHasDistinctErrorCode()
    {
        using ExecutorFixture fixture = new();
        fixture.Reader.Fallback = () =>
            new TcUnitCompletionReadResult
            {
                FailureKind =
                    TcUnitCompletionFailureKind
                        .AdsUnavailable,
                AdsErrorCode =
                    "TargetMachineNotFound",
            };
        TcUnitRunPreparation preparation =
            fixture.Executor.Prepare(
                "activation-1");

        GatewayOperationException exception =
            await Assert.ThrowsAsync<
                GatewayOperationException>(
                () => fixture.Executor.ExecuteAsync(
                    "test-1",
                    "activation-1",
                    preparation,
                    CancellationToken.None));

        Assert.Equal(
            ErrorCodes.TestAdsUnavailable,
            exception.Code);
    }

    [Fact]
    public async Task InvalidFreshReportIsRejected()
    {
        using ExecutorFixture fixture = new();
        fixture.Reader.Results.Enqueue(
            () =>
            {
                File.WriteAllText(
                    fixture.ReportPath,
                    "<testsuite>");
                return Completed(initializedSuites: 1);
            });
        TcUnitRunPreparation preparation =
            fixture.Executor.Prepare(
                "activation-1");

        GatewayOperationException exception =
            await Assert.ThrowsAsync<
                GatewayOperationException>(
                () => fixture.Executor.ExecuteAsync(
                    "test-1",
                    "activation-1",
                    preparation,
                    CancellationToken.None));

        Assert.Equal(
            ErrorCodes.TestReportInvalid,
            exception.Code);
    }

    [Fact]
    public async Task FailedTestsProduceCompletedFailedResult()
    {
        using ExecutorFixture fixture = new();
        fixture.Reader.Results.Enqueue(
            () =>
            {
                File.WriteAllText(
                    fixture.ReportPath,
                    """
                    <testsuite name="Suite">
                      <testcase name="Broken">
                        <failure message="Expected true" />
                      </testcase>
                    </testsuite>
                    """);
                return Completed(initializedSuites: 1);
            });
        TcUnitRunPreparation preparation =
            fixture.Executor.Prepare(
                "activation-1");

        TestResult result =
            await fixture.Executor.ExecuteAsync(
                "test-1",
                "activation-1",
                preparation,
                CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(1, result.Counts.Failed);
        Assert.Single(result.Failures);
    }

    [Theory]
    [InlineData(ZeroTestsPolicy.Fail, false, false)]
    [InlineData(ZeroTestsPolicy.Warn, true, true)]
    [InlineData(ZeroTestsPolicy.Allow, true, false)]
    public async Task AppliesZeroTestsPolicy(
        ZeroTestsPolicy policy,
        bool expectedOk,
        bool expectedWarning)
    {
        using ExecutorFixture fixture = new();
        fixture.Profile.TcUnit!.ZeroTests = policy;
        fixture.Reader.Results.Enqueue(
            () =>
            {
                File.WriteAllText(
                    fixture.ReportPath,
                    "<testsuite name=\"Empty\" />");
                return Completed(initializedSuites: 0);
            });
        TcUnitRunPreparation preparation =
            fixture.Executor.Prepare(
                "activation-1");

        TestResult result =
            await fixture.Executor.ExecuteAsync(
                "test-1",
                "activation-1",
                preparation,
                CancellationToken.None);

        Assert.Equal(expectedOk, result.Ok);
        Assert.Equal(
            expectedWarning,
            fixture.Events.ReadAfter(null, 0, 20)
                .Events.Any(gatewayEvent =>
                    gatewayEvent.Type
                        == GatewayEventTypes
                            .TcUnitZeroTests));
    }

    private static TcUnitCompletionReadResult Completed(
        int initializedSuites)
    {
        return new TcUnitCompletionReadResult
        {
            Finished = true,
            InitializedSuites =
                initializedSuites,
            FailureKind =
                TcUnitCompletionFailureKind.None,
        };
    }

    private const string SuccessfulReport =
        """
        <testsuite name="Suite">
          <testcase name="Starts" />
          <testcase name="Stops" />
        </testsuite>
        """;

    private sealed class ExecutorFixture : IDisposable
    {
        private readonly string _directory;

        public ExecutorFixture()
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                "TwinCatGatewayTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            ReportPath = Path.Combine(
                _directory,
                "tcunit.xml");
            Profile = new ProjectProfile
            {
                Name = "fixture",
                Solution =
                    @"C:\Projects\Machine\Machine.sln",
                ExpectedTarget = new TargetIdentity
                {
                    AmsNetId =
                        "192.168.3.31.1.1",
                },
                TcUnit = new TcUnitProfile
                {
                    AdsPort = 851,
                    ReportPath = ReportPath,
                    CompletionTimeoutSeconds = 1,
                },
            };
            Status = new GatewayStatusSnapshotStore(
                GatewayStatusSnapshotStore
                    .CreateInitial("0.1.0"));
            Events = new GatewayEventJournal(Status);
            Logs = new LocalLogStore(_directory);
            Reader = new FakeCompletionReader();
            Clock = new AdvancingClock(
                new DateTimeOffset(
                    2026,
                    7,
                    28,
                    4,
                    0,
                    0,
                    TimeSpan.Zero));
            Executor = new TcUnitRunExecutor(
                Profile,
                Logs,
                new StructuredFileLogger(
                    _directory,
                    Clock),
                Events,
                Reader,
                Clock);
        }

        public string ReportPath { get; }

        public ProjectProfile Profile { get; }

        public GatewayStatusSnapshotStore Status { get; }

        public GatewayEventJournal Events { get; }

        public LocalLogStore Logs { get; }

        public FakeCompletionReader Reader { get; }

        public AdvancingClock Clock { get; }

        public TcUnitRunExecutor Executor { get; }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(
                    _directory,
                    recursive: true);
            }
        }
    }

    private sealed class FakeCompletionReader
        : ITcUnitCompletionEvidenceReader
    {
        public Queue<Func<TcUnitCompletionReadResult>>
            Results { get; } = new();

        public Func<TcUnitCompletionReadResult>
            Fallback { get; set; } =
                () => Completed(1);

        public TcUnitCompletionReadResult Read(
            string amsNetId,
            TcUnitProfile profile,
            TimeSpan timeout)
        {
            Assert.Equal(
                "192.168.3.31.1.1",
                amsNetId);
            Assert.Equal(851, profile.AdsPort);
            Assert.True(timeout > TimeSpan.Zero);
            return Results.Count > 0
                ? Results.Dequeue()()
                : Fallback();
        }
    }

    private sealed class AdvancingClock
        : ITcUnitExecutionClock, IClock
    {
        public AdvancingClock(
            DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; private set; }

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            cancellationToken
                .ThrowIfCancellationRequested();
            UtcNow = UtcNow.Add(delay);
            return Task.CompletedTask;
        }
    }
}
