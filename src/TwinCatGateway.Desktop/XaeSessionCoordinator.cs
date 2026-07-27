using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Ads;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Ipc;
using TwinCatGateway.Xae;

namespace TwinCatGateway.Desktop;

internal sealed class XaeSessionCoordinator : IDisposable
{
    private static readonly TimeSpan AttachTimeout =
        TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HealthTimeout =
        TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReconnectInterval =
        TimeSpan.FromSeconds(5);
    private readonly object _sync = new();
    private readonly ProjectProfile _profile;
    private readonly GatewayStatusSnapshotStore _status;
    private readonly StructuredFileLogger _logger;
    private readonly LocalLogStore _logs;
    private readonly IGatewayErrorSink _errors;
    private readonly XaeSession _session = new();
    private XaeSessionSnapshot _lastSnapshot = new();
    private ComDiagnostics _lastComDiagnostics = new();
    private AdsRuntimeDiagnostics _lastRuntimeDiagnostics = new();
    private string? _lastErrorMessage;
    private int? _lastHResult;
    private string? _lastFailureSignature;
    private string? _lastRuntimeFailureSignature;
    private bool _wasConnected;
    private int _disposed;

    public XaeSessionCoordinator(
        ProjectProfile profile,
        GatewayStatusSnapshotStore status,
        StructuredFileLogger logger,
        LocalLogStore logs,
        IGatewayErrorSink errors)
    {
        _profile = profile
            ?? throw new ArgumentNullException(nameof(profile));
        _status = status
            ?? throw new ArgumentNullException(nameof(status));
        _logger = logger
            ?? throw new ArgumentNullException(nameof(logger));
        _logs = logs
            ?? throw new ArgumentNullException(nameof(logs));
        _errors = errors
            ?? throw new ArgumentNullException(nameof(errors));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        bool connected = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (HasActiveChangingOperation())
            {
                await DelayAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                if (!connected)
                {
                    PublishAttaching();
                }

                XaeSessionSnapshot snapshot = connected
                    ? await _session.VerifyAttachedAsync(
                        _profile.Solution,
                        HealthTimeout,
                        cancellationToken).ConfigureAwait(false)
                    : await _session.EnsureAttachedAsync(
                        _profile.Solution,
                        _profile.AllowXaeLaunch,
                        _profile.XaeProgId,
                        AttachTimeout,
                        cancellationToken).ConfigureAwait(false);
                connected = true;
                AdsRuntimeStatusReadResult runtime =
                    ReadRuntimeStatus(snapshot);
                PublishConnected(snapshot, runtime);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                connected = false;
                XaeSessionSnapshot snapshot =
                    await ReadSnapshotAfterFailureAsync().ConfigureAwait(false);
                PublishFailure(snapshot, exception);
                await TryDisconnectAsync().ConfigureAwait(false);
            }

            await DelayAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public GatewayDiagnosticsResult CreateDiagnostics()
    {
        lock (_sync)
        {
            return new GatewayDiagnosticsResult
            {
                DteInstances = _lastSnapshot.DiscoveredInstances
                    .Select(CloneInfo)
                    .ToList(),
                Xae = new XaeDiagnostics
                {
                    SysManagerAvailable =
                        _lastSnapshot.SysManagerAvailable,
                    ActiveConfiguration =
                        _lastSnapshot.ActiveConfiguration,
                    ActivePlatform =
                        _lastSnapshot.ActivePlatform,
                    Target = CreateTarget(_lastSnapshot),
                    LastErrorMessages =
                        MergeLastErrorMessages(),
                    InspectionIssues =
                        _lastSnapshot.DiagnosticIssues.ToList(),
                    LastHResult = _lastHResult,
                },
                Com = CloneCom(_lastComDiagnostics),
                Runtime = CloneRuntime(
                    _lastRuntimeDiagnostics),
            };
        }
    }

    public async Task<BuildResult> ExecuteBuildAsync(
        string operationId,
        BuildParameters parameters,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(parameters.Profile)
            && !string.Equals(
                parameters.Profile,
                _profile.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayOperationException(
                ErrorCodes.ProfileNotFound,
                $"Project profile '{parameters.Profile}' is not active.",
                stage: "build.validate");
        }

        if (!string.IsNullOrWhiteSpace(parameters.Configuration)
            || !string.IsNullOrWhiteSpace(parameters.Platform))
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Explicit build configuration and platform selection "
                + "are not implemented yet.",
                stage: "build.validate");
        }

