using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TwinCatGateway.Ads;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;

namespace TwinCatGateway.Desktop;

internal interface ITcUnitCompletionEvidenceReader
{
    TcUnitCompletionReadResult Read(
        string amsNetId,
        ResolvedTcUnitProfile profile,
        TimeSpan timeout);
}

internal interface ITcUnitExecutionClock
{
    DateTimeOffset UtcNow { get; }

    Task DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken);
}

internal sealed class TcUnitRunExecutor
{
    private static readonly Action<ILogger, string, Exception?> LogInformation =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1, "tcunit"),
            "{Message}");
    private static readonly Action<ILogger, string, Exception?> LogWarning =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2, "tcunit.warning"),
            "{Message}");
    private static readonly Action<ILogger, string, Exception?> LogError =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(3, "tcunit.error"),
            "{Message}");
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumAdsReadTimeout =
        TimeSpan.FromSeconds(3);
    private readonly ResolvedProfile _profile;
    private readonly TcUnitReportResourceWriter _writeReport;
    private readonly ILogger<TcUnitRunExecutor> _logger;
    private readonly IGatewayEventSink _events;
    private readonly ITcUnitCompletionEvidenceReader
        _completionReader;
    private readonly ITcUnitExecutionClock _clock;

    public TcUnitRunExecutor(
        ResolvedProfile profile,
        TcUnitReportResourceWriter writeReport,
        ILogger<TcUnitRunExecutor> logger,
        IGatewayEventSink events,
        ITcUnitCompletionEvidenceReader?
            completionReader = null,
        ITcUnitExecutionClock? clock = null)
    {
        _profile = profile
            ?? throw new ArgumentNullException(
                nameof(profile));
        _writeReport = writeReport
            ?? throw new ArgumentNullException(
                nameof(writeReport));
        _logger = logger
            ?? throw new ArgumentNullException(
                nameof(logger));
        _events = events
            ?? throw new ArgumentNullException(
                nameof(events));
        _completionReader = completionReader
            ?? TcUnitCompletionEvidenceReader.Instance;
        _clock = clock
            ?? SystemTcUnitExecutionClock.Instance;
    }

    public TcUnitRunPreparation Prepare(
        string rootOperationId)
    {
        if (string.IsNullOrWhiteSpace(
            rootOperationId))
        {
            throw new ArgumentException(
                "Root operation ID is required.",
                nameof(rootOperationId));
        }

        ResolvedTcUnitProfile tcUnit = GetTcUnitProfile();
        string amsNetId = GetExpectedAmsNetId();
        TcUnitCompletionReadResult completion =
            _completionReader.Read(
                amsNetId,
                tcUnit,
                MaximumAdsReadTimeout);
        EnsureReadableCompletionBaseline(completion);
        TcUnitReportBaseline baseline =
            TcUnitReportFile.CaptureBaseline(
                tcUnit.ReportPath,
                tcUnit.AllowDeleteExistingReport);
        TcUnitRunPreparation preparation = new()
        {
            RootOperationId = rootOperationId,
            ExpectedAmsNetId = amsNetId,
            PreparedAtUtc = _clock.UtcNow,
            ReportBaseline = baseline,
            CompletionBaseline = new TcUnitCompletionBaseline
            {
                Finished = completion.Finished!.Value,
                InitializedSuites =
                    completion.InitializedSuites!.Value,
                ReadAtUtc = completion.ReadAtUtc,
            },
        };
        LogInformation(
            _logger,
            string.Format(
                CultureInfo.InvariantCulture,
                "TcUnit baseline captured for {0}, profile {1}, target {2}, "
                    + "report {3}; completion={4}, suites={5}.",
                rootOperationId,
                _profile.Name,
                amsNetId,
                baseline.Path,
                completion.Finished.Value,
                completion.InitializedSuites.Value),
            null);
        return preparation;
    }

    public async Task<TestResult> ExecuteAsync(
        string operationId,
        TcUnitRunPreparation preparation,
        CancellationToken cancellationToken)
    {
        ValidatePreparation(
            operationId,
            preparation);
        ResolvedTcUnitProfile tcUnit = GetTcUnitProfile();
        DateTimeOffset startedAtUtc = _clock.UtcNow;
        DateTimeOffset deadlineUtc =
            startedAtUtc.AddSeconds(
                tcUnit.CompletionTimeoutSeconds);
        TcUnitCompletionReadResult completion =
            await WaitForCompletionAsync(
                operationId,
                preparation.ExpectedAmsNetId,
                tcUnit,
                preparation.CompletionBaseline,
                deadlineUtc,
                cancellationToken).ConfigureAwait(false);
        RecordEvent(
            operationId,
            GatewayEventTypes.TcUnitCompletionObserved,
            DiagnosticSeverity.Info,
            "tcunit.adsCompletion",
            "TcUnit ADS completion was observed.",
            new Dictionary<string, string>
            {
                ["amsNetId"] =
                    preparation.ExpectedAmsNetId,
                ["adsPort"] = tcUnit.AdsPort.ToString(
                    CultureInfo.InvariantCulture),
                ["initializedSuites"] =
                    completion.InitializedSuites!
                        .Value.ToString(
                            CultureInfo.InvariantCulture),
            });

        StableReport report =
            await WaitForReportAsync(
                preparation.ReportBaseline,
                deadlineUtc,
                cancellationToken).ConfigureAwait(false);
        ResourceReference reportResource =
            _writeReport(
                operationId,
                report.Xml);
        bool zeroTests =
            report.Parsed.Counts.Tests == 0;
        bool ok = report.Parsed.Counts.Failed == 0
            && (!zeroTests
                || tcUnit.ZeroTests
                    != ZeroTestsPolicy.Fail);
        if (zeroTests
            && tcUnit.ZeroTests
                == ZeroTestsPolicy.Warn)
        {
            RecordEvent(
                operationId,
                GatewayEventTypes.TcUnitZeroTests,
                DiagnosticSeverity.Warning,
                "tcunit.verify",
                "TcUnit report contains zero tests.");
        }

        RecordEvent(
            operationId,
            GatewayEventTypes.TcUnitReportProduced,
            DiagnosticSeverity.Info,
            "tcunit.report",
            "A fresh stable TcUnit report was collected.",
            new Dictionary<string, string>
            {
                ["tests"] =
                    report.Parsed.Counts.Tests.ToString(
                        CultureInfo.InvariantCulture),
                ["failed"] =
                    report.Parsed.Counts.Failed.ToString(
                        CultureInfo.InvariantCulture),
                ["report"] = reportResource.Uri,
            });
        return new TestResult
        {
            Ok = ok,
            OperationId = operationId,
            DurationMs = Math.Max(
                0,
                (long)(_clock.UtcNow - startedAtUtc)
                    .TotalMilliseconds),
            Counts = report.Parsed.Counts,
            InitializedSuites =
                completion.InitializedSuites!.Value,
            Failures =
                report.Parsed.Failures.ToList(),
            MoreFailures =
                report.Parsed.MoreFailures,
            Report = reportResource,
        };
    }

    private async Task<TcUnitCompletionReadResult>
        WaitForCompletionAsync(
            string operationId,
            string amsNetId,
            ResolvedTcUnitProfile tcUnit,
            TcUnitCompletionBaseline baseline,
            DateTimeOffset deadlineUtc,
            CancellationToken cancellationToken)
    {
        TcUnitCompletionReadResult? last = null;
        bool resetObserved = !baseline.Finished;
        while (_clock.UtcNow < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remaining =
                deadlineUtc - _clock.UtcNow;
            TimeSpan readTimeout =
                remaining < MaximumAdsReadTimeout
                    ? remaining
                    : MaximumAdsReadTimeout;
            if (readTimeout > TimeSpan.Zero)
            {
                last = _completionReader.Read(
                    amsNetId,
                    tcUnit,
                    readTimeout);
                if (last.FailureKind
                    == TcUnitCompletionFailureKind.None)
                {
                    if (last.Finished == false)
                    {
                        resetObserved = true;
                    }
                    else if (last.Finished == true
                        && resetObserved
                        && last.InitializedSuites.HasValue
                        && last.ReadAtUtc >= baseline.ReadAtUtc)
                    {
                        return last;
                    }
                }
            }

            await DelayUntilNextPollAsync(
                deadlineUtc,
                cancellationToken).ConfigureAwait(false);
        }

        GatewayOperationException exception =
            CreateCompletionFailure(last);
        LogError(
            _logger,
            string.Format(
                CultureInfo.InvariantCulture,
                "TcUnit completion failed for {0}, target {1}:{2}; "
                    + "ADS error {3}, symbol {4}.",
                operationId,
                amsNetId,
                tcUnit.AdsPort,
                last?.AdsErrorCode ?? "none",
                last?.FailedSymbol ?? "none"),
            exception);
        throw exception;
    }

    private async Task<StableReport>
        WaitForReportAsync(
            TcUnitReportBaseline baseline,
            DateTimeOffset deadlineUtc,
            CancellationToken cancellationToken)
    {
        TcUnitReportSnapshot? previous = null;
        GatewayOperationException? lastInvalid = null;
        bool freshSeen = false;
        while (_clock.UtcNow < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                TcUnitReportSnapshot current =
                    TcUnitReportFile.Capture(
                        baseline.Path);
                if (TcUnitReportFile.IsFresh(
                    baseline,
                    current))
                {
                    freshSeen = true;
                    if (previous is not null
                        && TcUnitReportFile
                            .HasSameFileState(
                                previous,
                                current))
                    {
                        string xml =
                            TcUnitReportFile.ReadAllText(
                                current);
                        TcUnitReportSnapshot afterRead =
                            TcUnitReportFile.Capture(
                                baseline.Path);
                        if (TcUnitReportFile
                            .HasSameFileState(
                                current,
                                afterRead))
                        {
                            try
                            {
                                return new StableReport(
                                    xml,
                                    TcUnitReportParser
                                        .Parse(xml));
                            }
                            catch (
                                GatewayOperationException
                                exception) when (
                                    exception.Code
                                    == ErrorCodes
                                        .TestReportInvalid)
                            {
                                lastInvalid = exception;
                            }
                        }
                    }

                    previous = current;
                }
                else
                {
                    previous = null;
                }
            }
            catch (IOException)
            {
                previous = null;
            }
            catch (UnauthorizedAccessException)
            {
                previous = null;
            }

            await DelayUntilNextPollAsync(
                deadlineUtc,
                cancellationToken).ConfigureAwait(false);
        }

        if (freshSeen
            && lastInvalid is not null)
        {
            throw lastInvalid;
        }

        throw new GatewayOperationException(
            ErrorCodes.TestReportNotProduced,
            "TcUnit did not produce a fresh stable report.",
            retryable: true,
            stage: "tcunit.report");
    }

    private async Task DelayUntilNextPollAsync(
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        TimeSpan remaining =
            deadlineUtc - _clock.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan delay = remaining < PollInterval
            ? remaining
            : PollInterval;
        await _clock.DelayAsync(
            delay,
            cancellationToken).ConfigureAwait(false);
    }

    private void ValidatePreparation(
        string operationId,
        TcUnitRunPreparation preparation)
    {
        if (preparation is null)
        {
            throw new ArgumentNullException(
                nameof(preparation));
        }

        if (!string.Equals(
            operationId,
            preparation.RootOperationId,
            StringComparison.Ordinal))
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "TcUnit preparation belongs to another root operation.",
                stage: "tcunit.preflight");
        }

        if (!string.Equals(
            GetExpectedAmsNetId(),
            preparation.ExpectedAmsNetId,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayOperationException(
                ErrorCodes.ActivationTargetMismatch,
                "TcUnit target AMS NetId changed after activation.",
                stage: "tcunit.preflight");
        }
    }

    private static void EnsureReadableCompletionBaseline(
        TcUnitCompletionReadResult completion)
    {
        if (completion.FailureKind == TcUnitCompletionFailureKind.None
            && completion.Finished.HasValue
            && completion.InitializedSuites.HasValue)
        {
            return;
        }

        throw CreateCompletionFailure(completion);
    }

    private ResolvedTcUnitProfile GetTcUnitProfile()
    {
        return _profile.Target?.TcUnit
            ?? throw new GatewayOperationException(
                ErrorCodes.ProfileInvalid,
                "The active profile has no TcUnit settings.",
                stage: "tcunit.preflight");
    }

    private string GetExpectedAmsNetId()
    {
        return _profile.Target?.AmsNetId
            ?? throw new GatewayOperationException(
                ErrorCodes.ProfileInvalid,
                "The active profile has no expected AMS NetId.",
                stage: "tcunit.preflight");
    }

    private static GatewayOperationException
        CreateCompletionFailure(
            TcUnitCompletionReadResult? last)
    {
        switch (last?.FailureKind)
        {
            case TcUnitCompletionFailureKind
                .CompletionSymbolUnavailable:
            case TcUnitCompletionFailureKind
                .SuiteCountSymbolUnavailable:
                return new GatewayOperationException(
                    ErrorCodes
                        .TestCompletionSymbolUnavailable,
                    "A configured TcUnit completion symbol "
                        + "is unavailable.",
                    retryable: true,
                    stage: "tcunit.adsCompletion");
            case TcUnitCompletionFailureKind
                .AdsUnavailable:
                return new GatewayOperationException(
                    ErrorCodes.TestAdsUnavailable,
                    "TcUnit ADS completion evidence "
                        + "is unavailable.",
                    retryable: true,
                    stage: "tcunit.adsCompletion");
            default:
                return new GatewayOperationException(
                    ErrorCodes.TestCompletionTimeout,
                    "TcUnit completion was not observed "
                        + "before the deadline.",
                    retryable: true,
                    stage: "tcunit.adsCompletion");
        }
    }

    private void RecordEvent(
        string operationId,
        string type,
        DiagnosticSeverity severity,
        string stage,
        string message,
        IReadOnlyDictionary<string, string>?
            extraProperties = null)
    {
        Dictionary<string, string> properties = new()
        {
            ["rootOperationId"] = operationId,
            ["profile"] = _profile.Name,
            ["amsNetId"] = GetExpectedAmsNetId(),
        };
        if (extraProperties is not null)
        {
            foreach (
                KeyValuePair<string, string> pair
                in extraProperties)
            {
                properties[pair.Key] = pair.Value;
            }
        }

        Action<ILogger, string, Exception?> writeLog =
            severity == DiagnosticSeverity.Warning
                ? LogWarning
                : LogInformation;
        writeLog(
            _logger,
            string.Format(
                CultureInfo.InvariantCulture,
                "{0}: {1}; operation={2}",
                type,
                message,
                operationId),
            null);
        _events.Record(
            new GatewayEvent
            {
                Type = type,
                Severity = severity,
                OperationId = operationId,
                OperationKind = OperationKind.Test,
                Stage = stage,
                Message = message,
                Properties = properties,
            },
            _clock.UtcNow);
    }

    private sealed class StableReport
    {
        public StableReport(
            string xml,
            TcUnitReportParseResult parsed)
        {
            Xml = xml;
            Parsed = parsed;
        }

        public string Xml { get; }

        public TcUnitReportParseResult Parsed { get; }
    }
}

