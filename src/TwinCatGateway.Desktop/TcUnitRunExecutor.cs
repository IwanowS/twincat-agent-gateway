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
        TcUnitProfile profile,
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
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumAdsReadTimeout =
        TimeSpan.FromSeconds(3);
    private readonly ProjectProfile _profile;
    private readonly LocalLogStore _logs;
    private readonly ILogger<TcUnitRunExecutor> _logger;
    private readonly IGatewayEventSink _events;
    private readonly ITcUnitCompletionEvidenceReader
        _completionReader;
    private readonly ITcUnitExecutionClock _clock;

    public TcUnitRunExecutor(
        ProjectProfile profile,
        LocalLogStore logs,
        ILogger<TcUnitRunExecutor> logger,
        IGatewayEventSink events,
        ITcUnitCompletionEvidenceReader?
            completionReader = null,
        ITcUnitExecutionClock? clock = null)
    {
        _profile = profile
            ?? throw new ArgumentNullException(
                nameof(profile));
        _logs = logs
            ?? throw new ArgumentNullException(
                nameof(logs));
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
        string activationOperationId)
    {
        if (string.IsNullOrWhiteSpace(
            activationOperationId))
        {
            throw new ArgumentException(
                "Activation operation ID is required.",
                nameof(activationOperationId));
        }

        TcUnitProfile tcUnit = GetTcUnitProfile();
        string amsNetId = GetExpectedAmsNetId();
        TcUnitReportBaseline baseline =
            TcUnitReportFile.CaptureBaseline(
                tcUnit.ReportPath,
                tcUnit.AllowDeleteExistingReport);
        TcUnitRunPreparation preparation = new()
        {
            ActivationOperationId =
                activationOperationId,
            ExpectedAmsNetId = amsNetId,
            PreparedAtUtc = _clock.UtcNow,
            ReportBaseline = baseline,
        };
        _logger.Write(
            LogLevel.Information,
            "tcunit.prepared",
            "TcUnit report baseline captured.",
            activationOperationId,
            new Dictionary<string, string>
            {
                ["profile"] = _profile.Name,
                ["amsNetId"] = amsNetId,
                ["reportPath"] = baseline.Path,
                ["baselineExists"] =
                    baseline.Exists.ToString(),
                ["baselineDeleted"] =
                    baseline.ExistingReportDeleted
                        .ToString(),
            });
        return preparation;
    }

    public async Task<TestResult> ExecuteAsync(
        string operationId,
        string activationOperationId,
        TcUnitRunPreparation preparation,
        CancellationToken cancellationToken)
    {
        ValidatePreparation(
            activationOperationId,
            preparation);
        TcUnitProfile tcUnit = GetTcUnitProfile();
        DateTimeOffset startedAtUtc = _clock.UtcNow;
        DateTimeOffset deadlineUtc =
            startedAtUtc.AddSeconds(
                tcUnit.CompletionTimeoutSeconds);
        TcUnitCompletionReadResult completion =
            await WaitForCompletionAsync(
                operationId,
                activationOperationId,
                preparation.ExpectedAmsNetId,
                tcUnit,
                deadlineUtc,
                cancellationToken).ConfigureAwait(false);
        RecordEvent(
            operationId,
            activationOperationId,
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
            _logs.WriteText(
                operationId,
                ResourceKind.TestReport,
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
                activationOperationId,
                GatewayEventTypes.TcUnitZeroTests,
                DiagnosticSeverity.Warning,
                "tcunit.verify",
                "TcUnit report contains zero tests.");
        }

        RecordEvent(
            operationId,
            activationOperationId,
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
            ActivationOperationId =
                activationOperationId,
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
            string activationOperationId,
            string amsNetId,
            TcUnitProfile tcUnit,
            DateTimeOffset deadlineUtc,
            CancellationToken cancellationToken)
    {
        TcUnitCompletionReadResult? last = null;
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
                        == TcUnitCompletionFailureKind.None
                    && last.Finished == true
                    && last.InitializedSuites.HasValue)
                {
                    return last;
                }
            }

            await DelayUntilNextPollAsync(
                deadlineUtc,
                cancellationToken).ConfigureAwait(false);
        }

        GatewayOperationException exception =
            CreateCompletionFailure(last);
        _logger.Write(
            LogLevel.Error,
            "tcunit.completionFailed",
            exception.Message,
            operationId,
            new Dictionary<string, string>
            {
                ["activationOperationId"] =
                    activationOperationId,
                ["amsNetId"] = amsNetId,
                ["adsPort"] = tcUnit.AdsPort.ToString(
                    CultureInfo.InvariantCulture),
                ["adsError"] =
                    last?.AdsErrorCode
                    ?? "none",
                ["failedSymbol"] =
                    last?.FailedSymbol
                    ?? "none",
            },
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
        string activationOperationId,
        TcUnitRunPreparation preparation)
    {
        if (preparation is null)
        {
            throw new ArgumentNullException(
                nameof(preparation));
        }

        if (!string.Equals(
            activationOperationId,
            preparation.ActivationOperationId,
            StringComparison.Ordinal))
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "TcUnit preparation belongs to another activation.",
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

    private TcUnitProfile GetTcUnitProfile()
    {
        return _profile.TcUnit
            ?? throw new GatewayOperationException(
                ErrorCodes.ProfileInvalid,
                "The active profile has no TcUnit settings.",
                stage: "tcunit.preflight");
    }

    private string GetExpectedAmsNetId()
    {
        return _profile.ExpectedTarget?.AmsNetId
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
        string activationOperationId,
        string type,
        DiagnosticSeverity severity,
        string stage,
        string message,
        IReadOnlyDictionary<string, string>?
            extraProperties = null)
    {
        Dictionary<string, string> properties = new()
        {
            ["activationOperationId"] =
                activationOperationId,
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

        _logger.Write(
            severity == DiagnosticSeverity.Warning
                ? LogLevel.Warning
                : LogLevel.Information,
            type,
            message,
            operationId,
            properties);
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
        TcUnitProfile profile,
        TimeSpan timeout)
    {
        return _reader.Read(
            amsNetId,
            profile,
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