        TimeSpan timeout = TimeSpan.FromSeconds(
            parameters.TimeoutSeconds ?? 120);
        XaeBuildExecutionResult execution =
            await _session.ExecuteBuildAsync(
                parameters.Action,
                parameters.ChangedPaths,
                timeout,
                cancellationToken).ConfigureAwait(false);
        List<BuildDiagnostic> diagnostics =
            execution.Diagnostics.ToList();
        int errors = diagnostics.Count(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
        if (execution.FailedProjects > 0
            && errors == 0)
        {
            diagnostics.Add(
                new BuildDiagnostic
                {
                    Severity = DiagnosticSeverity.Error,
                    Source = "xae-build",
                    Message = execution.FailedProjects == 1
                        ? "One project failed to build; XAE Error List "
                            + "did not expose compiler diagnostics."
                        : $"{execution.FailedProjects} projects failed "
                            + "to build; XAE Error List did not expose "
                            + "compiler diagnostics.",
                });
            errors = 1;
        }

        int warnings = diagnostics.Count(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Warning);
        const int maximumDiagnostics = 50;
        ResourceReference log = _logs.WriteText(
            operationId,
            ResourceKind.BuildLog,
            FormatBuildOutput(execution.Output));
        ResourceReference? projectNoise = execution.ProjectChanges.Count == 0
            ? null
            : _logs.WriteText(
                operationId,
                ResourceKind.ProjectNoise,
                FormatProjectChanges(execution.ProjectChanges));
        List<ProjectChangeSummary> expectedProjectNoise =
            execution.ProjectChanges
                .Where(change =>
                    change.Classification
                        == ProjectChangeClassification
                            .ExpectedReorderOnly
                    || change.Classification
                        == ProjectChangeClassification
                            .WhitespaceOnly)
                .Select(change =>
                    new ProjectChangeSummary
                    {
                        File = change.Path,
                        Classification = change.Classification,
                        MovedBlocks = change.MovedBlocks,
                        ContentChanges = change.ContentChanges,
                        DoNotInspectFullFile = true,
                        Details = projectNoise,
                    })
                .ToList();
        XaeProjectFileChangeResult? unsupportedProjectChange =
            execution.ProjectChanges.FirstOrDefault(change =>
                change.Classification
                    == ProjectChangeClassification.ContentChanged
                || change.Classification
                    == ProjectChangeClassification.Unknown);
        if (unsupportedProjectChange is not null)
        {
            throw new GatewayOperationException(
                ErrorCodes.ExternalEditUnsupported,
                "TwinCAT project content changed during the operation "
                + $"and was classified as "
                + $"'{unsupportedProjectChange.Classification}': "
                + $"'{unsupportedProjectChange.Path}'.",
                stage: "xae.build.project-file",
                rawLogRef: projectNoise?.Uri);
        }

        BuildResult result = new()
        {
            Ok = execution.FailedProjects == 0
                && errors == 0,
            OperationId = operationId,
            Action = execution.Action,
            DurationMs = execution.DurationMs,
            Counts = new DiagnosticCounts
            {
                Errors = errors,
                Warnings = warnings,
            },
            Diagnostics = diagnostics
                .Take(maximumDiagnostics)
                .ToList(),
            MoreDiagnostics = Math.Max(
                0,
                diagnostics.Count - maximumDiagnostics),
            ExpectedProjectNoise = expectedProjectNoise,
            Log = log,
        };
        _logger.Write(
            result.Ok
                ? StructuredLogLevel.Information
                : StructuredLogLevel.Warning,
            "xae.build.completed",
            result.Ok
                ? "XAE build completed successfully."
                : "XAE build completed with errors.",
            operationId,
            properties: new Dictionary<string, string>
            {
                ["action"] = result.Action.ToString(),
                ["failedProjects"] =
                    execution.FailedProjects.ToString(
                        CultureInfo.InvariantCulture),
                ["errors"] = errors.ToString(
                    CultureInfo.InvariantCulture),
                ["warnings"] = warnings.ToString(
                    CultureInfo.InvariantCulture),
                ["synchronizedFiles"] =
                    execution.Synchronization
                        .SynchronizedDocuments.Count.ToString(
                            CultureInfo.InvariantCulture),
                ["projectNoise"] =
                    expectedProjectNoise.Count.ToString(
                        CultureInfo.InvariantCulture),
            });
        return result;
    }

