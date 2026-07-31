using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TwinCatGateway.Ads;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Ipc;
using TwinCatGateway.Xae;
using XaeSessionSnapshot = TwinCatGateway.Xae.XaeSessionSnapshot;

namespace TwinCatGateway.Desktop;

internal sealed class XaeSessionCoordinator : IDisposable
{
    private const int RpcECallRejected =
        unchecked((int)0x80010001);
    private const int RpcEServerCallRetryLater =
        unchecked((int)0x8001010A);
    private static readonly TimeSpan AttachTimeout =
        TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HealthTimeout =
        TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReconnectInterval =
        TimeSpan.FromSeconds(5);
    private readonly object _sync = new();
    private readonly ProjectProfile _profile;
    private readonly GatewayStatusSnapshotStore _status;
    private readonly ILogger<XaeSessionCoordinator> _logger;
    private readonly LocalLogStore _logs;
    private readonly IGatewayEventSink _events;
    private readonly AdsRuntimeMonitor _runtimeMonitor;
    private readonly XaeErrorListSnapshotStore
        _errorListSnapshots;
    private readonly XaeSession _session = new();
    private readonly TcUnitRunExecutor _tcUnit;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private XaeSessionSnapshot _lastSnapshot = new();
    private ComDiagnostics _lastComDiagnostics = new();
    private string? _lastErrorMessage;
    private int? _lastHResult;
    private string? _lastFailureSignature;
    private bool _wasConnected;
    private int _autoLaunchSuppressed;
    private int _reconnectRequested;
    private int _disposed;

