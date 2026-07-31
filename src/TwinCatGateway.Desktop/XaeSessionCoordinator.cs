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
using XaeSessionSnapshotContract = TwinCatGateway.Contracts.XaeSessionSnapshot;

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
    private readonly ResolvedProfile _profile;
    private readonly ProfileResolver _profiles;
    private readonly CapabilityEvaluator _capabilities;
    private readonly GatewayStatusSnapshotStore _status;
    private readonly ILogger<XaeSessionCoordinator> _logger;
    private readonly LocalLogStore _logs;
    private readonly IGatewayEventSink _events;
    private readonly AdsRuntimeMonitor? _runtimeMonitor;
    private readonly XaeErrorListSnapshotStore
        _errorListSnapshots;
    private readonly SourceManifestStore _sourceManifests;
    private readonly XaeCloseConsentStore _xaeCloseConsent;
    private readonly CapabilitySnapshotStore _capabilitySnapshots;
    private readonly TargetOperationService _targetOperations = new();
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
        ResolvedProfile profile,
        ProfileResolver profiles,
        CapabilityEvaluator capabilities,
        GatewayStatusSnapshotStore status,
        ILogger<XaeSessionCoordinator> logger,
        ILogger<TcUnitRunExecutor> tcUnitLogger,
        LocalLogStore logs,
        IGatewayEventSink events,
        AdsRuntimeMonitor? runtimeMonitor,
        XaeErrorListSnapshotStore errorListSnapshots,
        SourceManifestStore sourceManifests,
        XaeCloseConsentStore xaeCloseConsent,
        CapabilitySnapshotStore capabilitySnapshots)
    {
        _profile = profile
            ?? throw new ArgumentNullException(nameof(profile));
        _profiles = profiles
            ?? throw new ArgumentNullException(nameof(profiles));
        _capabilities = capabilities
            ?? throw new ArgumentNullException(
                nameof(capabilities));
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
        _runtimeMonitor = runtimeMonitor;
        _errorListSnapshots = errorListSnapshots
            ?? throw new ArgumentNullException(
                nameof(errorListSnapshots));
        _sourceManifests = sourceManifests
            ?? throw new ArgumentNullException(
                nameof(sourceManifests));
        _xaeCloseConsent = xaeCloseConsent
            ?? throw new ArgumentNullException(
                nameof(xaeCloseConsent));
        _capabilitySnapshots = capabilitySnapshots
            ?? throw new ArgumentNullException(
                nameof(capabilitySnapshots));
        _tcUnit = new TcUnitRunExecutor(
            _profile,
            (operationId, xml) => _logs.WriteText(
                operationId,
                OperationArtifactKind.TestXunit,
                xml),
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
                bool wasConnected = connected;
                if (!wasConnected)
                {
                    PublishAttaching();
                }

                XaeSessionSnapshot snapshot = wasConnected
                    ? await _session.VerifyAttachedAsync(
                        _profile.Xae.Solution,
                        HealthTimeout,
                        cancellationToken).ConfigureAwait(false)
                    : await _session.EnsureAttachedAsync(
                        _profile.Xae.Solution,
                        IsEffective(CapabilityKey.XaeLaunch)
                            && Volatile.Read(
                                ref _autoLaunchSuppressed) == 0,
                        _profile.Xae.ProgId,
                        _profile.Xae.Workspace
                            .AssumeAttachedSynchronized,
                        () => _capabilities.EnsureAllowed(
                            _profile,
                            CapabilityKey.XaeLaunch,
                            "xae.launch.preSideEffect"),
                        AttachTimeout,
                        cancellationToken).ConfigureAwait(false);
                snapshot = _session.RefreshSynchronizationStatus(
                    cancellationToken);
                connected = true;
                if (snapshot.SynchronizationState
                    != SynchronizationState.Confirmed)
                {
                    _sourceManifests.MarkStale(
                        ErrorCodes.ProjectGraphInvalid,
                        "The source manifest is stale because the "
                            + "project graph requires synchronization.");
                }
                else if (!wasConnected
                    || _sourceManifests.DiscoveryState
                        != SourceDiscoveryState.Confirmed)
                {
                    RefreshSourceManifest(cancellationToken);
                }

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
                        _profile.Xae.Solution,
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
                ?? _profile.Xae.Solution,
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

    public async Task<XaeBuildResult> ExecuteXaeBuildAsync(
        string operationId,
        XaeBuildParameters parameters,
        CancellationToken cancellationToken)
    {
        EnsureProfileIdentity(parameters.Profile, "xae.build.preflight");
        _capabilities.EnsureAllowed(
            _profile,
            CapabilityKey.XaeBuild,
            "xae.build.preflight");
        if (_profile.Xae.Workspace.AutoSynchronizeBeforeOperation)
        {
            _capabilities.EnsureAllowed(
                _profile,
                CapabilityKey.XaeSynchronize,
                "xae.build.synchronize");
        }

        DateTimeOffset deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(
            120);
        XaeBuildExecutionResult execution;
        using XaeDialogOperationScope dialogScope =
            _session.BeginDialogOperation(
                operationId,
                parameters.Action.ToString().ToLowerInvariant(),
                "xae.build.preflight");
        try
        {
            await dialogScope.ObserveAsync(
                _session.VerifyAttachedAsync(
                    _profile.Xae.Solution,
                    GetRemaining(
                        deadlineUtc,
                        "xae.build.preflight"),
                    cancellationToken)).ConfigureAwait(false);

            OperationCapabilityGuard buildGuard = new(
                _capabilities,
                _profile,
                CapabilityKey.XaeBuild);
            buildGuard.EnsureAllowed(
                "xae.build.preSideEffect");
            bool configurationChanged =
                await dialogScope.ObserveAsync(
                _session.SelectBuildConfigurationAsync(
                    _profile.Xae.Configuration,
                    _profile.Xae.Platform,
                    GetRemaining(
                        deadlineUtc,
                        "xae.build.configuration"),
                    cancellationToken)).ConfigureAwait(false);
            dialogScope.SetStage("xae.build");
            execution = await dialogScope.ObserveAsync(
                _session.ExecuteBuildAsync(
                    _profile.Xae.Solution,
                    parameters.Action,
                    parameters.Scope,
                    parameters.Project,
                    parameters.ChangedPaths,
                    _profile.Xae.Workspace.ExternalChangePolicy,
                    _profile.Xae.Workspace
                        .AutoSynchronizeBeforeOperation,
                    sideEffectsStarted =>
                        buildGuard.EnsureAllowed(
                            "xae.build.safeBoundary",
                            configurationChanged
                                || sideEffectsStarted),
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
            OperationArtifactKind.Build,
            FormatBuildOutput(execution.Output));
        ResourceReference? projectNoise = execution.ProjectChanges.Count == 0
            ? null
            : _logs.WriteText(
                operationId,
                OperationArtifactKind.ProjectNoise,
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
            OperationKind.XaeBuild,
            "xae.build.settle",
            execution.AcceptedProjectChanges);
        if (execution.AcceptedProjectChanges is not null
            || execution.Synchronization.Scope
                != SynchronizationScope.None)
        {
            RefreshSourceManifest(cancellationToken);
        }

        XaeBuildResult result = new()
        {
            Ok = execution.FailedProjects == 0
                && errors == 0,
            OperationId = operationId,
            Action = execution.Action,
            Scope = execution.Scope,
            Project = execution.Project,
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
                ["scope"] = result.Scope.ToString(),
                ["project"] = result.Project ?? string.Empty,
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
        EnsureProfileIdentity(
            parameters.Profile,
            "synchronize.preflight");
        _capabilities.EnsureAllowed(
            _profile,
            CapabilityKey.XaeSynchronize,
            "synchronize.preflight");
        if (parameters.DiscardDirtyDocuments)
        {
            _capabilities.EnsureAllowed(
                _profile,
                CapabilityKey.XaeDiscardDirtyDocuments,
                "synchronize.discard.preflight");
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
            _capabilities.EnsureAllowed(
                _profile,
                CapabilityKey.XaeSynchronize,
                "xae.synchronize.preSideEffect");
            if (parameters.DiscardDirtyDocuments)
            {
                _capabilities.EnsureAllowed(
                    _profile,
                    CapabilityKey.XaeDiscardDirtyDocuments,
                    "xae.synchronize.discard.preSideEffect");
            }

            execution =
                await dialogScope.ObserveAsync(
                    _session.SynchronizeExternalChangesAsync(
                    parameters.ChangedPaths,
                    _profile.Xae.Workspace.ExternalChangePolicy,
                    parameters.DiscardDirtyDocuments,
                    IsEffective(
                        CapabilityKey.XaeDiscardDirtyDocuments),
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
        RefreshSourceManifest(cancellationToken);
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
                _profile.Xae.Solution,
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

        CapabilityEvaluationContext context = new(processId);
        _capabilities.EnsureAllowed(
            _profile,
            CapabilityKey.XaeClose,
            "xae.close.preSideEffect",
            context);
        if (parameters.SaveMode == XaeSaveMode.Discard)
        {
            _capabilities.EnsureAllowed(
                _profile,
                CapabilityKey.XaeDiscardDirtyDocuments,
                "xae.close.discard.preSideEffect",
                context);
        }

        Interlocked.Exchange(ref _autoLaunchSuppressed, 1);
        CloseXaeResult result =
            await _session.CloseAttachedAsync(
                _profile.Xae.Solution,
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
        _sourceManifests.MarkStale(
            ErrorCodes.XaeNotFound,
            "The source manifest is stale because the XAE session was closed.");
        return result;
    }

    public async Task<ActivationResult> ExecuteActivationAsync(
        string operationId,
        ActivateParameters parameters,
        CancellationToken cancellationToken)
    {
        EnsureProfileIdentity(
            parameters.Profile,
            "activation.preflight");

        _capabilities.EnsureAllowed(
            _profile,
            CapabilityKey.XaeActivate,
            "activation.preflight");
        OperationCapabilityGuard activationGuard = new(
            _capabilities,
            _profile,
            CapabilityKey.XaeActivate);
        bool runAfterActivation = parameters.FinalTargetMode
            == ActivationFinalTargetMode.Run;
        bool sideEffectsStarted = false;

        string expectedAmsNetId =
            _profile.Target?.AmsNetId
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
                runAfterActivation);
        XaeSessionSnapshot snapshot =
            await dialogScope.ObserveAsync(
                _session.VerifyAttachedAsync(
                        _profile.Xae.Solution,
                GetRemaining(
                    deadlineUtc,
                    "activation.preflight"),
                cancellationToken)).ConfigureAwait(false);
        VerifyTarget(
            snapshot,
            "activation.preflight");
        TargetSystemObservation runtime =
            await ReadDirectTargetObservationAsync(
                    TimeSpan.FromSeconds(3),
                    cancellationToken)
                .ConfigureAwait(false);
        TargetSystemState initialRuntimeMode = runtime.State;
        SynchronizeResult synchronizationResult = new()
        {
            Ok = true,
            OperationId = operationId,
            Scope = SynchronizationScope.None,
        };
        if (_profile.Xae.Workspace.AutoSynchronizeBeforeOperation)
        {
            _capabilities.EnsureAllowed(
                _profile,
                CapabilityKey.XaeSynchronize,
                "activation.synchronize");
            dialogScope.SetStage("activation.synchronize");
            try
            {
                ExternalChangeSynchronizationResult synchronization =
                    await dialogScope.ObserveAsync(
                    _session.SynchronizeExternalChangesAsync(
                        changedPaths: null,
                        _profile.Xae.Workspace.ExternalChangePolicy,
                        discardDirtyDocuments: false,
                        IsEffective(
                            CapabilityKey.XaeDiscardDirtyDocuments),
                        force: false,
                        GetRemaining(
                            deadlineUtc,
                            "activation.synchronize"),
                        cancellationToken)).ConfigureAwait(false);
                sideEffectsStarted =
                    synchronization.SynchronizedDocuments.Count > 0
                    || synchronization.DiscardedDocuments.Count > 0;
                synchronizationResult = new SynchronizeResult
                {
                    Ok = true,
                    OperationId = operationId,
                    Scope = synchronization.Scope,
                    SynchronizedFileCount =
                        synchronization.SynchronizedDocuments.Count,
                    DiscardedDocumentCount =
                        synchronization.DiscardedDocuments.Count,
                    DiscardedDocuments =
                        synchronization.DiscardedDocuments.ToList(),
                };
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

        activationGuard.EnsureAllowed(
            "activation.activateConfiguration.preSideEffect",
            sideEffectsStarted);
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
                        _profile.Xae.Solution,
                        expectedAmsNetId,
                        runAfterActivation,
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
            runtime = await ReadDirectTargetObservationAsync(
                    TimeSpan.FromSeconds(3),
                    cancellationToken)
                .ConfigureAwait(false);
            long failedDurationMs = Math.Max(
                0,
                (long)(DateTimeOffset.UtcNow - startedAtUtc)
                    .TotalMilliseconds);
            ResourceReference failedLog = _logs.WriteText(
                operationId,
                OperationArtifactKind.XaeMessages,
                FormatActivationLog(
                    operationId,
                    expectedAmsNetId,
                    initialRuntimeMode,
                    command.AutostartSelection,
                    command.Dialogs,
                    compile,
                    parameters.FinalTargetMode,
                    activeConfigurationVerified: false,
                    runtime,
                    failedDurationMs));
            ActivationResult failed = new()
            {
                Ok = false,
                OperationId = operationId,
                DurationMs = failedDurationMs,
                Profile = _profile.Name,
                Solution = _profile.Xae.Solution,
                Target = new TargetIdentity
                {
                    Name = _profile.Target?.Name,
                    AmsNetId = expectedAmsNetId,
                },
                Sync = CreateSucceededSyncStage(
                    operationId,
                    synchronizationResult),
                Compile = CreateCompileStage(
                    operationId,
                    compile,
                    OperationCompletion.Failed),
                Deploy = CreateSkippedDeployStage(operationId),
                TargetTransition = CreateSkippedTargetTransitionStage(
                    operationId,
                    parameters.FinalTargetMode),
                Verification = CreateSkippedVerificationStage(operationId),
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
                    ["solution"] = _profile.Xae.Solution,
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
        TargetSystemObservation? targetObservation = null;
        if (runAfterActivation)
        {
            targetObservation = await WaitForDirectTargetRunAsync(
                deadlineUtc,
                cancellationToken).ConfigureAwait(false);
            runtime = targetObservation;
        }
        else
        {
            runtime = await ReadDirectTargetObservationAsync(
                    TimeSpan.FromSeconds(3),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        snapshot = await dialogScope.ObserveAsync(
            _session.VerifyAttachedAsync(
                        _profile.Xae.Solution,
            GetRemaining(
                deadlineUtc,
                "activation.verify"),
            cancellationToken)).ConfigureAwait(false);
        VerifyTarget(
            snapshot,
            "activation.verify");
        PublishConnected(snapshot);
        bool physicalActivationVerified;
        if (runAfterActivation)
        {
            physicalActivationVerified = true;
            RecordActivationEvent(
                operationId,
                GatewayEventTypes.ActivationConfigurationActivated,
                "activation.verify",
                "TwinCAT configuration activation was verified.",
                expectedAmsNetId);
        }
        else
        {
            physicalActivationVerified = false;
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
            runAfterActivation
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
            OperationArtifactKind.XaeMessages,
            FormatActivationLog(
                operationId,
                expectedAmsNetId,
                initialRuntimeMode,
                command.AutostartSelection,
                command.Dialogs,
                compile,
                parameters.FinalTargetMode,
                physicalActivationVerified,
                runtime,
                durationMs));
        ActivationResult result = new()
        {
            Ok = true,
            OperationId = operationId,
            DurationMs = durationMs,
            Profile = _profile.Name,
            Solution = _profile.Xae.Solution,
            Target = new TargetIdentity
            {
                Name = _profile.Target?.Name,
                AmsNetId = expectedAmsNetId,
            },
            Sync = CreateSucceededSyncStage(
                operationId,
                synchronizationResult),
            Compile = CreateCompileStage(
                operationId,
                compile,
                OperationCompletion.Succeeded),
            Deploy = CreateDeployStage(
                operationId,
                command.AutostartSelection,
                physicalActivationVerified),
            TargetTransition = CreateTargetTransitionStage(
                operationId,
                parameters.FinalTargetMode,
                targetObservation),
            Verification = CreateSkippedVerificationStage(operationId),
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
                ["solution"] = _profile.Xae.Solution,
                ["amsNetId"] = expectedAmsNetId,
                ["finalTargetMode"] =
                    parameters.FinalTargetMode.ToString(),
                ["autostartBootProjects"] =
                    command.AutostartSelection.ToString(),
                ["durationMs"] = durationMs.ToString(
                    CultureInfo.InvariantCulture),
            });
        return result;
    }

    public Task<TargetConfigResult> ExecuteTargetConfigAsync(
        string operationId,
        TargetConfigParameters parameters,
        CancellationToken cancellationToken)
    {
        EnsureProfileIdentity(
            parameters.Profile,
            "target.config.preflight");
        _capabilities.EnsureAllowed(
            _profile,
            CapabilityKey.TargetConfig,
            "target.config.preflight");
        OperationCapabilityGuard capabilityGuard = new(
            _capabilities,
            _profile,
            CapabilityKey.TargetConfig);
        return _targetOperations.ExecuteConfigAsync(
            operationId,
            _profile,
            capabilityGuard,
            ReadDirectTargetObservationAsync,
            ReadTargetFaultEvidenceAsync,
            (timeout, commandCancellation) =>
                ExecuteTargetConfigCommandAsync(
                    operationId,
                    timeout,
                    commandCancellation),
            TimeSpan.FromSeconds(120),
            cancellationToken);
    }

    public Task<TargetStartRestartResult>
        ExecuteTargetStartRestartAsync(
            string operationId,
            TargetStartRestartParameters parameters,
            CancellationToken cancellationToken)
    {
        EnsureProfileIdentity(
            parameters.Profile,
            "target.startRestart.preflight");
        _capabilities.EnsureAllowed(
            _profile,
            CapabilityKey.TargetStartRestart,
            "target.startRestart.preflight");
        OperationCapabilityGuard capabilityGuard = new(
            _capabilities,
            _profile,
            CapabilityKey.TargetStartRestart);
        return _targetOperations.ExecuteStartRestartAsync(
            operationId,
            _profile,
            capabilityGuard,
            ReadDirectTargetObservationAsync,
            (timeout, commandCancellation) =>
                ExecuteTargetStartRestartCommandAsync(
                    operationId,
                    timeout,
                    commandCancellation),
            TimeSpan.FromSeconds(120),
            cancellationToken);
    }

    private Task<TargetSystemObservation>
        ReadDirectTargetObservationAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResolvedTargetProfile target = _profile.Target
            ?? throw new GatewayOperationException(
                ErrorCodes.TargetNotConfigured,
                $"Profile '{_profile.Name}' has no configured Target System.",
                stage: "target.observe",
                component: GatewayComponent.Target,
                sideEffectsStarted: false);
        AdsStateReadResult read = AdsStateReader.Read(
            target.AmsNetId,
            timeout);
        return Task.FromResult(new TargetSystemObservation
        {
            Source = ObservationSource.SystemService,
            Profile = _profile.Name,
            AmsNetId = target.AmsNetId,
            Port = AdsStateReader.SystemServicePort,
            RawAdsState = read.RawAdsState,
            RawAdsStateName = read.RawAdsStateName,
            RawDeviceState = read.RawDeviceState,
            State = read.RawAdsState.HasValue
                ? AdsStateMapper.MapSystemService(
                    read.RawAdsState.Value)
                : TargetSystemState.Unknown,
            ObservedAtUtc = read.ObservedAtUtc,
            Freshness = read.Succeeded
                ? ObservationFreshness.Fresh
                : ObservationFreshness.Unavailable,
            Error = read.Error,
        });
    }

    private async Task<XaeMessagesResult?>
        ReadTargetFaultEvidenceAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        XaeSessionSnapshot snapshot =
            await _session.VerifyAttachedAsync(
                _profile.Xae.Solution,
                timeout,
                cancellationToken).ConfigureAwait(false);
        IReadOnlyList<BuildDiagnostic> messages =
            await _session.ReadErrorListAsync(
                timeout,
                cancellationToken).ConfigureAwait(false);
        BuildDiagnostic[] selected = messages
            .Where(message =>
                message.Severity == DiagnosticSeverity.Error
                || message.Severity == DiagnosticSeverity.Warning)
            .ToArray();
        return new XaeMessagesResult
        {
            Solution = snapshot.SelectedInstance?.Solution
                ?? _profile.Xae.Solution,
            ReadAtUtc = DateTimeOffset.UtcNow,
            Counts = new DiagnosticCounts
            {
                Errors = selected.Count(message =>
                    message.Severity == DiagnosticSeverity.Error),
                Warnings = selected.Count(message =>
                    message.Severity == DiagnosticSeverity.Warning),
            },
            Messages = selected
                .Take(50)
                .Select(CloneBuildDiagnostic)
                .ToList(),
            MoreMessages = Math.Max(0, selected.Length - 50),
        };
    }

    private async Task ExecuteTargetConfigCommandAsync(
        string operationId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string expectedAmsNetId = _profile.Target?.AmsNetId
            ?? throw new GatewayOperationException(
                ErrorCodes.TargetNotConfigured,
                $"Profile '{_profile.Name}' has no configured Target System.",
                stage: "target.config.command",
                component: GatewayComponent.Target,
                sideEffectsStarted: false);
        using XaeDialogOperationScope dialogScope =
            _session.BeginDialogOperation(
                operationId,
                "targetConfig",
                "target.config.command");
        await dialogScope.ObserveAsync(
            _session.RequestTargetConfigAsync(
                _profile.Xae.Solution,
                expectedAmsNetId,
                timeout,
                cancellationToken)).ConfigureAwait(false);
    }

    private async Task ExecuteTargetStartRestartCommandAsync(
        string operationId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string expectedAmsNetId = _profile.Target?.AmsNetId
            ?? throw new GatewayOperationException(
                ErrorCodes.TargetNotConfigured,
                $"Profile '{_profile.Name}' has no configured Target System.",
                stage: "target.startRestart.command",
                component: GatewayComponent.Target,
                sideEffectsStarted: false);
        using XaeDialogOperationScope dialogScope =
            _session.BeginDialogOperation(
                operationId,
                "targetStartRestart",
                "target.startRestart.command");
        await dialogScope.ObserveAsync(
            _session.StartRestartTargetAsync(
                _profile.Xae.Solution,
                expectedAmsNetId,
                timeout,
                cancellationToken)).ConfigureAwait(false);
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

    public async Task<XaeOpenResult> ExecuteXaeOpenAsync(
        string operationId,
        XaeOpenParameters parameters,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                parameters.Profile,
                _profile.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayOperationException(
                ErrorCodes.ProfileNotFound,
                $"Project profile '{parameters.Profile}' was not found.",
                stage: "xae.open.admission",
                component: GatewayComponent.Profile,
                sideEffectsStarted: false);
        }

        lock (_sync)
        {
            _lastErrorMessage = null;
        }
        Interlocked.Exchange(ref _autoLaunchSuppressed, 0);
        RequestReconnect();
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(55);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            XaeSessionSnapshot snapshot;
            string? lastError;
            lock (_sync)
            {
                snapshot = CloneSnapshot(_lastSnapshot);
                lastError = _lastErrorMessage;
            }

            if (snapshot.Connected && snapshot.SelectedInstance is not null)
            {
                return new XaeOpenResult
                {
                    Attached = !snapshot.LaunchedByGateway,
                    Launched = snapshot.LaunchedByGateway,
                    State = CreateContractSnapshot(snapshot),
                };
            }

            if (!string.IsNullOrWhiteSpace(lastError)
                && Volatile.Read(ref _reconnectRequested) == 0)
            {
                throw new GatewayOperationException(
                    ErrorCodes.XaeLaunchFailed,
                    "XAE could not be opened: " + lastError,
                    retryable: true,
                    stage: "xae.open.attach",
                    component: GatewayComponent.Xae,
                    sideEffectsStarted: null);
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        throw new GatewayOperationException(
            ErrorCodes.ComCallTimeout,
            "XAE open did not reach an attached postcondition before the deadline.",
            retryable: true,
            stage: "xae.open.verify",
            component: GatewayComponent.Xae,
            sideEffectsStarted: null);
    }

    private XaeSessionSnapshotContract CreateContractSnapshot(
        XaeSessionSnapshot snapshot)
    {
        DteInstanceInfo selected = snapshot.SelectedInstance!;
        return new XaeSessionSnapshotContract
        {
            Profile = _profile.Name,
            ProcessId = selected.ProcessId,
            Ownership = snapshot.Ownership,
            DteAvailable = snapshot.Connected,
            ProgId = selected.ProgId,
            Version = selected.Version,
            Solution = selected.Solution,
            SolutionLoaded = !string.IsNullOrWhiteSpace(selected.Solution),
            ActiveConfiguration = snapshot.ActiveConfiguration,
            ActivePlatform = snapshot.ActivePlatform,
            SynchronizationState = snapshot.SynchronizationState,
            DirtyDocuments = snapshot.DirtyDocuments.ToList(),
            Errors = snapshot.DiagnosticIssues
                .Concat(snapshot.LastErrorMessages)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            TwinCatSystem = CloneTwinCatSystem(snapshot.TwinCatSystem),
            ObservedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public XaeSessionSnapshotContract ReadXaeState()
    {
        lock (_sync)
        {
            XaeSessionSnapshot snapshot = CloneSnapshot(_lastSnapshot);
            if (snapshot.Connected && snapshot.SelectedInstance is not null)
            {
                return CreateContractSnapshot(snapshot);
            }

            return new XaeSessionSnapshotContract
            {
                Profile = _profile.Name,
                Ownership = snapshot.Ownership,
                DteAvailable = false,
                Solution = _profile.Xae.Solution,
                SolutionLoaded = false,
                SynchronizationState = snapshot.SynchronizationState,
                Errors = MergeLastErrorMessages(),
                ObservedAtUtc = DateTimeOffset.UtcNow,
            };
        }
    }

    public XaeDiagnosticsSnapshot ReadXaeDiagnostics()
    {
        lock (_sync)
        {
            return new XaeDiagnosticsSnapshot
            {
                Profile = _profile.Name,
                State = ReadXaeState(),
                DteInstances = _lastSnapshot.DiscoveredInstances
                    .Select(CloneInfo)
                    .ToList(),
                Com = CloneCom(_lastComDiagnostics),
            };
        }
    }

    public XaeMessagesResult ReadXaeMessagesSnapshot()
    {
        IReadOnlyList<BuildDiagnostic> messages = _errorListSnapshots.Read();
        string solution;
        lock (_sync)
        {
            solution = _lastSnapshot.SelectedInstance?.Solution
                ?? _profile.Xae.Solution;
        }

        return new XaeMessagesResult
        {
            Solution = solution,
            ReadAtUtc = DateTimeOffset.UtcNow,
            Counts = new DiagnosticCounts
            {
                Errors = messages.Count(message =>
                    message.Severity == DiagnosticSeverity.Error),
                Warnings = messages.Count(message =>
                    message.Severity == DiagnosticSeverity.Warning),
            },
            Messages = messages.Take(200).Select(CloneBuildDiagnostic).ToList(),
            MoreMessages = Math.Max(0, messages.Count - 200),
        };
    }

    public async Task<TestResult> ExecuteTcUnitAsync(
        string operationId,
        TcUnitRunPreparation preparation,
        CancellationToken cancellationToken)
    {
        OperationCapabilityGuard verificationGuard = new(
            _capabilities,
            _profile,
            CapabilityKey.TargetTcUnitVerification);
        XaeSessionSnapshot snapshot =
            await _session.VerifyAttachedAsync(
                        _profile.Xae.Solution,
                HealthTimeout,
                cancellationToken).ConfigureAwait(false);
        VerifyTarget(
            snapshot,
            "tcunit.preflight");
        verificationGuard.EnsureAllowed(
            "tcunit.execute.preSideEffect");
        return await _tcUnit.ExecuteAsync(
            operationId,
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

    internal async Task<bool> CloseConsentedCleanXaeOnShutdownAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            XaeSessionSnapshot snapshot =
                await _session.GetSnapshotAsync(
                    timeout,
                    cancellationToken).ConfigureAwait(false);
            int? processId = snapshot.SelectedInstance?.ProcessId;
            if (!snapshot.Connected || !processId.HasValue)
            {
                return false;
            }

            XaeShutdownCleanupPolicy cleanupPolicy = new(
                _capabilities,
                _profile);
            if (!cleanupPolicy.CanClose(
                    processId.Value,
                    snapshot.DirtyDocumentCount,
                    "xae.close.shutdown.preSideEffect"))
            {
                _logger.Write(
                    LogLevel.Information,
                    "xae.close.shutdown.skipped",
                    "XAE shutdown cleanup was skipped because the session snapshot contains dirty documents.",
                    properties: new Dictionary<string, string>
                    {
                        ["processId"] = processId.Value.ToString(
                            CultureInfo.InvariantCulture),
                        ["dirtyDocumentCount"] =
                            snapshot.DirtyDocumentCount.ToString(
                                CultureInfo.InvariantCulture),
                    });
                return false;
            }

            bool closed = await _session.CloseCleanAttachedAsync(
                _profile.Xae.Solution,
                processId.Value,
                timeout,
                cancellationToken).ConfigureAwait(false);
            if (!closed)
            {
                _logger.Write(
                    LogLevel.Information,
                    "xae.close.shutdown.skipped",
                    "XAE shutdown cleanup was skipped because an open document has unsaved changes.",
                    properties: new Dictionary<string, string>
                    {
                        ["processId"] = processId.Value.ToString(
                            CultureInfo.InvariantCulture),
                    });
                return false;
            }

            _xaeCloseConsent.Clear(
                _profile.Name,
                processId.Value);
            _capabilitySnapshots.RefreshProfile(_profile);
            _logger.Write(
                LogLevel.Information,
                "xae.close.shutdown.completed",
                "The consented clean XAE process exited during Gateway shutdown.",
                properties: new Dictionary<string, string>
                {
                    ["processId"] = processId.Value.ToString(
                        CultureInfo.InvariantCulture),
                    ["ownership"] = snapshot.Ownership.ToString(),
                });
            return true;
        }
        catch (GatewayOperationException exception)
        {
            _logger.Write(
                LogLevel.Information,
                "xae.close.shutdown.denied",
                "XAE shutdown cleanup was not authorized or did not complete.",
                properties: new Dictionary<string, string>
                {
                    ["code"] = exception.Code,
                    ["stage"] = exception.Stage
                        ?? "xae.close.shutdown",
                },
                exception: exception);
            return false;
        }
        catch (Exception exception)
        {
            _logger.Write(
                LogLevel.Warning,
                "xae.close.shutdown.failed",
                "XAE shutdown cleanup failed; Gateway shutdown will continue.",
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
        _xaeCloseConsent.Clear(_profile.Name);
        _capabilitySnapshots.RefreshProfile(_profile);
        _session.Dispose();
        _wakeSignal.Dispose();
    }

    private bool HasActiveChangingOperation()
    {
        return !string.IsNullOrWhiteSpace(
            _status.Read().CurrentOperationId);
    }

    private void PublishAttaching()
    {
        _status.Update(status =>
        {
            status.ObservedAtUtc = DateTimeOffset.UtcNow;
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
        int processId = selected.ProcessId
            ?? throw new InvalidOperationException(
                "A connected XAE snapshot has no process identity.");
        XaeCloseConsentState closeConsent =
            _xaeCloseConsent.Observe(
                _profile.Name,
                processId,
                snapshot.Ownership);
        _capabilitySnapshots.RefreshProfile(
            _profile,
            new CapabilityEvaluationContext(processId));
        _runtimeMonitor?.PublishXaeObservation(
            snapshot.TwinCatSystem);
        _runtimeMonitor?.UpdateProject(
            snapshot.TwinCatProjectPath);
        _status.Update(status =>
        {
            if (status.State != GatewayProcessState.Stopping
                && status.CurrentOperationId is null)
            {
                status.State = GatewayProcessState.Ready;
            }
            status.ObservedAtUtc = DateTimeOffset.UtcNow;
            return status;
        });
        if (logConnection)
        {
            Dictionary<string, string> properties = new()
            {
                ["processId"] =
                    processId.ToString(
                        CultureInfo.InvariantCulture),
                ["progId"] = selected.ProgId ?? "unknown",
                ["solution"] = selected.Solution ?? "unknown",
                ["ownership"] = snapshot.Ownership.ToString(),
                ["closeConsented"] =
                    closeConsent.Consented.ToString(),
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
        _xaeCloseConsent.Clear(_profile.Name, processId);
        _capabilitySnapshots.RefreshProfile(_profile);
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
        _runtimeMonitor?.MarkXaeUnavailable(
            "The selected XAE process exited.");
        _status.Update(status =>
        {
            status.ObservedAtUtc = DateTimeOffset.UtcNow;
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
        RefreshSourceManifest(CancellationToken.None);
        if (wasConnected && !retainAttachment)
        {
            _runtimeMonitor?.MarkXaeUnavailable(
                "The configured XAE session disconnected.");
            _sourceManifests.MarkStale(
                code ?? ErrorCodes.XaeNotFound,
                "The source manifest is stale because the configured "
                    + "XAE session disconnected.");
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

    internal static GatewayStateSnapshot ApplyFailureStatus(
        GatewayStateSnapshot status,
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

        if (status.State != GatewayProcessState.Stopping)
        {
            status.State = GatewayProcessState.Ready;
        }
        status.ObservedAtUtc = DateTimeOffset.UtcNow;
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
        finally
        {
            _xaeCloseConsent.Clear(_profile.Name);
            _capabilitySnapshots.RefreshProfile(_profile);
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
            Ownership = source.Ownership,
            AgentWorkspaceOwned = source.AgentWorkspaceOwned,
            ClosedDocumentCount = source.ClosedDocumentCount,
            DiscardedDocumentCount =
                source.DiscardedDocumentCount,
            SynchronizationState =
                source.SynchronizationState,
            DirtyDocumentCount =
                source.DirtyDocumentCount,
            DirtyDocuments = source.DirtyDocuments.ToArray(),
            UnsynchronizedFiles =
                source.UnsynchronizedFiles
                    .Select(CloneProjectFileChange)
                    .ToArray(),
            ActiveConfiguration =
                source.ActiveConfiguration,
            ActivePlatform = source.ActivePlatform,
            TargetAmsNetId = source.TargetAmsNetId,
            TwinCatSystem = CloneTwinCatSystem(
                source.TwinCatSystem),
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

    private static XaeTwinCatSystemObservation?
        CloneTwinCatSystem(
        XaeTwinCatSystemObservation? source)
    {
        return source is null
            ? null
            : new XaeTwinCatSystemObservation
            {
                Source = source.Source,
                State = source.State,
                RawState = source.RawState,
                SelectedTarget = source.SelectedTarget,
                ObservedAtUtc = source.ObservedAtUtc,
                Freshness = source.Freshness,
                Error = source.Error is null
                    ? null
                    : new ObservationError
                    {
                        Code = source.Error.Code,
                        Message = source.Error.Message,
                        Retryable = source.Error.Retryable,
                    },
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
            ["solution"] = _profile.Xae.Solution,
            ["amsNetId"] = amsNetId,
        };
        string? targetName = _profile.Target?.Name;
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
            XaeAcceptedProjectGraphChanges accepted =
                await _session.AcceptProjectGraphChangesAsync(
                    scope,
                    timeout,
                    cancellationToken).ConfigureAwait(false);
            RefreshSourceManifest(cancellationToken);
            return accepted;
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
                OperationKind.XaeBuild,
            "synchronize" => OperationKind.Synchronize,
            "activate" => OperationKind.Activate,
            "targetconfig" => OperationKind.TargetConfig,
            "targetstartrestart" => OperationKind.TargetStartRestart,
            "opensolution" => OperationKind.XaeOpen,
            _ => null,
        };
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

    private void VerifyTarget(
        XaeSessionSnapshot snapshot,
        string stage)
    {
        ProfileResolver.EnsureTargetIdentity(
            _profile,
            snapshot.TargetAmsNetId,
            stage);
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

    private void RefreshSourceManifest(
        CancellationToken cancellationToken)
    {
        try
        {
            TwinCatProjectGraphSnapshot graph =
                _session.ResolveProjectGraph(
                    _profile.Xae.Solution,
                    cancellationToken);
            _sourceManifests.Refresh(graph);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            GatewayOperationException? operationException =
                FindGatewayOperationException(exception);
            string code = operationException?.Code
                ?? ErrorCodes.ProjectGraphInvalid;
            string message =
                "The source manifest could not be refreshed: "
                + exception.Message;
            if (_sourceManifests.DiscoveryState
                == SourceDiscoveryState.Unknown)
            {
                _sourceManifests.MarkUnavailable(
                    code,
                    message);
            }
            else
            {
                _sourceManifests.MarkStale(
                    code,
                    message);
            }

            _logger.Write(
                LogLevel.Warning,
                "xae.sourceManifest.refreshFailed",
                message,
                properties: new Dictionary<string, string>
                {
                    ["code"] = code,
                },
                exception: exception);
        }
    }

    private void PublishSynchronizationRequired()
    {
        _session.MarkSynchronizationRequired();
        _sourceManifests.MarkStale(
            ErrorCodes.ProjectGraphInvalid,
            "The source manifest is stale because the project graph "
                + "requires synchronization.");
        _status.Update(status =>
        {
            status.ObservedAtUtc = DateTimeOffset.UtcNow;
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
            OperationArtifactKind.Build,
            FormatBuildOutput(execution.Output));
        return new ActivationCompileResult
        {
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

    private static OperationStageResult<SynchronizeResult>
        CreateSucceededSyncStage(
            string operationId,
            SynchronizeResult result)
    {
        return new OperationStageResult<SynchronizeResult>
        {
            OperationId = operationId,
            Component = GatewayComponent.Xae,
            Stage = "activation.sync",
            Completion = OperationCompletion.Succeeded,
            SideEffectsStarted =
                result.SynchronizedFileCount > 0
                || result.DiscardedDocumentCount > 0,
            Result = result,
        };
    }

    private static OperationStageResult<ActivationCompileResult>
        CreateCompileStage(
            string operationId,
            ActivationCompileResult result,
            OperationCompletion completion)
    {
        OperationStageResult<ActivationCompileResult> stage = new()
        {
            OperationId = operationId,
            Component = GatewayComponent.Xae,
            Stage = "activation.compile",
            Completion = completion,
            SideEffectsStarted = true,
            Result = result,
        };
        if (completion == OperationCompletion.Failed)
        {
            stage.Error = new GatewayError
            {
                Code = ErrorCodes.ActivationCompileFailed,
                Message = "XAE activation compilation completed with errors.",
                OperationId = operationId,
                Component = GatewayComponent.Xae,
                Stage = stage.Stage,
                SideEffectsStarted = true,
                RawLogRef = result.Log?.Uri,
            };
        }
        if (result.Log is not null)
        {
            stage.Resources.Add(result.Log);
        }
        return stage;
    }

    private static OperationStageResult<ActivationDeployResult>
        CreateDeployStage(
            string operationId,
            AutostartBootProjectSelection autostartSelection,
            bool physicalActivationVerified)
    {
        return new OperationStageResult<ActivationDeployResult>
        {
            OperationId = operationId,
            Component = GatewayComponent.Xae,
            Stage = "activation.deploy",
            Completion = OperationCompletion.Succeeded,
            SideEffectsStarted = true,
            Result = new ActivationDeployResult
            {
                ConfigurationStored = true,
                PhysicalActivationVerified = physicalActivationVerified,
                AutostartBootProjects = autostartSelection,
            },
        };
    }

    private static OperationStageResult<ActivationDeployResult>
        CreateSkippedDeployStage(string operationId)
    {
        return new OperationStageResult<ActivationDeployResult>
        {
            OperationId = operationId,
            Component = GatewayComponent.Xae,
            Stage = "activation.deploy",
            Completion = OperationCompletion.Skipped,
            SideEffectsStarted = false,
        };
    }

    private static OperationStageResult<ActivationTargetTransitionResult>
        CreateTargetTransitionStage(
            string operationId,
            ActivationFinalTargetMode requestedMode,
            TargetSystemObservation? observation)
    {
        return new OperationStageResult<ActivationTargetTransitionResult>
        {
            OperationId = operationId,
            Component = GatewayComponent.Target,
            Stage = "activation.targetTransition",
            Completion = requestedMode == ActivationFinalTargetMode.Run
                ? OperationCompletion.Succeeded
                : OperationCompletion.Skipped,
            SideEffectsStarted =
                requestedMode == ActivationFinalTargetMode.Run,
            Result = new ActivationTargetTransitionResult
            {
                RequestedMode = requestedMode,
                Observation = observation,
            },
        };
    }

    private static OperationStageResult<ActivationTargetTransitionResult>
        CreateSkippedTargetTransitionStage(
            string operationId,
            ActivationFinalTargetMode requestedMode)
    {
        return new OperationStageResult<ActivationTargetTransitionResult>
        {
            OperationId = operationId,
            Component = GatewayComponent.Target,
            Stage = "activation.targetTransition",
            Completion = OperationCompletion.Skipped,
            SideEffectsStarted = false,
            Result = new ActivationTargetTransitionResult
            {
                RequestedMode = requestedMode,
            },
        };
    }

    private static OperationStageResult<TestResult>
        CreateSkippedVerificationStage(string operationId)
    {
        return new OperationStageResult<TestResult>
        {
            OperationId = operationId,
            Component = GatewayComponent.Verification,
            Stage = "activation.verification",
            Completion = OperationCompletion.Skipped,
            SideEffectsStarted = false,
        };
    }

    private async Task<TargetSystemObservation>
        WaitForDirectTargetRunAsync(
            DateTimeOffset deadlineUtc,
            CancellationToken cancellationToken)
    {
        while (DateTimeOffset.UtcNow < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remaining = deadlineUtc - DateTimeOffset.UtcNow;
            TimeSpan readTimeout = remaining < TimeSpan.FromSeconds(3)
                ? remaining
                : TimeSpan.FromSeconds(3);
            TargetSystemObservation observation =
                await ReadDirectTargetObservationAsync(
                    readTimeout,
                    cancellationToken).ConfigureAwait(false);
            if (observation.Freshness == ObservationFreshness.Fresh
                && observation.Error is null
                && observation.State == TargetSystemState.Run)
            {
                return observation;
            }
            if (observation.Freshness == ObservationFreshness.Fresh
                && observation.Error is null
                && observation.State == TargetSystemState.Exception)
            {
                string? details = await ReadRuntimeExceptionDetailsAsync(
                        readTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                throw new GatewayOperationException(
                    ErrorCodes.TargetTransitionFailed,
                    "Activation did not reach Run because the Target entered Exception.",
                    details: details,
                    retryable: false,
                    stage: "activation.targetTransition",
                    component: GatewayComponent.Target,
                    sideEffectsStarted: true);
            }
            await Task.Delay(
                TimeSpan.FromMilliseconds(250),
                cancellationToken).ConfigureAwait(false);
        }

        throw new GatewayOperationException(
            ErrorCodes.TargetTransitionFailed,
            "Target did not reach a freshly observed Run state after activation.",
            retryable: true,
            stage: "activation.targetTransition",
            component: GatewayComponent.Target,
            sideEffectsStarted: true);
    }

    private string FormatActivationLog(
        string operationId,
        string amsNetId,
        TargetSystemState initialRuntimeMode,
        AutostartBootProjectSelection autostartSelection,
        IReadOnlyList<XaeDialogObservation> dialogs,
        ActivationCompileResult compile,
        ActivationFinalTargetMode finalTargetMode,
        bool activeConfigurationVerified,
        TargetSystemObservation runtime,
        long durationMs)
    {
        StringBuilder builder = new();
        builder.AppendLine($"OperationId: {operationId}");
        builder.AppendLine($"Profile: {_profile.Name}");
        builder.AppendLine($"Solution: {_profile.Xae.Solution}");
        builder.AppendLine($"TargetAmsNetId: {amsNetId}");
        string? targetName = _profile.Target?.Name;
        if (!string.IsNullOrWhiteSpace(targetName))
        {
            builder.AppendLine($"TargetName: {targetName}");
        }
        builder.AppendLine(
            $"InitialRuntimeMode: {initialRuntimeMode}");
        builder.AppendLine(
            $"FinalTargetMode: {finalTargetMode}");
        builder.AppendLine(
            $"AutostartBootProjects: {autostartSelection}");
        builder.AppendLine($"CompileOk: {compile.Ok}");
        builder.AppendLine(
            $"CompileFailedProjects: {compile.FailedProjects}");
        builder.AppendLine(
            $"CompileErrors: {compile.Counts.Errors}");
        builder.AppendLine(
            $"CompileWarnings: {compile.Counts.Warnings}");
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
            $"FinalRuntimeMode: {runtime.State}");
        builder.AppendLine(
            $"FinalAdsState: "
                + $"{runtime.RawAdsStateName ?? "unknown"}");
        builder.AppendLine($"DurationMs: {durationMs}");
        return builder.ToString();
    }

    private bool IsEffective(CapabilityKey key)
    {
        return _capabilities.Evaluate(_profile, key).Effective;
    }

    private void EnsureProfileIdentity(
        string? requestedProfile,
        string stage)
    {
        if (string.IsNullOrWhiteSpace(requestedProfile)
            || string.Equals(
                requestedProfile,
                _profile.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new GatewayOperationException(
            ErrorCodes.XaeSolutionMismatch,
            $"Profile '{requestedProfile}' is not the active XAE context.",
            stage: stage,
            component: GatewayComponent.Xae,
            sideEffectsStarted: false,
            expected: new IdentityEvidence
            {
                Profile = requestedProfile,
            },
            observed: new IdentityEvidence
            {
                Profile = _profile.Name,
                Solution = _profile.Xae.Solution,
            });
    }

    private TargetIdentity? CreateTarget(
        XaeSessionSnapshot snapshot)
    {
        if (snapshot.TargetAmsNetId is null)
        {
            return null;
        }

        string? expectedAmsNetId =
            _profile.Target?.AmsNetId;
        return new TargetIdentity
        {
            Name = string.Equals(
                expectedAmsNetId,
                snapshot.TargetAmsNetId,
                StringComparison.OrdinalIgnoreCase)
                ? _profile.Target?.Name
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