    private static string FormatBuildOutput(
        IReadOnlyList<XaeOutputDelta> output)
    {
        if (output.Count == 0)
        {
            return "XAE produced no Output pane delta for this operation."
                + Environment.NewLine;
        }

        StringBuilder text = new();
        foreach (XaeOutputDelta pane in output)
        {
            text.Append("=== Output pane: ");
            text.Append(pane.PaneName);
            if (!string.IsNullOrWhiteSpace(pane.PaneGuid))
            {
                text.Append(" [");
                text.Append(pane.PaneGuid);
                text.Append(']');
            }

            text.AppendLine(" ===");
            text.Append(pane.Text);
            if (!pane.Text.EndsWith(
                Environment.NewLine,
                StringComparison.Ordinal))
            {
                text.AppendLine();
            }
        }

        return text.ToString();
    }

    private static string FormatProjectChanges(
        IReadOnlyList<XaeProjectFileChangeResult> changes)
    {
        return JsonSerializer.Serialize(
            new
            {
                SchemaVersion = 1,
                Changes = changes,
            },
            GatewayJson.CreateSerializerOptions());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _session.Dispose();
    }

    private bool HasActiveChangingOperation()
    {
        OperationSummary? operation = _status.Read().CurrentOperation;
        return operation is not null
            && (operation.State == OperationState.Queued
                || operation.State == OperationState.Running);
    }

    private void PublishAttaching()
    {
        _status.Update(status =>
        {
            if (status.Gateway.State != GatewayState.Stopping)
            {
                status.Gateway.State = GatewayState.Attaching;
            }

            return status;
        });
    }

    private void PublishConnected(
        XaeSessionSnapshot snapshot,
        AdsRuntimeStatusReadResult runtime)
    {
        ComDiagnostics diagnostics = _session.GetComDiagnostics();
        bool logConnection;
        bool logRuntimeFailure;
        lock (_sync)
        {
            _lastSnapshot = CloneSnapshot(snapshot);
            _lastComDiagnostics = CloneCom(diagnostics);
            _lastRuntimeDiagnostics =
                CloneRuntime(runtime.Diagnostics);
            logRuntimeFailure =
                runtime.Diagnostics.ErrorCode is not null
                && !string.Equals(
                    _lastRuntimeFailureSignature,
                    runtime.Diagnostics.ErrorCode,
                    StringComparison.Ordinal);
            _lastRuntimeFailureSignature =
                runtime.Diagnostics.ErrorCode;
            _lastFailureSignature = null;
            logConnection = !_wasConnected;
            _wasConnected = true;
        }

        DteInstanceInfo selected = snapshot.SelectedInstance
            ?? throw new InvalidOperationException(
                "A connected XAE snapshot has no selected instance.");
        _status.Update(status =>
        {
            if (status.Gateway.State != GatewayState.Stopping
                && status.CurrentOperation is null)
            {
                status.Gateway.State = GatewayState.Ready;
            }

            status.Xae.Connected = true;
            status.Xae.Version = selected.Version;
            status.Xae.Solution = selected.Solution;
            status.Xae.AgentWorkspaceOwned =
                snapshot.AgentWorkspaceOwned;
            status.TwinCat.Started =
                runtime.Status.Started;
            status.TwinCat.Mode =
                runtime.Status.Mode;
            return status;
        });
        if (logConnection)
        {
            _logger.Write(
                StructuredLogLevel.Information,
                "xae.connected",
                "Connected to the configured XAE solution.",
                properties: new Dictionary<string, string>
                {
                    ["processId"] =
                        selected.ProcessId?.ToString(
                            CultureInfo.InvariantCulture)
                        ?? "unknown",
                    ["progId"] = selected.ProgId ?? "unknown",
                    ["solution"] = selected.Solution ?? "unknown",
                    ["agentWorkspaceOwned"] =
                        snapshot.AgentWorkspaceOwned.ToString(),
                    ["closedDocumentCount"] =
                        snapshot.ClosedDocumentCount.ToString(
                            CultureInfo.InvariantCulture),
                    ["discardedDocumentCount"] =
                        snapshot.DiscardedDocumentCount.ToString(
                            CultureInfo.InvariantCulture),
                });
        }

        if (logRuntimeFailure)
        {
            _logger.Write(
                StructuredLogLevel.Warning,
                "ads.runtime_status.failed",
                "Could not read the selected target runtime state.",
                properties: new Dictionary<string, string>
                {
                    ["amsNetId"] =
                        runtime.Diagnostics.AmsNetId
                        ?? "unknown",
                    ["port"] =
                        runtime.Diagnostics.Port.ToString(
                            CultureInfo.InvariantCulture),
                    ["errorCode"] =
                        runtime.Diagnostics.ErrorCode
                        ?? "UNKNOWN",
                },
                exception: runtime.Failure);
            _errors.Record(
                new GatewayError
                {
                    Code = ErrorCodes.TwinCatStateUnknown,
                    Message =
                        "Could not read the selected target runtime state.",
                    Retryable = true,
                    Stage = "ads.runtimeStatus",
                },
                DateTimeOffset.UtcNow);
        }
    }