    public XaeSessionCoordinator(
        ProjectProfile profile,
        GatewayStatusSnapshotStore status,
        ILogger<XaeSessionCoordinator> logger,
        ILogger<TcUnitRunExecutor> tcUnitLogger,
        LocalLogStore logs,
        IGatewayEventSink events,
        AdsRuntimeMonitor runtimeMonitor,
        XaeErrorListSnapshotStore errorListSnapshots)
    {
        _profile = profile
            ?? throw new ArgumentNullException(nameof(profile));
        _status = status
            ?? throw new ArgumentNullException(nameof(status));
        _logger = logger
            ?? throw new ArgumentNullException(nameof(logger));
        if (tcUnitLogger is null)
        {
            throw new ArgumentNullException(nameof(tcUnitLogger));
        }

        _logs = logs
            ?? throw new ArgumentNullException(nameof(logs));
        _events = events
            ?? throw new ArgumentNullException(nameof(events));
        _runtimeMonitor = runtimeMonitor
            ?? throw new ArgumentNullException(
                nameof(runtimeMonitor));
        _errorListSnapshots = errorListSnapshots
            ?? throw new ArgumentNullException(
                nameof(errorListSnapshots));
        _tcUnit = new TcUnitRunExecutor(
            _profile,
            _logs,
            tcUnitLogger,
            _events);
        _session.DialogObserved += OnDialogObserved;
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
                        _profile.AllowXaeLaunch
                            && Volatile.Read(
                                ref _autoLaunchSuppressed) == 0,
                        _profile.XaeProgId,
                        _profile.AssumeAttachedXaeSynchronized,
                        AttachTimeout,
                        cancellationToken).ConfigureAwait(false);
                snapshot = _session.RefreshSynchronizationStatus(
                    cancellationToken);
                connected = true;
                PublishConnected(snapshot);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                bool retainAttachment =
                    ShouldRetainAttachmentAfterFailure(
                        connected,
                        exception);
                XaeSessionSnapshot snapshot =
                    await ReadSnapshotAfterFailureAsync().ConfigureAwait(false);
                PublishFailure(
                    snapshot,
                    exception,
                    retainAttachment);
                if (!retainAttachment)
                {
                    connected = false;
                    await TryDisconnectAsync().ConfigureAwait(false);
                }
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
                    UnsynchronizedFiles =
                        _lastSnapshot.UnsynchronizedFiles
                            .Select(CreateUnsynchronizedFileInfo)
                            .ToList(),
                },
                Com = CloneCom(_lastComDiagnostics),
                Runtime =
                    _runtimeMonitor.GetSystemDiagnostics(),
                PlcRuntimes = _runtimeMonitor
                    .GetPlcDiagnostics()
                    .ToList(),
            };
        }
    }

    public async Task<XaeMessagesResult>
        ReadXaeMessagesAsync(
            GetXaeMessagesParameters parameters,
            CancellationToken cancellationToken)
    {
        if (parameters is null)
        {
            throw new ArgumentNullException(
                nameof(parameters));
        }

        XaeSessionSnapshot snapshot =
            await _session.VerifyAttachedAsync(
                _profile.Solution,
                HealthTimeout,
                cancellationToken).ConfigureAwait(false);
        IReadOnlyList<BuildDiagnostic> messages =
            await _session.ReadErrorListAsync(
                HealthTimeout,
                cancellationToken).ConfigureAwait(false);
        snapshot.ErrorListMessages = messages;
        _errorListSnapshots.Replace(messages);
        PublishConnected(snapshot);

        BuildDiagnostic[] selected = messages
            .Where(message =>
                message.Severity == DiagnosticSeverity.Error
                || message.Severity
                    == DiagnosticSeverity.Warning)
            .ToArray();
        return new XaeMessagesResult
        {
            Solution = snapshot.SelectedInstance?.Solution
                ?? _profile.Solution,
            ReadAtUtc = DateTimeOffset.UtcNow,
            Counts = new DiagnosticCounts
            {
                Errors = selected.Count(message =>
                    message.Severity
                        == DiagnosticSeverity.Error),
                Warnings = selected.Count(message =>
                    message.Severity
                        == DiagnosticSeverity.Warning),
            },
            Messages = selected
                .Take(parameters.MaximumMessages)
                .Select(CloneBuildDiagnostic)
                .ToList(),
            MoreMessages = Math.Max(
                0,
                selected.Length
                    - parameters.MaximumMessages),
        };
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

        string? configuration = ResolveBuildSetting(
            parameters.Configuration,
            _profile.Configuration,
            "configuration");
        string? platform = ResolveBuildSetting(
            parameters.Platform,
            _profile.Platform,
            "platform");

        DateTimeOffset deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(
            parameters.TimeoutSeconds ?? 120);
        XaeBuildExecutionResult execution;
        using XaeDialogOperationScope dialogScope =
            _session.BeginDialogOperation(
                operationId,
                parameters.Action.ToString().ToLowerInvariant(),
                "build.runtimePreflight");
        try
        {
            XaeSessionSnapshot snapshot =
                await dialogScope.ObserveAsync(
                    _session.VerifyAttachedAsync(
                        _profile.Solution,
                        GetRemaining(
                            deadlineUtc,
                            "build.runtimePreflight"),
                        cancellationToken)).ConfigureAwait(false);
            AdsRuntimeStatusReadResult runtime =
                ReadRuntimeStatus(snapshot);
            string? runtimeExceptionDetails =
                XaeRuntimeExceptionDetails.Select(
                    snapshot.ErrorListMessages);
            if (runtime.Status.Mode
                    == RuntimeMode.Exception
                && runtimeExceptionDetails is null)
            {
                runtimeExceptionDetails =
                    await ReadRuntimeExceptionDetailsAsync(
                        GetRemaining(
                            deadlineUtc,
                            "build.runtimePreflight"),
                        cancellationToken)
                        .ConfigureAwait(false);
            }

            RuntimeOperationPolicy.EnsureBuildAllowed(
                runtime.Status.Mode,
                runtimeExceptionDetails);

            dialogScope.SetStage("xae.build");
            execution = await dialogScope.ObserveAsync(
                _session.ExecuteBuildAsync(
                    parameters.Action,
                    parameters.ChangedPaths,
                    configuration,
                    platform,
                    _profile.ExternalChangePolicy,
                    parameters.DiscardDirtyDocuments,
                    _profile.AllowDirtyDocumentDiscard,
                    _profile.AutoSynchronizeBeforeOperation,
                    GetRemaining(deadlineUtc, "xae.build"),
                    cancellationToken))
                .ConfigureAwait(false);
        }
        catch (GatewayOperationException exception) when (
            RequiresSynchronization(exception.Code)
            || string.Equals(
                exception.Stage,
                "xae.workspace.settle",
                StringComparison.Ordinal))
        {
            PublishSynchronizationRequired();
            throw;
        }
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
                            .ExpectedGeneratedArtifact
                    || change.Classification
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
        LogAcceptedProjectChanges(
            operationId,
            OperationKind.Build,
            "xae.build.settle",
            execution.AcceptedProjectChanges);

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
            DiscardedDocuments =
                execution.Synchronization
                    .DiscardedDocuments.ToList(),
            Log = log,
        };
        _logger.Write(
            result.Ok
                ? LogLevel.Information
                : LogLevel.Warning,
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

    public async Task<SynchronizeResult> ExecuteSynchronizationAsync(
        string operationId,
        SynchronizeParameters parameters,
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
                stage: "synchronize.validate");
        }

        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        ExternalChangeSynchronizationResult execution;
        using XaeDialogOperationScope dialogScope =
            _session.BeginDialogOperation(
                operationId,
                "synchronize",
                "xae.synchronize");
        try
        {
            execution =
                await dialogScope.ObserveAsync(
                    _session.SynchronizeExternalChangesAsync(
                    parameters.ChangedPaths,
                    _profile.ExternalChangePolicy,
                    parameters.DiscardDirtyDocuments,
                    _profile.AllowDirtyDocumentDiscard,
                    force: true,
                    TimeSpan.FromSeconds(
                        parameters.TimeoutSeconds ?? 120),
                    cancellationToken)).ConfigureAwait(false);
        }
        catch
        {
            PublishSynchronizationRequired();
            throw;
        }
        return new SynchronizeResult
        {
            Ok = true,
            OperationId = operationId,
            Scope = execution.Scope,
            SynchronizedFileCount =
                execution.SynchronizedDocuments.Count,
            DiscardedDocumentCount =
                execution.DiscardedDocuments.Count,
            DiscardedDocuments =
                execution.DiscardedDocuments.ToList(),
            DurationMs = (long)(
                DateTimeOffset.UtcNow - startedAtUtc)
                .TotalMilliseconds,
        };
    }

    public async Task<CloseXaeResult> ExecuteCloseXaeAsync(
        string operationId,
        CloseXaeParameters parameters,
        CancellationToken cancellationToken)
    {
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        DateTimeOffset deadlineUtc = startedAtUtc.AddSeconds(
            parameters.TimeoutSeconds ?? 120);
        XaeSessionSnapshot snapshot =
            await _session.VerifyAttachedAsync(
                _profile.Solution,
                GetRemaining(
                    deadlineUtc,
                    "xae.close.verify"),
                cancellationToken).ConfigureAwait(false);
        DteInstanceInfo selected = snapshot.SelectedInstance
            ?? throw new GatewayOperationException(
                ErrorCodes.XaeNotFound,
                "The selected XAE process is unavailable.",
                retryable: true,
                stage: "xae.close.verify");
        int processId = selected.ProcessId
            ?? throw new GatewayOperationException(
                ErrorCodes.XaeNotFound,
                "The selected XAE process identity is unavailable.",
                retryable: true,
                stage: "xae.close.verify");

        Interlocked.Exchange(ref _autoLaunchSuppressed, 1);
        CloseXaeResult result =
            await _session.CloseAttachedAsync(
                _profile.Solution,
                processId,
                parameters.SaveMode,
                GetRemaining(
                    deadlineUtc,
                    "xae.close.command"),
                cancellationToken).ConfigureAwait(false);
        result.OperationId = operationId;
        result.Profile = _profile.Name;
        result.DurationMs = (long)(
            DateTimeOffset.UtcNow - startedAtUtc)
            .TotalMilliseconds;
        PublishClosed(processId, parameters.SaveMode);
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
        using XaeDialogOperationScope dialogScope =
            _session.BeginDialogOperation(
                operationId,
                "activate",
                "activation.preflight",
                parameters.RunAfterActivation);
        XaeSessionSnapshot snapshot =
            await dialogScope.ObserveAsync(
                _session.VerifyAttachedAsync(
                _profile.Solution,
                GetRemaining(
                    deadlineUtc,
                    "activation.preflight"),
                cancellationToken)).ConfigureAwait(false);
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

        RuntimeMode initialRuntimeMode = runtime.Status.Mode;
        string? runtimeExceptionDetails =
            XaeRuntimeExceptionDetails.Select(
                snapshot.ErrorListMessages);
        if (initialRuntimeMode
                == RuntimeMode.Exception
            && runtimeExceptionDetails is null)
        {
            runtimeExceptionDetails =
                await ReadRuntimeExceptionDetailsAsync(
                    GetRemaining(
                        deadlineUtc,
                        "activation.runtimePreflight"),
                    cancellationToken)
                    .ConfigureAwait(false);
        }

        RuntimeOperationPolicy.EnsureActivationAllowed(
            initialRuntimeMode,
            details: runtimeExceptionDetails);
        const bool recoveryAttempted = false;

        if (_profile.AutoSynchronizeBeforeOperation)
        {
            dialogScope.SetStage("activation.synchronize");
            try
            {
                await dialogScope.ObserveAsync(
                    _session.SynchronizeExternalChangesAsync(
                        changedPaths: null,
                        _profile.ExternalChangePolicy,
                        discardDirtyDocuments: false,
                        _profile.AllowDirtyDocumentDiscard,
                        force: false,
                        GetRemaining(
                            deadlineUtc,
                            "activation.synchronize"),
                        cancellationToken)).ConfigureAwait(false);
            }
            catch (GatewayOperationException exception) when (
                RequiresSynchronization(exception.Code)
                || string.Equals(
                    exception.Stage,
                    "xae.workspace.settle",
                    StringComparison.Ordinal))
            {
                PublishSynchronizationRequired();
                throw;
            }
        }

        dialogScope.SetStage("activation.activateConfiguration");
        RecordActivationEvent(
            operationId,
            GatewayEventTypes.ActivationConfigurationStarted,
            "activation.activateConfiguration",
            "TwinCAT configuration activation started.",
            expectedAmsNetId);
        XaeActivationCommandResult command;
        using (XaeProjectGraphChangeScope projectChanges =
            _session.BeginProjectGraphChangeTracking())
        {
            try
            {
                command =
                    await _session.ActivateConfigurationAsync(
                        _profile.Solution,
                        expectedAmsNetId,
                        parameters.RunAfterActivation,
                        dialogScope,
                        GetRemaining(
                            deadlineUtc,
                            "activation.activateConfiguration"),
                        cancellationToken).ConfigureAwait(false);
                XaeAcceptedProjectGraphChanges accepted =
                    await AcceptProjectGraphChangesAsync(
                        projectChanges,
                        GetRemaining(
                            deadlineUtc,
                            "activation.projectFiles"),
                        cancellationToken).ConfigureAwait(false);
                LogAcceptedProjectChanges(
                    operationId,
                    OperationKind.Activate,
                    "activation.projectFiles",
                    accepted);
            }
            catch (GatewayOperationException exception) when (
                IsTerminalActivationOutcome(exception.Code))
            {
                XaeAcceptedProjectGraphChanges accepted =
                    await AcceptProjectGraphChangesAsync(
                        projectChanges,
                        GetRemaining(
                            deadlineUtc,
                            "activation.projectFiles"),
                        cancellationToken).ConfigureAwait(false);
                LogAcceptedProjectChanges(
                    operationId,
                    OperationKind.Activate,
                    "activation.projectFiles",
                    accepted);
                throw;
            }
            catch
            {
                _session.AbandonProjectGraphChanges(
                    projectChanges);
                PublishSynchronizationRequired();
                throw;
            }
        }

        XaeBuildExecutionResult activationBuild =
            command.Build
            ?? throw new GatewayOperationException(
                ErrorCodes.BuildResultInconsistent,
                "XAE activation completed without internal build evidence.",
                retryable: true,
                stage: "activation.compile.verify");
        ActivationCompileResult compile =
            CreateActivationCompileResult(
                operationId,
                activationBuild);
        if (!compile.Ok)
        {
            runtime = ReadRuntimeStatus(snapshot);
            long failedDurationMs = Math.Max(
                0,
                (long)(DateTimeOffset.UtcNow - startedAtUtc)
                    .TotalMilliseconds);
            ResourceReference failedLog = _logs.WriteText(
                operationId,
                ResourceKind.ActivationLog,
                FormatActivationLog(
                    operationId,
                    expectedAmsNetId,
                    recoveryAttempted,
                    initialRuntimeMode,
                    parameters.RunAfterActivation,
                    command.AutostartSelection,
                    command.Dialogs,
                    compile,
                    ActivationCompletion.Unknown,
                    activeConfigurationVerified: false,
                    runtime,
                    failedDurationMs));
            ActivationResult failed = new()
            {
                Ok = false,
                OperationId = operationId,
                DurationMs = failedDurationMs,
                Profile = _profile.Name,
                Solution = _profile.Solution,
                Target = new TargetIdentity
                {
                    Name = _profile.ExpectedTarget?.Name,
                    AmsNetId = expectedAmsNetId,
                },
                RecoveryAttempted = recoveryAttempted,
                RunAfterActivation =
                    parameters.RunAfterActivation,
                Completion = ActivationCompletion.Unknown,
                ActiveConfigurationVerified = false,
                ObservedRuntimeMode = runtime.Status.Mode,
                AutostartBootProjects =
                    command.AutostartSelection,
                Compile = compile,
                Resources =
                {
                    failedLog,
                    compile.Log!,
                },
            };
            _logger.Write(
                LogLevel.Warning,
                "activation.compile.failed",
                "TwinCAT activation stopped because its internal build "
                    + "completed with errors.",
                operationId,
                properties: new Dictionary<string, string>
                {
                    ["profile"] = _profile.Name,
                    ["solution"] = _profile.Solution,
                    ["amsNetId"] = expectedAmsNetId,
                    ["errors"] = compile.Counts.Errors.ToString(
                        CultureInfo.InvariantCulture),
                    ["warnings"] = compile.Counts.Warnings.ToString(
                        CultureInfo.InvariantCulture),
                    ["failedProjects"] =
                        compile.FailedProjects.ToString(
                            CultureInfo.InvariantCulture),
                });
            return failed;
        }

        dialogScope.SetStage("activation.verify");
        if (parameters.RunAfterActivation)
        {
            runtime = await dialogScope.ObserveAsync(
                WaitForRuntimeModeAsync(
                    expectedAmsNetId,
                    RuntimeMode.Run,
                    deadlineUtc,
                    ErrorCodes.TwinCatRestartFailed,
                    "TwinCAT did not reach Run after activation.",
                    "activation.verify",
                    cancellationToken,
                    failOnException: true)).ConfigureAwait(false);
        }
        else
        {
            runtime = ReadRuntimeStatus(snapshot);
        }

        snapshot = await dialogScope.ObserveAsync(
            _session.VerifyAttachedAsync(
            _profile.Solution,
            GetRemaining(
                deadlineUtc,
                "activation.verify"),
            cancellationToken)).ConfigureAwait(false);
        VerifyTarget(
            snapshot,
            expectedAmsNetId,
            "activation.verify");
        PublishConnected(snapshot);
        ActivationCompletion completion;
        bool activeConfigurationVerified;
        if (parameters.RunAfterActivation)
        {
            completion = ActivationCompletion.AppliedAndRunning;
            activeConfigurationVerified = true;
            RecordActivationEvent(
                operationId,
                GatewayEventTypes.ActivationConfigurationActivated,
                "activation.verify",
                "TwinCAT configuration activation was verified.",
                expectedAmsNetId);
        }
        else
        {
            completion = ActivationCompletion.RestartSkipped;
            activeConfigurationVerified = false;
            RecordActivationEvent(
                operationId,
                GatewayEventTypes.ActivationRestartSkipped,
                "activation.verify",
                "TwinCAT configuration was built and transferred, but "
                    + "the final runtime transition was skipped.",
                expectedAmsNetId);
        }

        RecordActivationEvent(
            operationId,
            GatewayEventTypes.ActivationRuntimeReady,
            "activation.verify",
            parameters.RunAfterActivation
                ? "TwinCAT runtime reached Run."
                : "TwinCAT runtime state was observed without treating "
                    + "it as proof that the new configuration is active.",
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
                initialRuntimeMode,
                parameters.RunAfterActivation,
                command.AutostartSelection,
                command.Dialogs,
                compile,
                completion,
                activeConfigurationVerified,
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
            RunAfterActivation =
                parameters.RunAfterActivation,
            Completion = completion,
            ActiveConfigurationVerified =
                activeConfigurationVerified,
            ObservedRuntimeMode = runtime.Status.Mode,
            AutostartBootProjects =
                command.AutostartSelection,
            Compile = compile,
            Resources =
            {
                log,
                compile.Log!,
            },
        };
        _logger.Write(
            LogLevel.Information,
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
                ["runAfterActivation"] =
                    parameters.RunAfterActivation.ToString(),
                ["autostartBootProjects"] =
                    command.AutostartSelection.ToString(),
                ["durationMs"] = durationMs.ToString(
                    CultureInfo.InvariantCulture),
            });
        return result;
    }

    public async Task<RecoverToConfigResult>
        ExecuteRecoverToConfigAsync(
            string operationId,
            RecoverToConfigParameters parameters,
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
                stage: "recovery.validate");
        }

        if (!_profile.AllowActivation)
        {
            throw new GatewayOperationException(
                ErrorCodes.ActivationNotAllowed,
                $"Runtime recovery is disabled for profile "
                    + $"'{_profile.Name}'.",
                stage: "recovery.validate");
        }

        string expectedAmsNetId =
            _profile.ExpectedTarget?.AmsNetId
            ?? throw new GatewayOperationException(
                ErrorCodes.ProfileInvalid,
                "The recovery profile has no expected AMS NetId.",
                stage: "recovery.validate");
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        DateTimeOffset deadlineUtc = startedAtUtc.AddSeconds(
            parameters.TimeoutSeconds ?? 120);
        using XaeDialogOperationScope dialogScope =
            _session.BeginDialogOperation(
                operationId,
                "recoverToConfig",
                "recovery.preflight");
        XaeSessionSnapshot snapshot =
            await dialogScope.ObserveAsync(
                _session.VerifyAttachedAsync(
                    _profile.Solution,
                    GetRemaining(
                        deadlineUtc,
                        "recovery.preflight"),
                    cancellationToken)).ConfigureAwait(false);
        VerifyTarget(
            snapshot,
            expectedAmsNetId,
            "recovery.preflight");
        AdsRuntimeStatusReadResult runtime =
            ReadRuntimeStatus(snapshot);
        if (runtime.Status.Mode == RuntimeMode.Unknown)
        {
            throw new GatewayOperationException(
                ErrorCodes.TwinCatStateUnknown,
                "Runtime recovery requires a readable TwinCAT "
                    + "runtime state.",
                retryable: true,
                stage: "recovery.preflight");
        }

        RuntimeMode initialRuntimeMode = runtime.Status.Mode;
        bool transitionRequested =
            initialRuntimeMode != RuntimeMode.Config;
        if (transitionRequested)
        {
            dialogScope.SetStage("recovery.command");
            await dialogScope.ObserveAsync(
                _session.RestartTwinCatConfigModeAsync(
                    _profile.Solution,
                    expectedAmsNetId,
                    GetRemaining(
                        deadlineUtc,
                        "recovery.command"),
                    cancellationToken)).ConfigureAwait(false);
            dialogScope.SetStage("recovery.verify");
            runtime = await dialogScope.ObserveAsync(
                WaitForRuntimeModeAsync(
                    expectedAmsNetId,
                    RuntimeMode.Config,
                    deadlineUtc,
                    ErrorCodes.ConfigModeRecoveryFailed,
                    "TwinCAT did not reach Config Mode after the "
                        + "explicit recovery request.",
                    "recovery.verify",
                    cancellationToken)).ConfigureAwait(false);
        }

        PublishConnected(snapshot);
        long durationMs = Math.Max(
            0,
            (long)(DateTimeOffset.UtcNow - startedAtUtc)
                .TotalMilliseconds);
        _logger.Write(
            LogLevel.Information,
            "runtime.recoverToConfig.completed",
            transitionRequested
                ? "TwinCAT reached Config Mode after an explicit "
                    + "recovery request."
                : "TwinCAT was already in Config Mode.",
            operationId,
            properties: new Dictionary<string, string>
            {
                ["profile"] = _profile.Name,
                ["solution"] = _profile.Solution,
                ["amsNetId"] = expectedAmsNetId,
                ["initialRuntimeMode"] =
                    initialRuntimeMode.ToString(),
                ["observedRuntimeMode"] =
                    runtime.Status.Mode.ToString(),
                ["transitionRequested"] =
                    transitionRequested.ToString(),
            });
        return new RecoverToConfigResult
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
            InitialRuntimeMode = initialRuntimeMode,
            ObservedRuntimeMode = runtime.Status.Mode,
            TransitionRequested = transitionRequested,
        };
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
            LogLevel.Information,
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

    private static string? ResolveBuildSetting(
        string? requested,
        string? configured,
        string name)
    {
        if (requested is not null)
        {
            if (string.IsNullOrWhiteSpace(requested))
            {
                throw new GatewayOperationException(
                    ErrorCodes.RequestInvalid,
                    $"Build {name} cannot be empty.",
                    stage: "build.validate");
            }

            return requested.Trim();
        }

        return string.IsNullOrWhiteSpace(configured)
            ? null
            : configured!.Trim();
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

    internal async Task<bool> CloseGatewayLaunchedXaeAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using XaeDialogOperationScope dialogScope =
                _session.BeginDialogOperation(
                    Guid.NewGuid().ToString("N"),
                    "closeXae",
                    "xae.close");
            return await dialogScope.ObserveAsync(
                _session.CloseGatewayLaunchedAsync(
                    timeout,
                    cancellationToken)).ConfigureAwait(false);
        }
        catch (GatewayOperationException exception)
        {
            _logger.Write(
                LogLevel.Warning,
                "xae.close.failed",
                "Gateway-launched XAE cleanup did not complete.",
                properties: new Dictionary<string, string>
                {
                    ["code"] = exception.Code,
                    ["stage"] = exception.Stage
                        ?? "xae.close",
                },
                exception: exception);
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _session.DialogObserved -= OnDialogObserved;
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
        XaeSessionSnapshot snapshot)
    {
        _errorListSnapshots.Replace(
            snapshot.ErrorListMessages);
        ComDiagnostics diagnostics = _session.GetComDiagnostics();
        bool logConnection;
        lock (_sync)
        {
            _lastSnapshot = CloneSnapshot(snapshot);
            _lastComDiagnostics = CloneCom(diagnostics);
            _lastFailureSignature = null;
            logConnection = !_wasConnected;
            _wasConnected = true;
        }

        DteInstanceInfo selected = snapshot.SelectedInstance
            ?? throw new InvalidOperationException(
                "A connected XAE snapshot has no selected instance.");
        _runtimeMonitor.UpdateTarget(
            snapshot.TargetAmsNetId,
            snapshot.TwinCatProjectPath);
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
            status.Xae.SynchronizationState =
                snapshot.SynchronizationState;
            status.Xae.DirtyDocumentCount =
                snapshot.DirtyDocumentCount;
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
                LogLevel.Information,
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

    }

    private void PublishClosed(
        int processId,
        XaeSaveMode saveMode)
    {
        lock (_sync)
        {
            _lastSnapshot = new XaeSessionSnapshot();
            _lastComDiagnostics =
                CloneCom(_session.GetComDiagnostics());
            _lastFailureSignature = null;
            _wasConnected = false;
        }

        _errorListSnapshots.Replace(
            Array.Empty<BuildDiagnostic>());
        _status.Update(status =>
        {
            status.Xae.Connected = false;
            status.Xae.Version = null;
            status.Xae.Solution = null;
            status.Xae.AgentWorkspaceOwned = false;
            status.Xae.DiscardedDocumentCount = 0;
            status.Xae.SynchronizationState =
                SynchronizationState.Uninitialized;
            status.Xae.DirtyDocumentCount = 0;
            return status;
        });
        Dictionary<string, string> properties = new()
        {
            ["processId"] = processId.ToString(
                CultureInfo.InvariantCulture),
            ["saveMode"] = saveMode.ToString(),
            ["autoLaunchSuppressed"] = bool.TrueString,
        };
        _logger.Write(
            LogLevel.Information,
            "xae.closed",
            "The selected XAE process exited.",
            properties: properties);
        _events.Record(
            new GatewayEvent
            {
                Type = GatewayEventTypes.XaeDisconnected,
                Severity = DiagnosticSeverity.Info,
                Stage = "xae.close.verify",
                Message = "The selected XAE process exited.",
                Properties = properties,
            },
            DateTimeOffset.UtcNow);
    }

    private void PublishFailure(
        XaeSessionSnapshot snapshot,
        Exception exception,
        bool retainAttachment)
    {
        GatewayOperationException? operationException =
            FindGatewayOperationException(exception);
        string? code = operationException?.Code;
        string signature =
            $"{code ?? exception.GetType().FullName}|{exception.Message}";
        bool newFailure;
        bool wasConnected;
        int? hResult = GetMeaningfulHResult(exception);
        lock (_sync)
        {
            _lastSnapshot = CloneSnapshot(snapshot);
            if (!retainAttachment)
            {
                _lastSnapshot.UnsynchronizedFiles =
                    Array.Empty<ProjectFileChange>();
            }

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
            _wasConnected = retainAttachment;
        }

        _status.Update(status => ApplyFailureStatus(
            status,
            snapshot,
            code,
            retainAttachment));
        if (wasConnected && !retainAttachment)
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
                LogLevel.Warning,
                "xae.connection.failed",
                "Could not establish or verify the configured XAE session.",
                properties: new Dictionary<string, string>
                {
                    ["code"] = code ?? "UNEXPECTED_EXCEPTION",
                    ["stage"] =
                        operationException?.Stage
                        ?? "xae.coordinator",
                    ["hresult"] = hResult is null
                        ? "unknown"
                        : $"0x{hResult.Value:X8}",
                    ["attachmentRetained"] =
                        retainAttachment.ToString(),
                },
                exception: exception);
            GatewayError error = new()
            {
                Code = code ?? ErrorCodes.OperationFailed,
                Message = exception.Message,
                Retryable =
                    operationException?.Retryable
                    ?? true,
                Stage =
                    operationException?.Stage
                    ?? "xae.coordinator",
            };
            _events.Record(
                CreateErrorEvent(
                    GatewayEventTypes.XaeConnectionFailed,
                    error,
                    new Dictionary<string, string>
                    {
                        ["exceptionType"] =
                            exception.GetType().FullName
                            ?? exception.GetType().Name,
                        ["hresult"] = hResult is null
                            ? "unknown"
                            : $"0x{hResult.Value:X8}",
                        ["attachmentRetained"] =
                            retainAttachment.ToString(),
                    }),
                DateTimeOffset.UtcNow);
        }
    }

    internal static bool ShouldRetainAttachmentAfterFailure(
        bool attached,
        Exception exception)
    {
        if (!attached)
        {
            return false;
        }

        if (exception is null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        GatewayOperationException? operationException =
            FindGatewayOperationException(exception);
        if (operationException?.Code == ErrorCodes.ComCallTimeout
            || operationException?.Code == ErrorCodes.ComCallRejected)
        {
            return true;
        }

        int? hResult = GetMeaningfulHResult(exception);
        return hResult == RpcECallRejected
            || hResult == RpcEServerCallRetryLater;
    }

    internal static GatewayStatusResult ApplyFailureStatus(
        GatewayStatusResult status,
        XaeSessionSnapshot snapshot,
        string? code,
        bool retainAttachment)
    {
        if (status is null)
        {
            throw new ArgumentNullException(nameof(status));
        }

        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (status.Gateway.State != GatewayState.Stopping)
        {
            status.Gateway.State = retainAttachment
                ? GatewayState.Faulted
                : code == ErrorCodes.XaeNotFound
                    ? GatewayState.Disconnected
                    : GatewayState.Faulted;
        }

        if (retainAttachment)
        {
            status.Xae.Connected = true;
            status.Xae.Version =
                snapshot.SelectedInstance?.Version
                ?? status.Xae.Version;
            status.Xae.Solution =
                snapshot.SelectedInstance?.Solution
                ?? status.Xae.Solution;
            status.Xae.AgentWorkspaceOwned =
                snapshot.AgentWorkspaceOwned;
            status.Xae.DiscardedDocumentCount =
                snapshot.DiscardedDocumentCount;
            status.Xae.SynchronizationState =
                snapshot.SynchronizationState;
            status.Xae.DirtyDocumentCount =
                snapshot.DirtyDocumentCount;
            return status;
        }

        status.Xae.Connected = false;
        status.Xae.Version = null;
        status.Xae.Solution = null;
        status.Xae.AgentWorkspaceOwned = false;
        status.Xae.DiscardedDocumentCount = 0;
        status.Xae.SynchronizationState =
            SynchronizationState.Uninitialized;
        status.Xae.DirtyDocumentCount = 0;
        return status;
    }

    private static GatewayOperationException?
        FindGatewayOperationException(Exception exception)
    {
        Exception? current = exception;
        while (current is not null)
        {
            if (current is GatewayOperationException operationException)
            {
                return operationException;
            }

            current = current.InnerException;
        }

        return null;
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
                LogLevel.Warning,
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
                LogLevel.Warning,
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
            SynchronizationState =
                source.SynchronizationState,
            DirtyDocumentCount =
                source.DirtyDocumentCount,
            UnsynchronizedFiles =
                source.UnsynchronizedFiles
                    .Select(CloneProjectFileChange)
                    .ToArray(),
            ActiveConfiguration =
                source.ActiveConfiguration,
            ActivePlatform = source.ActivePlatform,
            TargetAmsNetId = source.TargetAmsNetId,
            TwinCatProjectPath =
                source.TwinCatProjectPath,
            LastErrorMessages =
                source.LastErrorMessages.ToArray(),
            ErrorListMessages =
                source.ErrorListMessages
                    .Select(CloneBuildDiagnostic)
                    .ToArray(),
            DiagnosticIssues =
                source.DiagnosticIssues.ToArray(),
            DiscoveredInstances = source.DiscoveredInstances
                .Select(CloneInfo)
                .ToArray(),
        };
    }

    private static BuildDiagnostic CloneBuildDiagnostic(
        BuildDiagnostic source)
    {
        return new BuildDiagnostic
        {
            Severity = source.Severity,
            Source = source.Source,
            Code = source.Code,
            Message = source.Message,
            File = source.File,
            Line = source.Line,
            Column = source.Column,
        };
    }

    private static UnsynchronizedFileInfo
        CreateUnsynchronizedFileInfo(
            ProjectFileChange source)
    {
        return new UnsynchronizedFileInfo
        {
            Path = source.Path,
            ChangeKind = source.Kind switch
            {
                ProjectFileChangeKind.Added =>
                    SynchronizationChangeKind.Added,
                ProjectFileChangeKind.Modified =>
                    SynchronizationChangeKind.Modified,
                ProjectFileChangeKind.Deleted =>
                    SynchronizationChangeKind.Deleted,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(source)),
            },
            Role = source.Role switch
            {
                ProjectGraphFileRole.TwinCatProject =>
                    SynchronizationFileRole.TwinCatProject,
                ProjectGraphFileRole.PlcProject =>
                    SynchronizationFileRole.PlcProject,
                ProjectGraphFileRole.PlcSource =>
                    SynchronizationFileRole.PlcSource,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(source)),
            },
        };
    }

    private static ProjectFileChange CloneProjectFileChange(
        ProjectFileChange source)
    {
        return new ProjectFileChange(
            source.Path,
            source.Kind,
            source.Role);
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
            LogLevel.Information,
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

    private void OnDialogObserved(
        object sender,
        XaeDialogObservationEventArgs eventArgs)
    {
        _ = sender;
        XaeDialogObservation dialog = eventArgs.Observation;
        string buttons = string.Join(
            ", ",
            dialog.Buttons.Select(button =>
                $"{button.AutomationId}=\"{button.Name}\""));
        Dictionary<string, string> properties = new()
        {
            ["processId"] = dialog.ProcessId.ToString(
                CultureInfo.InvariantCulture),
            ["nativeWindowHandle"] =
                dialog.NativeWindowHandle.ToString(
                    CultureInfo.InvariantCulture),
            ["runtimeId"] = dialog.RuntimeId,
            ["title"] = dialog.Title,
            ["content"] = dialog.Content,
            ["kind"] = dialog.Kind,
            ["known"] = dialog.Known.ToString(),
            ["modal"] = dialog.Modal.ToString(),
            ["frameworkId"] = dialog.FrameworkId,
            ["className"] = dialog.ClassName,
            ["buttons"] = buttons,
            ["action"] = dialog.Action,
            ["actionRequested"] =
                dialog.ActionRequested.ToString(),
            ["actionCompleted"] =
                dialog.ActionCompleted.ToString(),
            ["failure"] = dialog.Failure.ToString(),
        };
        if (!string.IsNullOrWhiteSpace(dialog.OperationName))
        {
            properties["operationName"] =
                dialog.OperationName!;
        }

        _logger.Write(
            dialog.Failure
                ? LogLevel.Warning
                : LogLevel.Information,
            GatewayEventTypes.XaeDialogObserved,
            $"XAE modal dialog '{dialog.Kind}' was observed"
                + (string.IsNullOrWhiteSpace(dialog.Action)
                    ? "."
                    : $" with action '{dialog.Action}'."),
            dialog.OperationId,
            properties);
        _events.Record(
            new GatewayEvent
            {
                Type = GatewayEventTypes.XaeDialogObserved,
                Severity = dialog.Failure
                    ? DiagnosticSeverity.Warning
                    : DiagnosticSeverity.Info,
                OperationId = dialog.OperationId,
                OperationKind = MapDialogOperationKind(
                    dialog.OperationName),
                Stage = dialog.Stage ?? "xae.dialog",
                Message =
                    $"XAE modal dialog '{dialog.Kind}' was observed"
                    + (string.IsNullOrWhiteSpace(dialog.Action)
                        ? "."
                        : $" with action '{dialog.Action}'."),
                Properties = properties,
            },
            dialog.ObservedAtUtc);
    }

    private void LogAcceptedProjectChanges(
        string operationId,
        OperationKind operationKind,
        string stage,
        XaeAcceptedProjectGraphChanges? accepted)
    {
        if (accepted is null)
        {
            return;
        }

        Dictionary<string, string> summary = new()
        {
            ["changeCount"] = accepted.Changes.Count.ToString(
                CultureInfo.InvariantCulture),
            ["watcherEventCount"] =
                accepted.WatcherEventCount.ToString(
                    CultureInfo.InvariantCulture),
            ["watcherOverflow"] =
                accepted.WatcherOverflow.ToString(),
            ["settleDurationMs"] =
                accepted.SettleDurationMs.ToString(
                    CultureInfo.InvariantCulture),
        };
        _logger.Write(
            accepted.WatcherOverflow
                ? LogLevel.Warning
                : LogLevel.Information,
            GatewayEventTypes.XaeProjectChangesAccepted,
            accepted.Changes.Count == 0
                ? "XAE project files became quiet without graph changes."
                : $"{accepted.Changes.Count} XAE project graph change(s) "
                    + "were accepted.",
            operationId,
            summary);
        foreach (ProjectFileChange change in accepted.Changes)
        {
            _logger.Write(
                LogLevel.Information,
                "xae.projectFile.accepted",
                "A project graph file change made during the XAE "
                    + "operation was accepted.",
                operationId,
                properties: new Dictionary<string, string>
                {
                    ["path"] = change.Path,
                    ["kind"] = change.Kind.ToString(),
                    ["role"] = change.Role.ToString(),
                });
        }

        _events.Record(
            new GatewayEvent
            {
                Type = GatewayEventTypes.XaeProjectChangesAccepted,
                Severity = accepted.WatcherOverflow
                    ? DiagnosticSeverity.Warning
                    : DiagnosticSeverity.Info,
                OperationId = operationId,
                OperationKind = operationKind,
                Stage = stage,
                Message = accepted.Changes.Count == 0
                    ? "XAE project files became quiet without graph "
                        + "changes."
                    : $"{accepted.Changes.Count} XAE project graph "
                        + "change(s) were accepted.",
                Properties = summary,
            },
            DateTimeOffset.UtcNow);
    }

    private async Task<XaeAcceptedProjectGraphChanges>
        AcceptProjectGraphChangesAsync(
        XaeProjectGraphChangeScope scope,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _session.AcceptProjectGraphChangesAsync(
                scope,
                timeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            PublishSynchronizationRequired();
            throw;
        }
    }

    private static bool IsTerminalActivationOutcome(string code)
    {
        return string.Equals(
                code,
                ErrorCodes.ActivationDialogDetected,
                StringComparison.Ordinal)
            || string.Equals(
                code,
                ErrorCodes.XaeDialogReportedFailure,
                StringComparison.Ordinal);
    }

    private static OperationKind? MapDialogOperationKind(
        string? operationName)
    {
        return operationName?.ToLowerInvariant() switch
        {
            "build" or "rebuild" or "clean" =>
                OperationKind.Build,
            "synchronize" => OperationKind.Synchronize,
            "activate" => OperationKind.Activate,
            "recovertoconfig" => OperationKind.RecoverToConfig,
            "opensolution" => OperationKind.OpenSolution,
            _ => null,
        };
    }

    private async Task<AdsRuntimeStatusReadResult>
        WaitForRuntimeModeAsync(
            string amsNetId,
            RuntimeMode expectedMode,
            DateTimeOffset deadlineUtc,
            string errorCode,
            string errorMessage,
            string stage,
            CancellationToken cancellationToken,
            bool failOnException = false)
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

                if (runtime.Diagnostics.ErrorCode is null
                    && failOnException)
                {
                    string? details = null;
                    if (runtime.Status.Mode
                        == RuntimeMode.Exception)
                    {
                        details =
                            await ReadRuntimeExceptionDetailsAsync(
                                readTimeout,
                                cancellationToken)
                                .ConfigureAwait(false);
                    }

                    RuntimeOperationPolicy.EnsureActivationAllowed(
                        runtime.Status.Mode,
                        stage,
                        "Activation did not reach Run because the TwinCAT "
                            + "runtime entered Exception. Explicitly "
                            + "recover the runtime to Config before "
                            + "building or activating again.",
                        details);
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
            stage: stage);
    }

    private async Task<string?>
        ReadRuntimeExceptionDetailsAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<BuildDiagnostic> messages =
                await _session.ReadErrorListAsync(
                    timeout,
                    cancellationToken).ConfigureAwait(false);
            _errorListSnapshots.Replace(messages);
            return XaeRuntimeExceptionDetails.Select(
                messages);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Write(
                LogLevel.Warning,
                "xae.error_list.read_failed",
                "Could not read XAE Error List after the runtime entered Exception.",
                exception: exception);
            return null;
        }
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
                "The gateway operation exceeded its deadline.",
                retryable: true,
                stage: stage);
        }

        return remaining;
    }

    private static bool RequiresSynchronization(string code)
    {
        return code == ErrorCodes.XaeSyncRequired
            || code == ErrorCodes.ExternalChangeDetected
            || code == ErrorCodes.ExternalEditSyncFailed
            || code == ErrorCodes.ProjectGraphInvalid;
    }

    private void PublishSynchronizationRequired()
    {
        _session.MarkSynchronizationRequired();
        _status.Update(status =>
        {
            status.Xae.SynchronizationState =
                SynchronizationState.SyncRequired;
            return status;
        });
    }

    private ActivationCompileResult CreateActivationCompileResult(
        string operationId,
        XaeBuildExecutionResult execution)
    {
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
                    Source = "xae-activation-build",
                    Message = execution.FailedProjects == 1
                        ? "One project failed during activation; XAE Error "
                            + "List did not expose compiler diagnostics."
                        : $"{execution.FailedProjects} projects failed "
                            + "during activation; XAE Error List did not "
                            + "expose compiler diagnostics.",
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
        return new ActivationCompileResult
        {
            Completed = true,
            Ok = execution.FailedProjects == 0
                && errors == 0,
            DurationMs = execution.DurationMs,
            FailedProjects = execution.FailedProjects,
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
            Log = log,
        };
    }

    private string FormatActivationLog(
        string operationId,
        string amsNetId,
        bool recoveryAttempted,
        RuntimeMode initialRuntimeMode,
        bool runAfterActivation,
        AutostartBootProjectSelection autostartSelection,
        IReadOnlyList<XaeDialogObservation> dialogs,
        ActivationCompileResult compile,
        ActivationCompletion completion,
        bool activeConfigurationVerified,
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
            $"InitialRuntimeMode: {initialRuntimeMode}");
        builder.AppendLine(
            $"RunAfterActivation: {runAfterActivation}");
        builder.AppendLine(
            $"AutostartBootProjects: {autostartSelection}");
        builder.AppendLine(
            $"CompileCompleted: {compile.Completed}");
        builder.AppendLine($"CompileOk: {compile.Ok}");
        builder.AppendLine(
            $"CompileFailedProjects: {compile.FailedProjects}");
        builder.AppendLine(
            $"CompileErrors: {compile.Counts.Errors}");
        builder.AppendLine(
            $"CompileWarnings: {compile.Counts.Warnings}");
        builder.AppendLine($"Completion: {completion}");
        builder.AppendLine(
            $"ActiveConfigurationVerified: "
                + $"{activeConfigurationVerified}");
        foreach (XaeDialogObservation dialog in dialogs)
        {
            builder.AppendLine(
                $"Dialog: {dialog.Kind}; Action={dialog.Action}; "
                    + $"Requested={dialog.ActionRequested}; "
                    + $"Title={dialog.Title}");
            if (!string.IsNullOrWhiteSpace(dialog.Content))
            {
                builder.AppendLine($"DialogText: {dialog.Content}");
            }
        }
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
