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
    private readonly IGatewayEventSink _events;
    private readonly XaeSession _session = new();
    private readonly TcUnitRunExecutor _tcUnit;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private XaeSessionSnapshot _lastSnapshot = new();
    private ComDiagnostics _lastComDiagnostics = new();
    private AdsRuntimeDiagnostics _lastRuntimeDiagnostics = new();
    private string? _lastErrorMessage;
    private int? _lastHResult;
    private string? _lastFailureSignature;
    private string? _lastRuntimeFailureSignature;
    private string? _lastRuntimeStateSignature;
    private bool _wasConnected;
    private int _reconnectRequested;
    private int _disposed;

    public XaeSessionCoordinator(
        ProjectProfile profile,
        GatewayStatusSnapshotStore status,
        StructuredFileLogger logger,
        LocalLogStore logs,
        IGatewayEventSink events)
    {
        _profile = profile
            ?? throw new ArgumentNullException(nameof(profile));
        _status = status
            ?? throw new ArgumentNullException(nameof(status));
        _logger = logger
            ?? throw new ArgumentNullException(nameof(logger));
        _logs = logs
            ?? throw new ArgumentNullException(nameof(logs));
        _events = events
            ?? throw new ArgumentNullException(nameof(events));
        _tcUnit = new TcUnitRunExecutor(
            _profile,
            _logs,
            _logger,
            _events);
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

            if (Interlocked.Exchange(
                    ref _reconnectRequested,
                    0)
                != 0)
            {
                connected = false;
                await TryDisconnectAsync().ConfigureAwait(false);
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

    public async Task<ActivationResult> ExecuteActivationAsync(
        string operationId,
        ActivateParameters parameters,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
            parameters.Profile,
            _profile.Name,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayOperationException(
                ErrorCodes.ProfileNotFound,
                $"Project profile '{parameters.Profile}' is not active.",
                stage: "activation.validate");
        }

        if (!_profile.AllowActivation)
        {
            throw new GatewayOperationException(
                ErrorCodes.ActivationNotAllowed,
                $"Activation is disabled for profile '{_profile.Name}'.",
                stage: "activation.validate");
        }

        string expectedAmsNetId =
            _profile.ExpectedTarget?.AmsNetId
            ?? throw new GatewayOperationException(
                ErrorCodes.ProfileInvalid,
                "The activation profile has no expected AMS NetId.",
                stage: "activation.validate");
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        DateTimeOffset deadlineUtc = startedAtUtc.AddSeconds(
            parameters.TimeoutSeconds ?? 120);
        XaeSessionSnapshot snapshot =
            await _session.VerifyAttachedAsync(
                _profile.Solution,
                GetRemaining(
                    deadlineUtc,
                    "activation.preflight"),
                cancellationToken).ConfigureAwait(false);
        VerifyTarget(
            snapshot,
            expectedAmsNetId,
            "activation.preflight");
        AdsRuntimeStatusReadResult runtime =
            ReadRuntimeStatus(snapshot);
        if (runtime.Status.Mode == RuntimeMode.Unknown)
        {
            throw new GatewayOperationException(
                ErrorCodes.TwinCatStateUnknown,
                "Activation requires a readable TwinCAT runtime state.",
                retryable: true,
                stage: "activation.preflight");
        }

        bool recoveryAttempted =
            runtime.Status.Mode == RuntimeMode.Exception;
        if (recoveryAttempted)
        {
            RecordActivationEvent(
                operationId,
                GatewayEventTypes.ActivationRecoveryStarted,
                "activation.recoverToConfig",
                "TwinCAT Config Mode recovery started.",
                expectedAmsNetId);
            await _session.RestartTwinCatConfigModeAsync(
                _profile.Solution,
                expectedAmsNetId,
                GetRemaining(
                    deadlineUtc,
                    "activation.recoverToConfig"),
                cancellationToken).ConfigureAwait(false);
            runtime = await WaitForRuntimeModeAsync(
                expectedAmsNetId,
                RuntimeMode.Config,
                deadlineUtc,
                ErrorCodes.ConfigModeRecoveryFailed,
                "TwinCAT did not reach Config Mode after recovery.",
                cancellationToken).ConfigureAwait(false);
            PublishConnected(snapshot, runtime);
            RecordActivationEvent(
                operationId,
                GatewayEventTypes.ActivationRecoverySucceeded,
                "activation.recoverToConfig",
                "TwinCAT reached Config Mode.",
                expectedAmsNetId);
        }

        RecordActivationEvent(
            operationId,
            GatewayEventTypes.ActivationConfigurationStarted,
            "activation.activateConfiguration",
            "TwinCAT configuration activation started.",
            expectedAmsNetId);
        await _session.ActivateConfigurationAsync(
            _profile.Solution,
            expectedAmsNetId,
            GetRemaining(
                deadlineUtc,
                "activation.activateConfiguration"),
            cancellationToken).ConfigureAwait(false);
        RecordActivationEvent(
            operationId,
            GatewayEventTypes.ActivationConfigurationActivated,
            "activation.activateConfiguration",
            "TwinCAT configuration was activated.",
            expectedAmsNetId);

        RecordActivationEvent(
            operationId,
            GatewayEventTypes.ActivationRestartStarted,
            "activation.restart",
            "TwinCAT restart started.",
            expectedAmsNetId);
        await _session.StartRestartTwinCatAsync(
            _profile.Solution,
            expectedAmsNetId,
            GetRemaining(
                deadlineUtc,
                "activation.restart"),
            cancellationToken).ConfigureAwait(false);
        RecordActivationEvent(
            operationId,
            GatewayEventTypes.ActivationRestartRequested,
            "activation.restart",
            "TwinCAT restart was requested.",
            expectedAmsNetId);

        runtime = await WaitForRuntimeModeAsync(
            expectedAmsNetId,
            RuntimeMode.Run,
            deadlineUtc,
            ErrorCodes.TwinCatRestartFailed,
            "TwinCAT did not reach Run after restart.",
            cancellationToken).ConfigureAwait(false);
        snapshot = await _session.VerifyAttachedAsync(
            _profile.Solution,
            GetRemaining(
                deadlineUtc,
                "activation.verify"),
            cancellationToken).ConfigureAwait(false);
        VerifyTarget(
            snapshot,
            expectedAmsNetId,
            "activation.verify");
        PublishConnected(snapshot, runtime);
        RecordActivationEvent(
            operationId,
            GatewayEventTypes.ActivationRuntimeReady,
            "activation.verify",
            "TwinCAT runtime reached Run.",
            expectedAmsNetId);

        long durationMs = Math.Max(
            0,
            (long)(DateTimeOffset.UtcNow - startedAtUtc)
                .TotalMilliseconds);
        ResourceReference log = _logs.WriteText(
            operationId,
            ResourceKind.ActivationLog,
            FormatActivationLog(
                operationId,
                expectedAmsNetId,
                recoveryAttempted,
                runtime,
                durationMs));
        ActivationResult result = new()
        {
            Ok = true,
            OperationId = operationId,
            DurationMs = durationMs,
            Profile = _profile.Name,
            Solution = _profile.Solution,
            Target = new TargetIdentity
            {
                Name = _profile.ExpectedTarget?.Name,
                AmsNetId = expectedAmsNetId,
            },
            RecoveryAttempted = recoveryAttempted,
            Resources =
            {
                log,
            },
        };
        _logger.Write(
            StructuredLogLevel.Information,
            "activation.completed",
            "TwinCAT activation completed successfully.",
            operationId,
            properties: new Dictionary<string, string>
            {
                ["profile"] = _profile.Name,
                ["solution"] = _profile.Solution,
                ["amsNetId"] = expectedAmsNetId,
                ["recoveryAttempted"] =
                    recoveryAttempted.ToString(),
                ["durationMs"] = durationMs.ToString(
                    CultureInfo.InvariantCulture),
            });
        return result;
    }

    public TcUnitRunPreparation PrepareTcUnitRun(
        string activationOperationId)
    {
        return _tcUnit.Prepare(
            activationOperationId);
    }

    public void RequestReconnect()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(
                nameof(XaeSessionCoordinator));
        }

        Interlocked.Exchange(ref _reconnectRequested, 1);
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pending wake-up already represents this request.
        }

        _logger.Write(
            StructuredLogLevel.Information,
            "xae.reconnect.requested",
            "Manual XAE reconnect requested.");
        _events.Record(
            new GatewayEvent
            {
                Type =
                    GatewayEventTypes.XaeReconnectRequested,
                Severity = DiagnosticSeverity.Info,
                Stage = "xae.reconnect",
                Message = "Manual XAE reconnect requested.",
            },
            DateTimeOffset.UtcNow);
    }

    public async Task<TestResult> ExecuteTcUnitAsync(
        string operationId,
        string activationOperationId,
        TcUnitRunPreparation preparation,
        CancellationToken cancellationToken)
    {
        XaeSessionSnapshot snapshot =
            await _session.VerifyAttachedAsync(
                _profile.Solution,
                HealthTimeout,
                cancellationToken).ConfigureAwait(false);
        VerifyTarget(
            snapshot,
            preparation.ExpectedAmsNetId,
            "tcunit.preflight");
        return await _tcUnit.ExecuteAsync(
            operationId,
            activationOperationId,
            preparation,
            cancellationToken).ConfigureAwait(false);
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
        _wakeSignal.Dispose();
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
        bool publishRuntimeState;
        string runtimeStateSignature = CreateRuntimeStateSignature(
            runtime);
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
            publishRuntimeState = !string.Equals(
                _lastRuntimeStateSignature,
                runtimeStateSignature,
                StringComparison.Ordinal);
            _lastRuntimeStateSignature =
                runtimeStateSignature;
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
            status.Xae.DiscardedDocumentCount =
                snapshot.DiscardedDocumentCount;
            status.TwinCat.Started =
                runtime.Status.Started;
            status.TwinCat.Mode =
                runtime.Status.Mode;
            return status;
        });
        if (logConnection)
        {
            Dictionary<string, string> properties = new()
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
            };
            _logger.Write(
                StructuredLogLevel.Information,
                "xae.connected",
                "Connected to the configured XAE solution.",
                properties: properties);
            _events.Record(
                new GatewayEvent
                {
                    Type = GatewayEventTypes.XaeConnected,
                    Severity = DiagnosticSeverity.Info,
                    Stage = "xae.attach",
                    Message =
                        "Connected to the configured XAE solution.",
                    Properties = properties,
                },
                DateTimeOffset.UtcNow);
        }

        if (publishRuntimeState)
        {
            _events.Record(
                CreateRuntimeStateEvent(runtime),
                DateTimeOffset.UtcNow);
        }

        if (logRuntimeFailure)
        {
            Dictionary<string, string> properties =
                CreateRuntimeProperties(runtime);
            _logger.Write(
                StructuredLogLevel.Warning,
                "ads.runtime_status.failed",
                "Could not read the selected target runtime state.",
                properties: properties,
                exception: runtime.Failure);
            GatewayError error = new()
            {
                Code = ErrorCodes.TwinCatStateUnknown,
                Message =
                    "Could not read the selected target runtime state.",
                Retryable = true,
                Stage = "ads.runtimeStatus",
            };
            _events.Record(
                CreateErrorEvent(
                    GatewayEventTypes.RuntimeStatusReadFailed,
                    error,
                    properties),
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
        bool wasConnected;
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
            wasConnected = _wasConnected;
            _wasConnected = false;
            _lastRuntimeStateSignature = null;
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
            status.Xae.DiscardedDocumentCount = 0;
            status.TwinCat.Started = null;
            status.TwinCat.Mode = RuntimeMode.Unknown;
            return status;
        });
        if (wasConnected)
        {
            _events.Record(
                new GatewayEvent
                {
                    Type = GatewayEventTypes.XaeDisconnected,
                    Severity = DiagnosticSeverity.Warning,
                    Stage = "xae.verify",
                    Message =
                        "The configured XAE session disconnected.",
                },
                DateTimeOffset.UtcNow);
        }

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
            GatewayError error = new()
            {
                Code = code ?? ErrorCodes.OperationFailed,
                Message = exception.Message,
                Retryable =
                    (exception as GatewayOperationException)?.Retryable
                    ?? true,
                Stage =
                    (exception as GatewayOperationException)?.Stage
                    ?? "xae.coordinator",
            };
            _events.Record(
                CreateErrorEvent(
                    GatewayEventTypes.XaeConnectionFailed,
                    error),
                DateTimeOffset.UtcNow);
        }
    }

    private static GatewayEvent CreateErrorEvent(
        string type,
        GatewayError error,
        Dictionary<string, string>? properties = null)
    {
        return new GatewayEvent
        {
            Type = type,
            Severity = DiagnosticSeverity.Error,
            Stage = error.Stage,
            Message = error.Message,
            Error = error,
            Properties = properties
                ?? new Dictionary<string, string>(),
        };
    }

    private static GatewayEvent CreateRuntimeStateEvent(
        AdsRuntimeStatusReadResult runtime)
    {
        DiagnosticSeverity severity;
        switch (runtime.Status.Mode)
        {
            case RuntimeMode.Exception:
                severity = DiagnosticSeverity.Error;
                break;
            case RuntimeMode.Unknown:
                severity = DiagnosticSeverity.Warning;
                break;
            default:
                severity = DiagnosticSeverity.Info;
                break;
        }

        return new GatewayEvent
        {
            Type = GatewayEventTypes.RuntimeStateChanged,
            Severity = severity,
            Stage = "ads.runtimeStatus",
            Message =
                $"TwinCAT runtime state changed to {runtime.Status.Mode}.",
            Properties = CreateRuntimeProperties(runtime),
        };
    }

    private static Dictionary<string, string>
        CreateRuntimeProperties(
            AdsRuntimeStatusReadResult runtime)
    {
        return new Dictionary<string, string>
        {
            ["amsNetId"] =
                runtime.Diagnostics.AmsNetId ?? "unknown",
            ["port"] = runtime.Diagnostics.Port.ToString(
                CultureInfo.InvariantCulture),
            ["started"] =
                runtime.Status.Started?.ToString()
                ?? "unknown",
            ["mode"] = runtime.Status.Mode.ToString(),
            ["adsState"] =
                runtime.Diagnostics.AdsState ?? "unknown",
            ["deviceState"] =
                runtime.Diagnostics.DeviceState?.ToString(
                    CultureInfo.InvariantCulture)
                ?? "unknown",
            ["errorCode"] =
                runtime.Diagnostics.ErrorCode ?? "none",
        };
    }

    private static string CreateRuntimeStateSignature(
        AdsRuntimeStatusReadResult runtime)
    {
        return string.Join(
            "|",
            runtime.Status.Started?.ToString() ?? "unknown",
            runtime.Status.Mode.ToString(),
            runtime.Diagnostics.AdsState ?? "unknown",
            runtime.Diagnostics.DeviceState?.ToString(
                CultureInfo.InvariantCulture)
                ?? "unknown",
            runtime.Diagnostics.ErrorCode ?? "none");
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

    private async Task DelayAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await _wakeSignal.WaitAsync(
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

    private void RecordActivationEvent(
        string operationId,
        string type,
        string stage,
        string message,
        string amsNetId)
    {
        Dictionary<string, string> properties = new()
        {
            ["profile"] = _profile.Name,
            ["solution"] = _profile.Solution,
            ["amsNetId"] = amsNetId,
        };
        string? targetName = _profile.ExpectedTarget?.Name;
        if (!string.IsNullOrWhiteSpace(targetName))
        {
            properties["targetName"] = targetName!;
        }
        _logger.Write(
            StructuredLogLevel.Information,
            type,
            message,
            operationId,
            properties);
        _events.Record(
            new GatewayEvent
            {
                Type = type,
                Severity = DiagnosticSeverity.Info,
                OperationId = operationId,
                OperationKind = OperationKind.Activate,
                Stage = stage,
                Message = message,
                Properties = properties,
            },
            DateTimeOffset.UtcNow);
    }

    private static async Task<AdsRuntimeStatusReadResult>
        WaitForRuntimeModeAsync(
            string amsNetId,
            RuntimeMode expectedMode,
            DateTimeOffset deadlineUtc,
            string errorCode,
            string errorMessage,
            CancellationToken cancellationToken)
    {
        while (DateTimeOffset.UtcNow < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remaining =
                deadlineUtc - DateTimeOffset.UtcNow;
            TimeSpan readTimeout = remaining
                < TimeSpan.FromSeconds(3)
                ? remaining
                : TimeSpan.FromSeconds(3);
            if (readTimeout > TimeSpan.Zero)
            {
                AdsRuntimeStatusReadResult runtime =
                    AdsRuntimeStatusReader.Read(
                        amsNetId,
                        readTimeout);
                if (runtime.Diagnostics.ErrorCode is null
                    && runtime.Status.Mode == expectedMode)
                {
                    return runtime;
                }
            }

            remaining = deadlineUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            TimeSpan delay = remaining
                < TimeSpan.FromMilliseconds(250)
                ? remaining
                : TimeSpan.FromMilliseconds(250);
            await Task.Delay(
                delay,
                cancellationToken).ConfigureAwait(false);
        }

        throw new GatewayOperationException(
            errorCode,
            errorMessage,
            retryable: true,
            stage: expectedMode == RuntimeMode.Config
                ? "activation.recoverToConfig"
                : "activation.verify");
    }

    private static void VerifyTarget(
        XaeSessionSnapshot snapshot,
        string expectedAmsNetId,
        string stage)
    {
        if (!string.Equals(
            snapshot.TargetAmsNetId,
            expectedAmsNetId,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayOperationException(
                ErrorCodes.ActivationTargetMismatch,
                "The selected XAE target AMS NetId does not match "
                    + "the activation profile.",
                stage: stage);
        }
    }

    private static TimeSpan GetRemaining(
        DateTimeOffset deadlineUtc,
        string stage)
    {
        TimeSpan remaining =
            deadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            throw new GatewayOperationException(
                ErrorCodes.OperationTimeout,
                "The activation operation exceeded its deadline.",
                retryable: true,
                stage: stage);
        }

        return remaining;
    }

    private string FormatActivationLog(
        string operationId,
        string amsNetId,
        bool recoveryAttempted,
        AdsRuntimeStatusReadResult runtime,
        long durationMs)
    {
        StringBuilder builder = new();
        builder.AppendLine($"OperationId: {operationId}");
        builder.AppendLine($"Profile: {_profile.Name}");
        builder.AppendLine($"Solution: {_profile.Solution}");
        builder.AppendLine($"TargetAmsNetId: {amsNetId}");
        string? targetName = _profile.ExpectedTarget?.Name;
        if (!string.IsNullOrWhiteSpace(targetName))
        {
            builder.AppendLine($"TargetName: {targetName}");
        }
        builder.AppendLine(
            $"RecoveryAttempted: {recoveryAttempted}");
        builder.AppendLine(
            $"FinalRuntimeMode: {runtime.Status.Mode}");
        builder.AppendLine(
            $"FinalAdsState: "
                + $"{runtime.Diagnostics.AdsState ?? "unknown"}");
        builder.AppendLine($"DurationMs: {durationMs}");
        return builder.ToString();
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