    private void PublishFailure(
        XaeSessionSnapshot snapshot,
        Exception exception)
    {
        string? code =
            (exception as GatewayOperationException)?.Code;
        string signature =
            $"{code ?? exception.GetType().FullName}|{exception.Message}";
        bool newFailure;
        int? hResult = GetMeaningfulHResult(exception);
        lock (_sync)
        {
            _lastSnapshot = CloneSnapshot(snapshot);
            _lastComDiagnostics =
                CloneCom(_session.GetComDiagnostics());
            _lastErrorMessage = exception.Message;
            _lastHResult = hResult;
            newFailure = !string.Equals(
                _lastFailureSignature,
                signature,
                StringComparison.Ordinal);
            _lastFailureSignature = signature;
            _wasConnected = false;
        }

        _status.Update(status =>
        {
            if (status.Gateway.State != GatewayState.Stopping)
            {
                status.Gateway.State =
                    code == ErrorCodes.XaeNotFound
                        ? GatewayState.Disconnected
                        : GatewayState.Faulted;
            }

            status.Xae.Connected = false;
            status.Xae.Version = null;
            status.Xae.Solution = null;
            status.Xae.AgentWorkspaceOwned = false;
            status.TwinCat.Started = null;
            status.TwinCat.Mode = RuntimeMode.Unknown;
            return status;
        });
        if (newFailure)
        {
            _logger.Write(
                StructuredLogLevel.Warning,
                "xae.connection.failed",
                "Could not establish or verify the configured XAE session.",
                properties: new Dictionary<string, string>
                {
                    ["code"] = code ?? "UNEXPECTED_EXCEPTION",
                    ["stage"] =
                        (exception as GatewayOperationException)?.Stage
                        ?? "xae.coordinator",
                    ["hresult"] = hResult is null
                        ? "unknown"
                        : $"0x{hResult.Value:X8}",
                },
                exception: exception);
            _errors.Record(
                new GatewayError
                {
                    Code = code ?? ErrorCodes.OperationFailed,
                    Message = exception.Message,
                    Retryable =
                        (exception as GatewayOperationException)?.Retryable
                        ?? true,
                    Stage =
                        (exception as GatewayOperationException)?.Stage
                        ?? "xae.coordinator",
                },
                DateTimeOffset.UtcNow);
        }
    }