internal sealed class TcUnitCompletionEvidenceReader
    : ITcUnitCompletionEvidenceReader
{
    private readonly TcUnitCompletionReader _reader =
        new();

    public static readonly TcUnitCompletionEvidenceReader
        Instance = new();

    private TcUnitCompletionEvidenceReader()
    {
    }

    public TcUnitCompletionReadResult Read(
        string amsNetId,
        ResolvedTcUnitProfile profile,
        TimeSpan timeout)
    {
        return _reader.Read(
            amsNetId,
            new TcUnitProfile
            {
                RuntimeId = profile.RuntimeId,
                AdsPort = profile.AdsPort,
                FinishedSymbol = profile.FinishedSymbol,
                SuiteCountSymbol = profile.SuiteCountSymbol,
                ReportPath = profile.ReportPath,
                AllowDeleteExistingReport =
                    profile.AllowDeleteExistingReport,
                CompletionTimeoutSeconds =
                    profile.CompletionTimeoutSeconds,
                ZeroTests = profile.ZeroTests,
            },
            timeout);
    }
}

internal sealed class SystemTcUnitExecutionClock
    : ITcUnitExecutionClock
{
    public static readonly SystemTcUnitExecutionClock
        Instance = new();

    private SystemTcUnitExecutionClock()
    {
    }

    public DateTimeOffset UtcNow =>
        DateTimeOffset.UtcNow;

    public Task DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        return Task.Delay(delay, cancellationToken);
    }
}