    private async Task<XaeSessionSnapshot> ReadSnapshotAfterFailureAsync()
    {
        try
        {
            return await _session.GetSnapshotAsync(
                TimeSpan.FromSeconds(2),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Write(
                StructuredLogLevel.Warning,
                "xae.snapshot.failed",
                "Could not read the XAE snapshot after a session failure.",
                exception: exception);
            lock (_sync)
            {
                return CloneSnapshot(_lastSnapshot);
            }
        }
    }

    private async Task TryDisconnectAsync()
    {
        try
        {
            await _session.DisconnectAsync(
                TimeSpan.FromSeconds(2),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Write(
                StructuredLogLevel.Warning,
                "xae.disconnect.failed",
                "Could not release the failed XAE session.",
                exception: exception);
        }
    }

    private static int? GetMeaningfulHResult(Exception exception)
    {
        Exception current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current is COMException
            ? current.HResult
            : (int?)null;
    }

    private static async Task DelayAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(
                ReconnectInterval,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static XaeSessionSnapshot CloneSnapshot(
        XaeSessionSnapshot source)
    {
        return new XaeSessionSnapshot
        {
            Connected = source.Connected,
            SelectedInstance = source.SelectedInstance is null
                ? null
                : CloneInfo(source.SelectedInstance),
            SysManagerAvailable = source.SysManagerAvailable,
            LaunchedByGateway = source.LaunchedByGateway,
            AgentWorkspaceOwned = source.AgentWorkspaceOwned,
            ClosedDocumentCount = source.ClosedDocumentCount,
            DiscardedDocumentCount =
                source.DiscardedDocumentCount,
            ActiveConfiguration =
                source.ActiveConfiguration,
            ActivePlatform = source.ActivePlatform,
            TargetAmsNetId = source.TargetAmsNetId,
            LastErrorMessages =
                source.LastErrorMessages.ToArray(),
            DiagnosticIssues =
                source.DiagnosticIssues.ToArray(),
            DiscoveredInstances = source.DiscoveredInstances
                .Select(CloneInfo)
                .ToArray(),
        };
    }

    private static DteInstanceInfo CloneInfo(DteInstanceInfo source)
    {
        return new DteInstanceInfo
        {
            Moniker = source.Moniker,
            ProgId = source.ProgId,
            ProcessId = source.ProcessId,
            Version = source.Version,
            Solution = source.Solution,
            SolutionLoaded = source.SolutionLoaded,
            Selected = source.Selected,
            SelectionReason = source.SelectionReason,
            InspectionError = source.InspectionError,
            InspectionHResult = source.InspectionHResult,
        };
    }

    private static ComDiagnostics CloneCom(ComDiagnostics source)
    {
        return new ComDiagnostics
        {
            RejectedCallCount = source.RejectedCallCount,
            RetryCount = source.RetryCount,
            LastCallLatencyMs = source.LastCallLatencyMs,
            LastHResult = source.LastHResult,
        };
    }

    private static AdsRuntimeDiagnostics CloneRuntime(
        AdsRuntimeDiagnostics source)
    {
        return new AdsRuntimeDiagnostics
        {
            AmsNetId = source.AmsNetId,
            Port = source.Port,
            AdsState = source.AdsState,
            DeviceState = source.DeviceState,
            ErrorCode = source.ErrorCode,
            ReadAtUtc = source.ReadAtUtc,
        };
    }

    private static AdsRuntimeStatusReadResult ReadRuntimeStatus(
        XaeSessionSnapshot snapshot)
    {
        if (snapshot.TargetAmsNetId is null)
        {
            return new AdsRuntimeStatusReadResult(
                new TwinCatStatus
                {
                    Started = null,
                    Mode = RuntimeMode.Unknown,
                },
                new AdsRuntimeDiagnostics
                {
                    Port =
                        AdsRuntimeStatusReader.SystemServicePort,
                    ErrorCode = "TARGET_NETID_UNAVAILABLE",
                    ReadAtUtc = DateTimeOffset.UtcNow,
                });
        }

        return AdsRuntimeStatusReader.Read(
            snapshot.TargetAmsNetId,
            TimeSpan.FromSeconds(3));
    }

    private TargetIdentity? CreateTarget(
        XaeSessionSnapshot snapshot)
    {
        if (snapshot.TargetAmsNetId is null)
        {
            return null;
        }

        string? expectedAmsNetId =
            _profile.ExpectedTarget?.AmsNetId;
        return new TargetIdentity
        {
            Name = string.Equals(
                expectedAmsNetId,
                snapshot.TargetAmsNetId,
                StringComparison.OrdinalIgnoreCase)
                ? _profile.ExpectedTarget?.Name
                : null,
            AmsNetId = snapshot.TargetAmsNetId,
        };
    }

    private List<string> MergeLastErrorMessages()
    {
        IEnumerable<string> messages =
            _lastSnapshot.LastErrorMessages;
        if (_lastErrorMessage is not null)
        {
            messages = messages.Append(
                _lastErrorMessage);
        }

        return messages
            .Distinct(StringComparer.Ordinal)
            .Take(50)
            .ToList();
    }
}
