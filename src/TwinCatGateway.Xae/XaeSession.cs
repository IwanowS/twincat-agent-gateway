using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell.Interop;
using TCatSysManagerLib;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using OleServiceProvider =
    Microsoft.VisualStudio.OLE.Interop.IServiceProvider;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace TwinCatGateway.Xae;

public sealed class XaeSession : IDisposable
{
    private static readonly TimeSpan ActivationDialogSettleTimeout =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PollCallLimit =
        TimeSpan.FromSeconds(3);
    private static readonly string[] ErrorMessageSeparators =
    {
        "\r\n",
        "\n",
        "\r",
    };
    private readonly ComStaDispatcher _dispatcher;
    private readonly object _dialogSync = new();
    private readonly object _fingerprintSync = new();
    private readonly bool _ownsDispatcher;
    private XaeDialogSupervisor? _dialogSupervisor;
    private DTE2? _dte;
    private XaeBuildEventLease? _activeBuild;
    private ProjectFileFingerprintSnapshot? _fingerprintBaseline;
    private int? _fingerprintProcessId;
    private string? _fingerprintMoniker;
    private bool _fingerprintLaunchedByGateway;
    private SynchronizationState _fingerprintState =
        SynchronizationState.Uninitialized;
    private string? _fingerprintSolution;
    private string? _fingerprintTwinCatProjectPath;
    private ITcSysManager? _sysManager;
    private TwinCatSilentModeLease? _silentModeLease;
    private XaeWorkspaceFileChangeGuard? _workspaceFileChangeGuard;
    private string? _twinCatProjectPath;
    private string[] _projectGraphPaths = Array.Empty<string>();
    private XaeSessionSnapshot _snapshot = new();
    private int _disposed;

    public XaeSession(ComStaDispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? new ComStaDispatcher();
        _ownsDispatcher = dispatcher is null;
    }

    public event EventHandler<XaeDialogObservationEventArgs>?
        DialogObserved;

    public Task<XaeSessionSnapshot> DiscoverAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _dispatcher.InvokeAsync(
            DiscoverOnSta,
            timeout,
            cancellationToken);
    }

    public async Task<XaeSessionSnapshot> AttachAsync(
        string solutionPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return await AttachAsync(
            solutionPath,
            assumeAttachedXaeSynchronized: false,
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<XaeSessionSnapshot> AttachAsync(
        string solutionPath,
        bool assumeAttachedXaeSynchronized,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string normalizedSolution = NormalizeSolutionPath(solutionPath);
        DateTimeOffset deadlineUtc = CreateDeadline(timeout);
        XaeSessionSnapshot snapshot =
            await _dispatcher.InvokeAsync(
                () => AttachOnSta(
                    normalizedSolution,
                    assumeAttachedXaeSynchronized),
                GetRemaining(
                    deadlineUtc,
                    "xae.attach"),
                cancellationToken).ConfigureAwait(false);
        EnsureDialogSupervisor(
            snapshot.SelectedInstance?.ProcessId
                ?? throw new GatewayOperationException(
                    ErrorCodes.XaeNotFound,
                    "The selected XAE process identity is unavailable.",
                    retryable: true,
                    stage: "xae.attach"));
        if (snapshot.SynchronizationState
            != SynchronizationState.Confirmed)
        {
            RequireSynchronization(
                normalizedSolution,
                cancellationToken);
        }
        GetRemaining(
            deadlineUtc,
            "xae.workspace.fingerprint");
        return snapshot;
    }

    public async Task<XaeSessionSnapshot> EnsureAttachedAsync(
        string solutionPath,
        bool allowLaunch,
        string? configuredProgId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return await EnsureAttachedAsync(
            solutionPath,
            allowLaunch,
            configuredProgId,
            assumeAttachedXaeSynchronized: false,
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<XaeSessionSnapshot> EnsureAttachedAsync(
        string solutionPath,
        bool allowLaunch,
        string? configuredProgId,
        bool assumeAttachedXaeSynchronized,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return await EnsureAttachedAsync(
            solutionPath,
            allowLaunch,
            configuredProgId,
            assumeAttachedXaeSynchronized,
            beforeLaunch: null,
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<XaeSessionSnapshot> EnsureAttachedAsync(
        string solutionPath,
        bool allowLaunch,
        string? configuredProgId,
        bool assumeAttachedXaeSynchronized,
        Action? beforeLaunch,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        DateTimeOffset deadlineUtc = CreateDeadline(timeout);
        try
        {
            return await AttachAsync(
                solutionPath,
                assumeAttachedXaeSynchronized,
                GetRemaining(deadlineUtc, "xae.attach"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayOperationException exception) when (
            exception.Code == ErrorCodes.XaeNotFound
            && allowLaunch)
        {
            beforeLaunch?.Invoke();
            return await LaunchAsync(
                solutionPath,
                configuredProgId,
                GetRemaining(deadlineUtc, "xae.launch"),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<XaeSessionSnapshot> LaunchAsync(
        string solutionPath,
        string? configuredProgId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string normalizedSolution = NormalizeSolutionPath(solutionPath);
        if (!File.Exists(normalizedSolution))
        {
            throw new GatewayOperationException(
                ErrorCodes.SolutionNotFound,
                $"Solution '{normalizedSolution}' was not found.",
                stage: "xae.launch");
        }

        DateTimeOffset deadlineUtc = CreateDeadline(timeout);
        IReadOnlyList<XaeLaunchCandidate> candidates =
            XaeProgIdResolver.ResolveCandidates(configuredProgId);
        StartedXaeProcess started = StartFirstAvailable(candidates);
        EnsureDialogSupervisor(started.ProcessId);
        using XaeDialogOperationScope dialogScope =
            BeginDialogOperation(
                $"xae-launch-{started.ProcessId}",
                "opensolution",
                "xae.launch");

        await dialogScope.ObserveAsync(
            WaitForLaunchedDteAsync(
            started,
            deadlineUtc,
            cancellationToken)).ConfigureAwait(false);
        dialogScope.SetStage("xae.openSolution");
        await dialogScope.ObserveAsync(
            _dispatcher.InvokeAsync(
            () =>
            {
                OpenSolutionOnSta(
                    normalizedSolution,
                    started.ProcessId);
                return true;
            },
            GetRemaining(deadlineUtc, "xae.openSolution"),
            cancellationToken)).ConfigureAwait(false);
        XaeSessionSnapshot snapshot =
            await dialogScope.ObserveAsync(
                WaitForLaunchedSolutionAsync(
            normalizedSolution,
            started.ProcessId,
            deadlineUtc,
            cancellationToken)).ConfigureAwait(false);
        ConfirmFingerprintBaseline(
            normalizedSolution,
            cancellationToken);
        GetRemaining(
            deadlineUtc,
            "xae.workspace.fingerprint");
        return snapshot;
    }

    public Task<XaeSessionSnapshot> GetSnapshotAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _dispatcher.InvokeAsync(
            () => CloneSnapshot(_snapshot),
            timeout,
            cancellationToken);
    }

    public TwinCatProjectGraphSnapshot ResolveProjectGraph(
        string solutionPath,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string normalizedSolution = NormalizeSolutionPath(solutionPath);
        string twinCatProjectPath;
        lock (_fingerprintSync)
        {
            twinCatProjectPath = _twinCatProjectPath
                ?? throw new GatewayOperationException(
                    ErrorCodes.SysManagerNotAvailable,
                    "The selected TwinCAT project path is unavailable.",
                    retryable: true,
                    stage: "xae.workspace.graph");
        }

        return TwinCatProjectGraphResolver.Resolve(
            normalizedSolution,
            twinCatProjectPath,
            cancellationToken);
    }

    public XaeSessionSnapshot RefreshSynchronizationStatus(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        FingerprintState state = GetFingerprintState();
        if (state.Baseline is null)
        {
            lock (_fingerprintSync)
            {
                _snapshot.UnsynchronizedFiles =
                    Array.Empty<ProjectFileChange>();
                _snapshot.SynchronizationState =
                    SynchronizationState.SyncRequired;
                _fingerprintState =
                    SynchronizationState.SyncRequired;
                return CloneSnapshot(_snapshot);
            }
        }

        ProjectFileFingerprintSnapshot current =
            CaptureProjectGraph(
                state.SolutionPath,
                state.TwinCatProjectPath,
                cancellationToken);
        ProjectFileChange[] changes =
            ProjectFileFingerprintScanner.Compare(
                state.Baseline,
                current)
                .Where(change =>
                    change.Role
                        != ProjectGraphFileRole.GeneratedArtifact)
                .ToArray();
        lock (_fingerprintSync)
        {
            _snapshot.UnsynchronizedFiles =
                changes.Select(CloneProjectFileChange).ToArray();
            _snapshot.SynchronizationState =
                changes.Length == 0
                    ? SynchronizationState.Confirmed
                    : SynchronizationState.SyncRequired;
            _fingerprintState =
                _snapshot.SynchronizationState;
            return CloneSnapshot(_snapshot);
        }
    }

    public void MarkSynchronizationRequired()
    {
        ThrowIfDisposed();
        lock (_fingerprintSync)
        {
            _snapshot.SynchronizationState =
                SynchronizationState.SyncRequired;
            _fingerprintState =
                SynchronizationState.SyncRequired;
        }
    }

    public async Task<XaeSessionSnapshot> VerifyAttachedAsync(
        string solutionPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string normalizedSolution = NormalizeSolutionPath(solutionPath);
        XaeSessionSnapshot snapshot = await _dispatcher.InvokeAsync(
            () => VerifyAttachedOnSta(normalizedSolution),
            timeout,
            cancellationToken).ConfigureAwait(false);
        EnsureDialogSupervisor(
            snapshot.SelectedInstance?.ProcessId
                ?? throw new GatewayOperationException(
                    ErrorCodes.XaeNotFound,
                    "The selected XAE process identity is unavailable.",
                    retryable: true,
                    stage: "xae.verify"));
        return snapshot;
    }

    public async Task<CloseXaeResult> CloseAttachedAsync(
        string solutionPath,
        int processId,
        XaeSaveMode saveMode,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        string normalizedSolution =
            NormalizeSolutionPath(solutionPath);
        DateTimeOffset deadlineUtc =
            DateTimeOffset.UtcNow.Add(timeout);
        using DiagnosticsProcess? process =
            TryGetProcess(processId);
        if (process is null)
        {
            return new CloseXaeResult
            {
                Ok = true,
                Solution = normalizedSolution,
                ProcessId = processId,
                SaveMode = saveMode,
                ProcessExited = true,
            };
        }

        XaeCloseCommandOutcome command =
            await _dispatcher.InvokeAsync(
                () => CloseAttachedOnSta(
                    normalizedSolution,
                    processId,
                    saveMode),
                GetRemaining(
                    deadlineUtc,
                    "xae.close.command"),
                cancellationToken).ConfigureAwait(false);
        bool processExited =
            await WaitForProcessExitAsync(
                process,
                GetRemaining(
                    deadlineUtc,
                    "xae.close.verify"),
                cancellationToken).ConfigureAwait(false);
        if (!processExited)
        {
            string commandDetails =
                string.IsNullOrWhiteSpace(command.Error)
                    ? string.Empty
                    : $" Command error: {command.Error}";
            throw new GatewayOperationException(
                ErrorCodes.XaeCloseFailed,
                $"XAE process {processId} did not exit before the "
                    + $"close deadline.{commandDetails}",
                retryable: true,
                stage: "xae.close.verify",
                details: command.Error);
        }

        return new CloseXaeResult
        {
            Ok = true,
            Solution = normalizedSolution,
            ProcessId = processId,
            SaveMode = saveMode,
            ProcessExited = true,
            CommandErrorObserved = command.Error is not null,
        };
    }

    internal async Task<bool> CloseCleanAttachedAsync(
        string solutionPath,
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        string normalizedSolution =
            NormalizeSolutionPath(solutionPath);
        DateTimeOffset deadlineUtc =
            DateTimeOffset.UtcNow.Add(timeout);
        using DiagnosticsProcess? process =
            TryGetProcess(processId);
        if (process is null)
        {
            return true;
        }

        bool closeStarted = await _dispatcher.InvokeAsync(
            () => CloseCleanAttachedOnSta(
                normalizedSolution,
                processId),
            GetRemaining(
                deadlineUtc,
                "xae.close.shutdown.command"),
            cancellationToken).ConfigureAwait(false);
        if (!closeStarted)
        {
            return false;
        }

        bool processExited = await WaitForProcessExitAsync(
            process,
            GetRemaining(
                deadlineUtc,
                "xae.close.shutdown.verify"),
            cancellationToken).ConfigureAwait(false);
        if (!processExited)
        {
            throw new GatewayOperationException(
                ErrorCodes.XaeCloseFailed,
                $"XAE process {processId} did not exit before the shutdown cleanup deadline.",
                retryable: true,
                stage: "xae.close.shutdown.verify");
        }

        return true;
    }

    public XaeDialogOperationScope BeginDialogOperation(
        string operationId,
        string operationName,
        string stage,
        bool? runAfterActivation = null)
    {
        ThrowIfDisposed();
        lock (_dialogSync)
        {
            return (_dialogSupervisor
                    ?? throw new GatewayOperationException(
                        ErrorCodes.XaeNotFound,
                        "No selected XAE process is available for dialog "
                            + "monitoring.",
                        retryable: true,
                        stage: stage))
                .BeginOperation(
                    operationId,
                    operationName,
                    stage,
                    runAfterActivation);
        }
    }

    public async Task<XaeActivationCommandResult>
        ActivateConfigurationAsync(
        string solutionPath,
        string expectedAmsNetId,
        bool runAfterActivation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using XaeDialogOperationScope dialogScope =
            BeginDialogOperation(
                Guid.NewGuid().ToString("N"),
                "activate",
                "activation.activateConfiguration",
                runAfterActivation);
        return await ActivateConfigurationAsync(
            solutionPath,
            expectedAmsNetId,
            runAfterActivation,
            dialogScope,
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<XaeActivationCommandResult>
        ActivateConfigurationAsync(
        string solutionPath,
        string expectedAmsNetId,
        bool runAfterActivation,
        XaeDialogOperationScope dialogScope,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (dialogScope is null)
        {
            throw new ArgumentNullException(nameof(dialogScope));
        }

        string normalizedSolution =
            NormalizeSolutionPath(solutionPath);
        DateTimeOffset deadlineUtc = CreateDeadline(timeout);
        Task<XaeBuildEventEvidence> buildCompletion =
            await _dispatcher.InvokeAsync(
                () => StartBuildObservationOnSta(
                    BuildAction.Build),
                GetRemaining(
                    deadlineUtc,
                    "activation.compile.observe"),
                cancellationToken).ConfigureAwait(false);
        try
        {
            Task activation = _dispatcher.InvokeAsync(
                () =>
                {
                    ActivateConfigurationOnSta(
                        normalizedSolution,
                        expectedAmsNetId);
                    return true;
                },
                GetRemaining(
                    deadlineUtc,
                    "activation.activateConfiguration"),
                cancellationToken);
            await dialogScope.ObserveAsync(activation)
                .ConfigureAwait(false);
            TimeSpan dialogSettleTimeout = GetRemaining(
                deadlineUtc,
                "activation.dialog");
            if (dialogSettleTimeout > ActivationDialogSettleTimeout)
            {
                dialogSettleTimeout =
                    ActivationDialogSettleTimeout;
            }

            await dialogScope.WaitForActivationDialogOutcomeAsync(
                dialogSettleTimeout,
                cancellationToken).ConfigureAwait(false);
            XaeActivationCommandResult result =
                dialogScope.GetActivationResult();
            if (!result.ActivationConfirmed)
            {
                throw new GatewayOperationException(
                    ErrorCodes.ActivateConfigurationFailed,
                    "The TwinCAT Activate Configuration command did not "
                    + "present the expected activation confirmation "
                    + "dialog.",
                    retryable: false,
                    stage: "activation.dialog");
            }

            XaeBuildEventEvidence evidence =
                await WaitForBuildEventAsync(
                    buildCompletion,
                    deadlineUtc,
                    cancellationToken).ConfigureAwait(false);
            result.Build = await _dispatcher.InvokeAsync(
                () => CompleteBuildOnSta(
                    evidence,
                    ExternalChangeSynchronizationResult.None),
                GetRemaining(
                    deadlineUtc,
                    "activation.compile.verify"),
                cancellationToken).ConfigureAwait(false);
            if (result.Build.FailedProjects > 0
                || result.Build.Diagnostics.Any(
                    diagnostic =>
                        diagnostic.Severity
                            == DiagnosticSeverity.Error))
            {
                return result;
            }

            if (!result.RunDecisionHandled)
            {
                throw new GatewayOperationException(
                    ErrorCodes.ActivateConfigurationFailed,
                    "The TwinCAT Activate Configuration command did not "
                    + "present the expected Run confirmation dialog.",
                    retryable: false,
                    stage: "activation.dialog");
            }

            return result;
        }
        catch
        {
            await TryAbortActiveBuildAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task RestartTwinCatConfigModeAsync(
        string solutionPath,
        string expectedAmsNetId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string normalizedSolution =
            NormalizeSolutionPath(solutionPath);
        await _dispatcher.InvokeAsync(
            () =>
            {
                RestartTwinCatConfigModeOnSta(
                    normalizedSolution,
                    expectedAmsNetId);
                return true;
            },
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    internal Task<bool> ReadSilentModeAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _dispatcher.InvokeAsync(
            ReadSilentModeOnSta,
            timeout,
            cancellationToken);
    }

    public async Task DisconnectAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _dispatcher.InvokeAsync(
            () =>
            {
                ReleaseSessionOnSta();
                return true;
            },
            timeout,
            cancellationToken).ConfigureAwait(false);
        StopDialogSupervisor();
    }

    public async Task<ExternalChangeSynchronizationResult>
        SynchronizeExternalChangesAsync(
            IEnumerable<string>? changedPaths,
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        return await SynchronizeExternalChangesAsync(
            changedPaths,
            ExternalChangePolicy.ReloadModified,
            discardDirtyDocuments: false,
            allowDirtyDocumentDiscard: false,
            force: false,
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExternalChangeSynchronizationResult>
        SynchronizeExternalChangesAsync(
            IEnumerable<string>? changedPaths,
            ExternalChangePolicy policy,
            bool discardDirtyDocuments,
            bool allowDirtyDocumentDiscard,
            bool force,
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        DateTimeOffset deadlineUtc = CreateDeadline(timeout);
        FingerprintState state = GetFingerprintState();
        ProjectFileFingerprintSnapshot current =
            CaptureProjectGraph(
                state.SolutionPath,
                state.TwinCatProjectPath,
                cancellationToken);
        IReadOnlyList<ProjectFileChange> detected =
            state.Baseline is null
                ? Array.Empty<ProjectFileChange>()
                : ProjectFileFingerprintScanner.Compare(
                    state.Baseline,
                    current)
                    .Where(change =>
                        change.Role
                            != ProjectGraphFileRole
                                .GeneratedArtifact)
                    .ToArray();
        SetUnsynchronizedFiles(detected);
        HashSet<string> graphPaths = new(
            current.Files.Select(file => file.Path),
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> synchronizedGraphPaths = new(
            current.Files
                .Where(file =>
                    file.Role
                        != ProjectGraphFileRole
                            .GeneratedArtifact)
                .Select(file => file.Path),
            StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> dirtyDocuments =
            await _dispatcher.InvokeAsync(
                () => InspectDirtyDocumentsOnSta(
                    synchronizedGraphPaths),
                GetRemaining(
                    deadlineUtc,
                    "xae.workspace.dirty"),
                cancellationToken).ConfigureAwait(false);
        if (dirtyDocuments.Count != 0
            && !(discardDirtyDocuments
                && allowDirtyDocumentDiscard))
        {
            throw new GatewayOperationException(
                ErrorCodes.DirtyXaeDocument,
                dirtyDocuments.Count == 1
                    ? $"XAE document has unsaved changes: "
                        + $"'{dirtyDocuments[0]}'."
                    : $"{dirtyDocuments.Count} XAE documents have "
                        + "unsaved changes.",
                stage: "xae.workspace.dirty");
        }

        if (state.Baseline is null && !force)
        {
            throw new GatewayOperationException(
                ErrorCodes.XaeSyncRequired,
                "The attached XAE session has no confirmed disk baseline. "
                + "Run an explicit synchronization first.",
                stage: "xae.workspace.fingerprint");
        }

        if (changedPaths is not null)
        {
            foreach (string path in changedPaths)
            {
                string normalized = NormalizeChangedPath(
                    state.SolutionPath,
                    graphPaths,
                    path);
                if (!graphPaths.Contains(normalized))
                {
                    throw new GatewayOperationException(
                        ErrorCodes.RequestInvalid,
                        $"Changed path is outside the selected TwinCAT "
                        + $"project graph: '{normalized}'.",
                        stage: "xae.workspace.validate");
                }
            }
        }

        SynchronizationScope scope = SelectSynchronizationScope(
            detected,
            policy,
            force,
            state.Baseline is null);
        string[] sourcePaths = detected
            .Where(change =>
                change.Role == ProjectGraphFileRole.PlcSource
                && change.Kind == ProjectFileChangeKind.Modified)
            .Select(change => change.Path)
            .ToArray();
        ValidateChangedPlcObjects(
            sourcePaths,
            cancellationToken);
        GetRemaining(
            deadlineUtc,
            "xae.workspace.validate");
        bool discard = discardDirtyDocuments
            && allowDirtyDocumentDiscard;
        XaeDocumentSynchronizationResult synchronized =
            await _dispatcher.InvokeAsync(
                () => SynchronizeExternalChangesOnSta(
                    state.SolutionPath,
                    graphPaths,
                    scope == SynchronizationScope.ModifiedSources
                        ? sourcePaths
                        : Array.Empty<string>(),
                    discard),
                GetRemaining(
                    deadlineUtc,
                    "xae.workspace.synchronize"),
                cancellationToken).ConfigureAwait(false);
        IReadOnlyList<XaeProjectFileChangeResult> trackedProjectChanges =
            Array.Empty<XaeProjectFileChangeResult>();
        if (scope == SynchronizationScope.PlcProject
            || scope == SynchronizationScope.TwinCatProject)
        {
            trackedProjectChanges = await _dispatcher.InvokeAsync(
                () =>
                {
                    return ReloadTwinCatProjectOnSta(
                        state.SolutionPath,
                        state.TwinCatProjectPath);
                },
                GetRemaining(
                    deadlineUtc,
                    "xae.workspace.reloadProject"),
                cancellationToken).ConfigureAwait(false);
        }

        ProjectFileFingerprintSnapshot verified =
            CaptureProjectGraph(
                state.SolutionPath,
                state.TwinCatProjectPath,
                cancellationToken);
        IReadOnlyList<ProjectFileChange> concurrentChanges =
            ProjectFileFingerprintScanner.Compare(
                current,
                verified)
                .Where(change =>
                    change.Role
                        != ProjectGraphFileRole
                            .GeneratedArtifact)
                .ToArray();
        if (concurrentChanges.Count != 0)
        {
            SetUnsynchronizedFiles(concurrentChanges);
        }
        HashSet<string> provenNoisePaths = new(
            trackedProjectChanges
                .Where(change =>
                    change.Classification
                        == ProjectChangeClassification
                            .ExpectedReorderOnly
                    || change.Classification
                        == ProjectChangeClassification
                            .WhitespaceOnly)
                .Select(change => change.Path),
            StringComparer.OrdinalIgnoreCase);
        bool onlyTrackedNoise = concurrentChanges.All(change =>
            change.Role == ProjectGraphFileRole.TwinCatProject
            && change.Kind == ProjectFileChangeKind.Modified
            && provenNoisePaths.Contains(change.Path));
        if (concurrentChanges.Count != 0 && !onlyTrackedNoise)
        {
            throw new GatewayOperationException(
                ErrorCodes.ExternalEditSyncFailed,
                "Project source files changed while XAE synchronization "
                + "was running. Retry the operation.",
                retryable: true,
                stage: "xae.workspace.verify");
        }

        await _dispatcher.InvokeAsync(
            () =>
            {
                UpdateWorkspaceFileChangeGuardOnSta(verified);
                return true;
            },
            GetRemaining(
                deadlineUtc,
                "xae.workspace.guard"),
            cancellationToken).ConfigureAwait(false);
        GetRemaining(
            deadlineUtc,
            "xae.workspace.verify");
        ConfirmFingerprintBaseline(
            state.SolutionPath,
            verified);
        return new ExternalChangeSynchronizationResult(
            detected,
            synchronized.SynchronizedDocuments,
            synchronized.DiscardedDocuments,
            scope);
    }

    public async Task<XaeBuildExecutionResult> ExecuteBuildAsync(
        string solutionPath,
        BuildAction action,
        XaeBuildScope scope,
        string? project,
        IEnumerable<string>? changedPaths,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return await ExecuteBuildAsync(
            solutionPath,
            action,
            scope,
            project,
            changedPaths,
            ExternalChangePolicy.ReloadModified,
            autoSynchronizeBeforeOperation: true,
            beforeBuild: null,
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<XaeBuildExecutionResult> ExecuteBuildAsync(
        string solutionPath,
        BuildAction action,
        XaeBuildScope scope,
        string? project,
        IEnumerable<string>? changedPaths,
        ExternalChangePolicy externalChangePolicy,
        bool autoSynchronizeBeforeOperation,
        Action<bool>? beforeBuild,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        DateTimeOffset deadlineUtc = CreateDeadline(timeout);
        ExternalChangeSynchronizationResult synchronization =
            autoSynchronizeBeforeOperation
                ? await SynchronizeExternalChangesAsync(
                    changedPaths,
                    externalChangePolicy,
                    discardDirtyDocuments: false,
                    allowDirtyDocumentDiscard: false,
                    force: false,
                    GetRemaining(
                        deadlineUtc,
                        "xae.build.synchronize"),
                    cancellationToken).ConfigureAwait(false)
                : ExternalChangeSynchronizationResult.None;
        TwinCatProjectGraphSnapshot graph = ResolveProjectGraph(
            solutionPath,
            cancellationToken);
        ResolvedXaeBuildTarget target = XaeBuildTargetResolver.Resolve(
            graph,
            scope,
            project);
        bool synchronizationSideEffectsStarted =
            synchronization.SynchronizedDocuments.Count != 0
            || synchronization.DiscardedDocuments.Count != 0;
        beforeBuild?.Invoke(synchronizationSideEffectsStarted);
        using XaeProjectGraphChangeScope projectChanges =
            BeginProjectGraphChangeTracking();
        Task<XaeBuildEventEvidence> completion =
            await _dispatcher.InvokeAsync(
                () => StartBuildOnSta(
                    action,
                    target),
                GetRemaining(
                    deadlineUtc,
                    "xae.build.start"),
                cancellationToken).ConfigureAwait(false);
        try
        {
            XaeBuildEventEvidence evidence =
                await WaitForBuildEventAsync(
                    completion,
                    deadlineUtc,
                    cancellationToken).ConfigureAwait(false);
            XaeBuildExecutionResult result =
                await _dispatcher.InvokeAsync(
                () => CompleteBuildOnSta(
                    evidence,
                    synchronization),
                GetRemaining(
                    deadlineUtc,
                    "xae.build.verify"),
                cancellationToken).ConfigureAwait(false);
            result.AcceptedProjectChanges =
                await AcceptProjectGraphChangesAsync(
                    projectChanges,
                    GetRemaining(
                        deadlineUtc,
                        "xae.build.settle"),
                    cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            AbandonProjectGraphChanges(projectChanges);
            await TryAbortActiveBuildAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<bool> SelectBuildConfigurationAsync(
        string? configuration,
        string? platform,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return await _dispatcher.InvokeAsync(
            () => ApplyBuildConfigurationOnSta(
                configuration,
                platform),
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    public ComDiagnostics GetComDiagnostics()
    {
        ThrowIfDisposed();
        return _dispatcher.GetDiagnostics();
    }

    public Task<IReadOnlyList<BuildDiagnostic>>
        ReadErrorListAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _dispatcher.InvokeAsync(
            ReadErrorListOnSta,
            timeout,
            cancellationToken);
    }

    public XaeProjectGraphChangeScope
        BeginProjectGraphChangeTracking()
    {
        ThrowIfDisposed();
        FingerprintState state = GetFingerprintState();
        return new XaeProjectGraphChangeScope(
            state.SolutionPath,
            state.TwinCatProjectPath,
            state.Baseline
                ?? throw new GatewayOperationException(
                    ErrorCodes.XaeSyncRequired,
                    "The attached XAE session has no confirmed disk "
                        + "baseline.",
                    retryable: true,
                    stage: "xae.workspace.fingerprint"));
    }

    public async Task<XaeAcceptedProjectGraphChanges>
        AcceptProjectGraphChangesAsync(
        XaeProjectGraphChangeScope scope,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (scope is null)
        {
            throw new ArgumentNullException(nameof(scope));
        }

        try
        {
            XaeAcceptedProjectGraphChanges accepted =
                await scope.SettleAsync(
                    token => CaptureProjectGraph(
                        scope.SolutionPath,
                        scope.TwinCatProjectPath,
                        token),
                    timeout,
                    cancellationToken).ConfigureAwait(false);
            ConfirmFingerprintBaseline(
                scope.SolutionPath,
                accepted.Snapshot);
            return accepted;
        }
        catch
        {
            RequireSynchronization(
                scope.SolutionPath,
                CancellationToken.None);
            throw;
        }
    }

    public void AbandonProjectGraphChanges(
        XaeProjectGraphChangeScope scope)
    {
        ThrowIfDisposed();
        if (scope is null)
        {
            throw new ArgumentNullException(nameof(scope));
        }

        RequireSynchronization(
            scope.SolutionPath,
            CancellationToken.None);
    }

    internal async Task<bool> CloseGatewayLaunchedAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        try
        {
            return await _dispatcher.InvokeAsync(
                CloseGatewayLaunchedOnSta,
                timeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "Gateway-launched XAE cleanup did not complete: {0}",
                exception);
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _dispatcher.InvokeAsync(
                () =>
                {
                    ReleaseSessionOnSta();
                    return true;
                },
                TimeSpan.FromSeconds(5),
                CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "XAE session cleanup did not complete: {0}",
                exception);
        }
        finally
        {
            ClearFingerprintBaseline();
            StopDialogSupervisor();
            if (_ownsDispatcher)
            {
                _dispatcher.Dispose();
            }
        }
    }

    private static StartedXaeProcess StartFirstAvailable(
        IReadOnlyList<XaeLaunchCandidate> candidates)
    {
        List<Exception> failures = new();
        foreach (XaeLaunchCandidate candidate in candidates)
        {
            try
            {
                ProcessStartInfo startInfo = new()
                {
                    FileName = candidate.ExecutablePath,
                    UseShellExecute = false,
                    WorkingDirectory =
                        Path.GetDirectoryName(candidate.ExecutablePath)
                        ?? Environment.CurrentDirectory,
                };
                using System.Diagnostics.Process process =
                    System.Diagnostics.Process.Start(startInfo)
                    ?? throw new InvalidOperationException(
                        "The XAE process did not return a process handle.");
                return new StartedXaeProcess(
                    candidate.ProgId,
                    process.Id);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                Trace.TraceWarning(
                    "Could not launch XAE candidate '{0}': {1}",
                    candidate.ProgId,
                    exception);
            }
        }

        throw new GatewayOperationException(
            ErrorCodes.XaeLaunchFailed,
            "None of the configured TwinCAT XAE processes could be started.",
            retryable: true,
            stage: "xae.launch",
            innerException: new AggregateException(failures));
    }

    private async Task WaitForLaunchedDteAsync(
        StartedXaeProcess started,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        while (DateTimeOffset.UtcNow < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                bool attached = await _dispatcher.InvokeAsync(
                    () => TryAttachLaunchedProcessOnSta(started),
                    GetPollCallTimeout(deadlineUtc),
                    cancellationToken).ConfigureAwait(false);
                if (attached)
                {
                    return;
                }
            }
            catch (GatewayOperationException exception) when (
                exception.Code == ErrorCodes.ComCallTimeout
                && DateTimeOffset.UtcNow < deadlineUtc)
            {
                Trace.TraceWarning(
                    "Timed out while waiting for XAE process {0} to register in the ROT.",
                    started.ProcessId);
            }

            await DelayForPollAsync(
                deadlineUtc,
                cancellationToken).ConfigureAwait(false);
        }

        throw new GatewayOperationException(
            ErrorCodes.XaeLaunchFailed,
            $"XAE process {started.ProcessId} did not register '{started.ProgId}' in the Running Object Table before the deadline.",
            retryable: true,
            stage: "xae.waitForRot");
    }

    private async Task<XaeSessionSnapshot> WaitForLaunchedSolutionAsync(
        string normalizedSolution,
        int processId,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        while (DateTimeOffset.UtcNow < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                XaeSessionSnapshot? completed =
                    await _dispatcher.InvokeAsync(
                        () => TryCompleteLaunchedSessionOnSta(
                            normalizedSolution,
                            processId),
                        GetPollCallTimeout(deadlineUtc),
                        cancellationToken).ConfigureAwait(false);
                if (completed is not null)
                {
                    return completed;
                }
            }
            catch (GatewayOperationException exception) when (
                exception.Code == ErrorCodes.ComCallTimeout
                && DateTimeOffset.UtcNow < deadlineUtc)
            {
                Trace.TraceWarning(
                    "Timed out while waiting for XAE process {0} to finish loading the solution.",
                    processId);
            }

            await DelayForPollAsync(
                deadlineUtc,
                cancellationToken).ConfigureAwait(false);
        }

        throw new GatewayOperationException(
            ErrorCodes.SysManagerNotAvailable,
            "XAE opened the solution but the TwinCAT System Manager was not available before the deadline.",
            retryable: true,
            stage: "xae.sysManager");
    }

    private XaeSessionSnapshot DiscoverOnSta()
    {
        using RotScanResult scan = RunningObjectTableScanner.Scan();
        _snapshot = new XaeSessionSnapshot
        {
            Connected = _dte is not null && _sysManager is not null,
            SelectedInstance = CloneInfo(_snapshot.SelectedInstance),
            SysManagerAvailable = _sysManager is not null,
            LaunchedByGateway = _snapshot.LaunchedByGateway,
            Ownership = _snapshot.Ownership,
            DiscoveredInstances = scan.Candidates
                .Select(candidate => CloneInfo(candidate.Info)!)
                .ToArray(),
        };
        return CloneSnapshot(_snapshot);
    }

    private bool TryAttachLaunchedProcessOnSta(
        StartedXaeProcess started)
    {
        using RotScanResult scan = RunningObjectTableScanner.Scan(
            started.ProgId,
            started.ProcessId);
        RunningXaeCandidate? candidate = scan.Candidates
            .SingleOrDefault(item =>
                item.Dte is not null
                && item.Info.ProcessId == started.ProcessId
                && string.Equals(
                    item.Info.ProgId,
                    started.ProgId,
                    StringComparison.OrdinalIgnoreCase));
        if (candidate is null)
        {
            return false;
        }

        DTE2 dte = candidate.TakeDte();
        TwinCatSilentModeLease? silentModeLease = null;
        try
        {
            ReleaseSessionOnSta();
            silentModeLease = TwinCatSilentModeLease.Enable(
                dte,
                restoreOnDispose: true);
            _dte = dte;
            _silentModeLease = silentModeLease;
            silentModeLease = null;
        }
        catch
        {
            try
            {
                silentModeLease?.Dispose();
            }
            finally
            {
                ComObject.Release(dte);
            }

            throw;
        }

        DteInstanceInfo info = CloneInfo(candidate.Info)!;
        info.Selected = true;
        info.SelectionReason =
            "Exact gateway-launched XAE process and ProgID match.";
        _snapshot = new XaeSessionSnapshot
        {
            Connected = false,
            SelectedInstance = info,
            SysManagerAvailable = false,
            LaunchedByGateway = true,
            Ownership = XaeProcessOwnership.GatewayLaunched,
            DiscoveredInstances = new[] { CloneInfo(info)! },
        };
        return true;
    }

    private void OpenSolutionOnSta(
        string normalizedSolution,
        int processId)
    {
        EnsurePendingLaunchedProcess(processId);
        Solution? solution = null;
        try
        {
            DTE2 automation = _dte!;
            automation.UserControl = true;
            automation.SuppressUI = true;
            solution = automation.Solution;
            string? currentSolution =
                NormalizeOptionalPath(solution.FullName);
            if (currentSolution is null)
            {
                solution.Open(normalizedSolution);
                return;
            }

            if (!string.Equals(
                currentSolution,
                normalizedSolution,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new GatewayOperationException(
                    ErrorCodes.SolutionMismatch,
                    $"Gateway-launched XAE process {processId} opened a different solution.",
                    stage: "xae.openSolution");
            }
        }
        catch
        {
            ReleaseSessionOnSta();
            throw;
        }
        finally
        {
            ComObject.Release(solution);
        }
    }

    private XaeSessionSnapshot? TryCompleteLaunchedSessionOnSta(
        string normalizedSolution,
        int processId)
    {
        EnsurePendingLaunchedProcess(processId);
        DteInstanceInfo info = RunningObjectTableScanner.InspectDte(
            _snapshot.SelectedInstance?.Moniker
                ?? $"!{_snapshot.SelectedInstance?.ProgId}:{processId}",
            _dte!);
        if (info.Solution is null)
        {
            return null;
        }

        if (!string.Equals(
            info.Solution,
            normalizedSolution,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayOperationException(
                ErrorCodes.SolutionMismatch,
                $"Gateway-launched XAE process {processId} opened a different solution.",
                stage: "xae.openSolution");
        }

        ITcSysManager sysManager;
        string twinCatProjectPath;
        try
        {
            sysManager = AcquireSysManager(
                _dte!,
                normalizedSolution,
                out twinCatProjectPath);
        }
        catch (GatewayOperationException exception) when (
            exception.Code == ErrorCodes.SysManagerNotAvailable)
        {
            return null;
        }

        ProjectFileFingerprintSnapshot graph =
            CaptureProjectGraph(
                normalizedSolution,
                twinCatProjectPath,
                CancellationToken.None);
        int dirtyDocumentCount =
            AgentWorkspaceOwnership.FindDirtyDocuments(
                _dte!,
                graph.Files.Select(file => file.Path))
            .Count;
        _projectGraphPaths =
            graph.Files.Select(file => file.Path).ToArray();
        ComObject.Release(_sysManager);
        _sysManager = sysManager;
        _twinCatProjectPath = twinCatProjectPath;
        try
        {
            AcquireWorkspaceFileChangeGuardOnSta(graph);
        }
        catch
        {
            ReleaseSessionOnSta();
            throw;
        }

        info.Selected = true;
        info.SelectionReason =
            "Gateway-launched XAE opened the exact normalized solution path.";
        _snapshot = new XaeSessionSnapshot
        {
            Connected = true,
            SelectedInstance = CloneInfo(info),
            SysManagerAvailable = true,
            LaunchedByGateway = true,
            Ownership = XaeProcessOwnership.GatewayLaunched,
            AgentWorkspaceOwned =
                _workspaceFileChangeGuard?.IsActive == true,
            ClosedDocumentCount = 0,
            DiscardedDocumentCount = 0,
            SynchronizationState = SynchronizationState.Confirmed,
            DirtyDocumentCount = dirtyDocumentCount,
            DiscoveredInstances = new[] { CloneInfo(info)! },
        };
        using (CreateUserSilentModeLease())
        {
            RefreshDiagnosticsOnSta();
        }
        return CloneSnapshot(_snapshot);
    }

    private XaeSessionSnapshot AttachOnSta(
        string normalizedSolution,
        bool assumeAttachedXaeSynchronized)
    {
        using RotScanResult scan = RunningObjectTableScanner.Scan();
        DteInstanceInfo[] instances = scan.Candidates
            .Select(candidate => CloneInfo(candidate.Info)!)
            .ToArray();
        int selectedIndex;
        try
        {
            selectedIndex = XaeInstanceSelector.Select(
                instances,
                normalizedSolution);
        }
        catch
        {
            ReleaseSessionOnSta();
            _snapshot = new XaeSessionSnapshot
            {
                DiscoveredInstances = instances,
            };
            throw;
        }

        RunningXaeCandidate selected = scan.Candidates[selectedIndex];
        DTE2 selectedDte = selected.TakeDte();
        ITcSysManager? selectedSysManager = null;
        string twinCatProjectPath = string.Empty;
        string[] projectGraphPaths = Array.Empty<string>();
        ProjectFileFingerprintSnapshot? capturedGraph = null;
        int dirtyDocumentCount = 0;
        try
        {
            using (TwinCatSilentModeLease.Enable(
                selectedDte,
                restoreOnDispose: true))
            {
                selectedSysManager = AcquireSysManager(
                    selectedDte,
                    normalizedSolution,
                    out twinCatProjectPath);
                ProjectFileFingerprintSnapshot graph =
                    CaptureProjectGraph(
                        normalizedSolution,
                        twinCatProjectPath,
                        CancellationToken.None);
                capturedGraph = graph;
                dirtyDocumentCount =
                    AgentWorkspaceOwnership.FindDirtyDocuments(
                        selectedDte,
                        graph.Files.Select(file => file.Path))
                    .Count;
                projectGraphPaths =
                    graph.Files.Select(file => file.Path).ToArray();
            }
        }
        catch
        {
            ComObject.Release(selectedSysManager);
            ComObject.Release(selectedDte);
            throw;
        }

        try
        {
            ReleaseSessionOnSta();
        }
        catch
        {
            ComObject.Release(selectedSysManager);
            ComObject.Release(selectedDte);
            throw;
        }

        _dte = selectedDte;
        _sysManager = selectedSysManager;
        _twinCatProjectPath = twinCatProjectPath;
        _projectGraphPaths = projectGraphPaths;
        try
        {
            AcquireWorkspaceFileChangeGuardOnSta(
                capturedGraph
                    ?? throw new GatewayOperationException(
                        ErrorCodes.ProjectGraphInvalid,
                        "The attached XAE project graph is unavailable.",
                        stage: "xae.workspace.guard"));
        }
        catch
        {
            ReleaseSessionOnSta();
            throw;
        }

        bool retainBaseline = CanRetainFingerprintBaseline(
            instances[selectedIndex].ProcessId,
            instances[selectedIndex].Moniker,
            normalizedSolution,
            twinCatProjectPath);
        SynchronizationState initialSynchronizationState =
            SelectInitialSynchronizationState(
                retainBaseline,
                assumeAttachedXaeSynchronized,
                dirtyDocumentCount);
        instances[selectedIndex].Selected = true;
        instances[selectedIndex].SelectionReason =
            "Exact normalized Solution.FullName match.";
        _snapshot = new XaeSessionSnapshot
        {
            Connected = true,
            SelectedInstance = CloneInfo(instances[selectedIndex]),
            SysManagerAvailable = true,
            LaunchedByGateway =
                retainBaseline
                && _fingerprintLaunchedByGateway,
            Ownership = retainBaseline
                && _fingerprintLaunchedByGateway
                    ? XaeProcessOwnership.GatewayLaunched
                    : XaeProcessOwnership.Attached,
            AgentWorkspaceOwned =
                _workspaceFileChangeGuard?.IsActive == true,
            ClosedDocumentCount = 0,
            DiscardedDocumentCount = 0,
            SynchronizationState = initialSynchronizationState,
            DirtyDocumentCount = dirtyDocumentCount,
            DiscoveredInstances = instances,
        };
        using (CreateUserSilentModeLease())
        {
            RefreshDiagnosticsOnSta();
        }

        if (!retainBaseline
            && initialSynchronizationState
                == SynchronizationState.Confirmed)
        {
            ConfirmFingerprintBaseline(
                normalizedSolution,
                capturedGraph
                    ?? throw new GatewayOperationException(
                        ErrorCodes.ProjectGraphInvalid,
                        "The attached XAE project graph baseline is "
                            + "unavailable.",
                        stage: "xae.workspace.fingerprint"));
        }

        return CloneSnapshot(_snapshot);
    }

    internal static SynchronizationState
        SelectInitialSynchronizationState(
        bool retainBaseline,
        bool assumeAttachedXaeSynchronized,
        int dirtyDocumentCount)
    {
        if (dirtyDocumentCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dirtyDocumentCount));
        }

        return retainBaseline
            || (assumeAttachedXaeSynchronized
                && dirtyDocumentCount == 0)
            ? SynchronizationState.Confirmed
            : SynchronizationState.SyncRequired;
    }

    private XaeSessionSnapshot VerifyAttachedOnSta(
        string normalizedSolution)
    {
        if (_dte is null || _sysManager is null)
        {
            throw new GatewayOperationException(
                ErrorCodes.XaeNotFound,
                "No XAE session is currently attached.",
                retryable: true,
                stage: "xae.health");
        }

        _workspaceFileChangeGuard?.ThrowIfFaulted();

        DteInstanceInfo info;
        using (CreateUserSilentModeLease())
        {
            info = RunningObjectTableScanner.InspectDte(
                _snapshot.SelectedInstance?.Moniker ?? string.Empty,
                _dte);
        }
        if (!string.Equals(
            info.Solution,
            normalizedSolution,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayOperationException(
                ErrorCodes.SolutionMismatch,
                "The attached XAE no longer has the configured solution open.",
                retryable: true,
                stage: "xae.health");
        }

        info.Selected = true;
        info.SelectionReason =
            _snapshot.SelectedInstance?.SelectionReason;
        int dirtyDocumentCount =
            AgentWorkspaceOwnership.FindDirtyDocuments(
                _dte,
                _projectGraphPaths)
            .Count;
        _snapshot = new XaeSessionSnapshot
        {
            Connected = true,
            SelectedInstance = CloneInfo(info),
            SysManagerAvailable = true,
            LaunchedByGateway = _snapshot.LaunchedByGateway,
            Ownership = _snapshot.Ownership,
            AgentWorkspaceOwned =
                _workspaceFileChangeGuard?.IsActive == true,
            ClosedDocumentCount = _snapshot.ClosedDocumentCount,
            DiscardedDocumentCount = _snapshot.DiscardedDocumentCount,
            SynchronizationState =
                _snapshot.SynchronizationState,
            DirtyDocumentCount =
                dirtyDocumentCount,
            DiscoveredInstances = _snapshot.DiscoveredInstances
                .Select(instance =>
                    instance.Selected
                        ? CloneInfo(info)!
                        : CloneInfo(instance)!)
                .ToArray(),
        };
        using (CreateUserSilentModeLease())
        {
            RefreshDiagnosticsOnSta();
        }
        return CloneSnapshot(_snapshot);
    }

    private void ActivateConfigurationOnSta(
        string normalizedSolution,
        string expectedAmsNetId)
    {
        const string stage =
            "activation.activateConfiguration";
        using (TwinCatSilentModeLease.Disable(
            _dte
                ?? throw new GatewayOperationException(
                    ErrorCodes.XaeNotFound,
                    "No XAE session is currently attached.",
                    retryable: true,
                    stage: "xae.silentMode"),
            restoreOnDispose: true))
        {
            VerifyActivationBoundaryOnSta(
                normalizedSolution,
                expectedAmsNetId,
                stage);
            try
            {
                // This is intentionally the UI-equivalent XAE command.
                // It owns platform validation, the activation/autostart
                // confirmation, build and deployment, and the final
                // optional Run confirmation. Do not follow it with
                // ITcSysManager.StartRestartTwinCAT().
                _dte!.ExecuteCommand(
                    "TwinCAT.ActivateConfiguration");
            }
            catch (Exception exception)
            {
                throw new GatewayOperationException(
                    ErrorCodes.ActivateConfigurationFailed,
                    "The TwinCAT Activate Configuration command failed.",
                    retryable: false,
                    stage: stage,
                    innerException: exception);
            }
        }
    }

    private void RestartTwinCatConfigModeOnSta(
        string normalizedSolution,
        string expectedAmsNetId)
    {
        const string stage = "activation.recoverToConfig";
        using (CreateUserSilentModeLease())
        {
            VerifyActivationBoundaryOnSta(
                normalizedSolution,
                expectedAmsNetId,
                stage);
            try
            {
                _dte!.ExecuteCommand(
                    "TwinCAT.RestartTwinCATConfigMode");
            }
            catch (Exception exception)
            {
                throw new GatewayOperationException(
                    ErrorCodes.ConfigModeRecoveryFailed,
                    "TwinCAT Config Mode recovery request failed.",
                    retryable: true,
                    stage: stage,
                    innerException: exception);
            }
        }
    }

    private void VerifyActivationBoundaryOnSta(
        string normalizedSolution,
        string expectedAmsNetId,
        string stage)
    {
        if (string.IsNullOrWhiteSpace(expectedAmsNetId))
        {
            throw new GatewayOperationException(
                ErrorCodes.ProfileInvalid,
                "Activation profile has no expected AMS NetId.",
                stage: stage);
        }

        XaeSessionSnapshot snapshot =
            VerifyAttachedOnSta(normalizedSolution);
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

    private void RefreshDiagnosticsOnSta()
    {
        DTE2 dte = _dte
            ?? throw new GatewayOperationException(
                ErrorCodes.XaeNotFound,
                "No XAE session is currently attached.",
                retryable: true,
                stage: "xae.diagnostics");
        ITcSysManager sysManager = _sysManager
            ?? throw new GatewayOperationException(
                ErrorCodes.SysManagerNotAvailable,
                "The attached XAE session has no System Manager.",
                retryable: true,
                stage: "xae.diagnostics");
        List<string> issues = new();
        string? activeConfiguration = null;
        string? activePlatform = null;
        string? targetAmsNetId = null;
        XaeTwinCatSystemObservation? twinCatSystem = null;
        IReadOnlyList<string> lastErrorMessages =
            Array.Empty<string>();
        IReadOnlyList<BuildDiagnostic> errorListMessages =
            Array.Empty<BuildDiagnostic>();

        try
        {
            ReadActiveSolutionConfiguration(
                dte,
                out activeConfiguration,
                out activePlatform);
        }
        catch (Exception exception)
        {
            issues.Add(FormatDiagnosticIssue(
                "activeSolutionConfiguration",
                exception));
        }

        try
        {
            ITcSysManager2 sysManager2 =
                (ITcSysManager2)sysManager;
            targetAmsNetId = EmptyToNull(
                sysManager2.GetTargetNetId());
            lastErrorMessages = SplitErrorMessages(
                sysManager2.GetLastErrorMessages());
        }
        catch (Exception exception)
        {
            issues.Add(FormatDiagnosticIssue(
                "sysManager2",
                exception));
        }

        DateTimeOffset systemObservedAtUtc =
            DateTimeOffset.UtcNow;
        try
        {
            twinCatSystem =
                XaeTwinCatSystemStateMapper.FromStartedFlag(
                    sysManager.IsTwinCATStarted(),
                    targetAmsNetId,
                    systemObservedAtUtc);
        }
        catch (Exception exception)
        {
            issues.Add(FormatDiagnosticIssue(
                "twinCatSystemState",
                exception));
            twinCatSystem =
                XaeTwinCatSystemStateMapper.Unavailable(
                    targetAmsNetId,
                    systemObservedAtUtc,
                    "XAE could not observe the TwinCAT system state.");
        }

        try
        {
            errorListMessages =
                XaeErrorListReader.Read(dte);
        }
        catch (Exception exception)
        {
            issues.Add(FormatDiagnosticIssue(
                "errorList",
                exception));
        }

        _snapshot.ActiveConfiguration =
            activeConfiguration;
        _snapshot.ActivePlatform = activePlatform;
        _snapshot.TargetAmsNetId = targetAmsNetId;
        _snapshot.TwinCatSystem = twinCatSystem;
        _snapshot.TwinCatProjectPath =
            _twinCatProjectPath;
        _snapshot.LastErrorMessages =
            lastErrorMessages;
        _snapshot.ErrorListMessages =
            errorListMessages;
        _snapshot.DiagnosticIssues = issues;
    }

    private IReadOnlyList<BuildDiagnostic>
        ReadErrorListOnSta()
    {
        DTE2 dte = _dte
            ?? throw new GatewayOperationException(
                ErrorCodes.XaeNotFound,
                "No XAE session is currently attached.",
                retryable: true,
                stage: "xae.errorList.read");
        try
        {
            return XaeErrorListReader.Read(dte);
        }
        catch (GatewayOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new GatewayOperationException(
                ErrorCodes.OperationFailed,
                "XAE Error List could not be read.",
                retryable: true,
                stage: "xae.errorList.read",
                innerException: exception);
        }
    }

    private static void ReadActiveSolutionConfiguration(
        DTE2 dte,
        out string? activeConfiguration,
        out string? activePlatform)
    {
        Solution? solution = null;
        SolutionBuild? solutionBuild = null;
        SolutionConfiguration? configuration = null;
        activeConfiguration = null;
        activePlatform = null;
        try
        {
            solution = dte.Solution;
            solutionBuild = solution.SolutionBuild;
            configuration =
                solutionBuild.ActiveConfiguration;
            activeConfiguration = EmptyToNull(
                configuration.Name);
            string[] uniquePlatforms =
                ReadSolutionConfigurationPlatforms(
                    configuration);
            activePlatform = uniquePlatforms.Length == 1
                ? uniquePlatforms[0]
                : null;
        }
        finally
        {
            ComObject.Release(configuration);
            ComObject.Release(solutionBuild);
            ComObject.Release(solution);
        }
    }

    private static string[]
        ReadSolutionConfigurationPlatforms(
            SolutionConfiguration configuration)
    {
        SolutionContexts? contexts = null;
        List<string> platforms = new();
        try
        {
            contexts = configuration.SolutionContexts;
            int count = contexts.Count;
            for (int index = 1; index <= count; index++)
            {
                SolutionContext? context = null;
                try
                {
                    object itemIndex = index;
                    context = contexts.Item(itemIndex);
                    string? platform = EmptyToNull(
                        context.PlatformName);
                    if (platform is not null)
                    {
                        platforms.Add(platform);
                    }
                }
                finally
                {
                    ComObject.Release(context);
                }
            }

            return platforms
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    value => value,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            ComObject.Release(contexts);
        }
    }

    private static string[] SplitErrorMessages(
        string? messages)
    {
        if (string.IsNullOrWhiteSpace(messages))
        {
            return Array.Empty<string>();
        }

        return messages!
            .Split(
                ErrorMessageSeparators,
                StringSplitOptions.RemoveEmptyEntries)
            .Select(message => message.Trim())
            .Where(message => message.Length != 0)
            .Take(50)
            .Select(message =>
                message.Length <= 2000
                    ? message
                    : message.Substring(0, 2000))
            .ToArray();
    }

    private static string FormatDiagnosticIssue(
        string source,
        Exception exception)
    {
        return exception is COMException
            ? $"{source}: COM call failed "
                + $"(HRESULT 0x{exception.HResult:X8})."
            : $"{source}: {exception.GetType().Name}.";
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value;
    }

    private static ITcSysManager AcquireSysManager(
        DTE dte,
        string normalizedSolution,
        out string twinCatProjectPath)
    {
        Solution? solution = null;
        Projects? projects = null;
        List<ITcSysManager> sysManagers = new();
        List<string> projectPaths = new();
        bool ownershipTransferred = false;
        twinCatProjectPath = string.Empty;
        try
        {
            solution = dte.Solution;
            string? actualSolution = solution.FullName;
            if (!string.Equals(
                NormalizeOptionalPath(actualSolution),
                normalizedSolution,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new GatewayOperationException(
                    ErrorCodes.SolutionMismatch,
                    "The selected XAE solution changed during attach.",
                    retryable: true,
                    stage: "xae.attach");
            }

            projects = solution.Projects;
            int count = projects.Count;
            for (int index = 1; index <= count; index++)
            {
                Project? project = null;
                try
                {
                    project = projects.Item(index);
                    string? projectPath = project.FullName;
                    if (!string.Equals(
                        Path.GetExtension(projectPath),
                        ".tsproj",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    object? projectObject = project.Object;
                    if (projectObject is ITcSysManager sysManager)
                    {
                        sysManagers.Add(sysManager);
                        projectPaths.Add(Path.GetFullPath(projectPath));
                    }
                    else
                    {
                        ComObject.Release(projectObject);
                    }
                }
                finally
                {
                    ComObject.Release(project);
                }
            }

            if (sysManagers.Count != 1)
            {
                throw new GatewayOperationException(
                    ErrorCodes.SysManagerNotAvailable,
                    sysManagers.Count == 0
                        ? "No TwinCAT System Manager project was found in the solution."
                        : "Multiple TwinCAT System Manager projects are not supported by this profile.",
                    stage: "xae.sysManager");
            }

            twinCatProjectPath = projectPaths[0];
            ownershipTransferred = true;
            return sysManagers[0];
        }
        finally
        {
            if (!ownershipTransferred)
            {
                foreach (ITcSysManager sysManager in sysManagers)
                {
                    ComObject.Release(sysManager);
                }
            }

            ComObject.Release(projects);
            ComObject.Release(solution);
        }
    }

    private XaeDocumentSynchronizationResult
        SynchronizeExternalChangesOnSta(
            string solutionPath,
            IEnumerable<string> projectGraphPaths,
            IEnumerable<string> changedPaths,
            bool discardDirtyDocuments)
    {
        DTE2 dte = _dte
            ?? throw new GatewayOperationException(
                ErrorCodes.XaeNotFound,
                "No XAE session is currently attached.",
                retryable: true,
                stage: "xae.workspace.synchronize");
        string? attachedSolution =
            _snapshot.SelectedInstance?.Solution;
        if (!string.Equals(
            attachedSolution,
            solutionPath,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayOperationException(
                ErrorCodes.SolutionMismatch,
                "The attached XAE solution changed before "
                + "external files were synchronized.",
                retryable: true,
                stage: "xae.workspace.synchronize");
        }

        using (CreateUserSilentModeLease())
        {
            XaeDocumentSynchronizationResult result =
                ExternalChangeSynchronizer.Synchronize(
                    dte,
                    changedPaths,
                    projectGraphPaths,
                    discardDirtyDocuments,
                    _workspaceFileChangeGuard);
            _snapshot.AgentWorkspaceOwned =
                _workspaceFileChangeGuard?.IsActive == true;
            _snapshot.ClosedDocumentCount = 0;
            _snapshot.DiscardedDocumentCount =
                result.DiscardedDocuments.Count;
            _snapshot.DirtyDocumentCount = 0;
            return result;
        }
    }

    private IReadOnlyList<string> InspectDirtyDocumentsOnSta(
        IEnumerable<string> projectGraphPaths)
    {
        DTE2 dte = _dte
            ?? throw new GatewayOperationException(
                ErrorCodes.XaeNotFound,
                "No XAE session is currently attached.",
                retryable: true,
                stage: "xae.workspace.dirty");
        IReadOnlyList<string> dirty =
            AgentWorkspaceOwnership.FindDirtyDocuments(
                dte,
                projectGraphPaths);
        _snapshot.DirtyDocumentCount = dirty.Count;
        return dirty;
    }

    private Task<XaeBuildEventEvidence> StartBuildOnSta(
        BuildAction action,
        ResolvedXaeBuildTarget target)
    {
        if (_activeBuild is not null)
        {
            throw new GatewayOperationException(
                ErrorCodes.XaeBusy,
                "An XAE build operation is already active.",
                retryable: true,
                stage: "xae.build.start");
        }

        DTE2 dte = _dte
            ?? throw new GatewayOperationException(
                ErrorCodes.XaeNotFound,
                "No XAE session is currently attached.",
                retryable: true,
                stage: "xae.build.start");
        _activeBuild = XaeBuildEventLease.Start(
            dte,
            action,
            target.Scope,
            target.Project,
            target.ProjectFile,
            _workspaceFileChangeGuard);
        return _activeBuild.Completion;
    }

    private Task<XaeBuildEventEvidence>
        StartBuildObservationOnSta(BuildAction action)
    {
        if (_activeBuild is not null)
        {
            throw new GatewayOperationException(
                ErrorCodes.XaeBusy,
                "An XAE build operation is already active.",
                retryable: true,
                stage: "activation.compile.observe");
        }

        DTE2 dte = _dte
            ?? throw new GatewayOperationException(
                ErrorCodes.XaeNotFound,
                "No XAE session is currently attached.",
                retryable: true,
                stage: "activation.compile.observe");
        _activeBuild = XaeBuildEventLease.ObserveNext(
            dte,
            action,
            _workspaceFileChangeGuard);
        return _activeBuild.Completion;
    }

    private bool ApplyBuildConfigurationOnSta(
        string? requestedConfiguration,
        string? requestedPlatform)
    {
        if (string.IsNullOrWhiteSpace(requestedConfiguration)
            && string.IsNullOrWhiteSpace(requestedPlatform))
        {
            return false;
        }

        const string stage = "xae.build.configuration";
        DTE2 dte = _dte
            ?? throw new GatewayOperationException(
                ErrorCodes.XaeNotFound,
                "No XAE session is currently attached.",
                retryable: true,
                stage: stage);
        Solution? solution = null;
        SolutionBuild? solutionBuild = null;
        SolutionConfiguration? active = null;
        try
        {
            solution = dte.Solution;
            solutionBuild = solution.SolutionBuild;
            active = solutionBuild.ActiveConfiguration;
            string? activeName = EmptyToNull(active.Name);
            string[] activePlatforms =
                ReadSolutionConfigurationPlatforms(active);
            string? effectiveConfiguration =
                string.IsNullOrWhiteSpace(requestedConfiguration)
                    ? activeName
                    : requestedConfiguration;
            bool configurationMatches =
                string.IsNullOrWhiteSpace(requestedConfiguration)
                || string.Equals(
                    activeName,
                    requestedConfiguration,
                    StringComparison.OrdinalIgnoreCase);
            bool platformMatches =
                string.IsNullOrWhiteSpace(requestedPlatform)
                || (activePlatforms.Length == 1
                    && string.Equals(
                        activePlatforms[0],
                        requestedPlatform,
                        StringComparison.OrdinalIgnoreCase));
            bool changed =
                !configurationMatches || !platformMatches;
            if (!configurationMatches || !platformMatches)
            {
                if (string.IsNullOrWhiteSpace(
                        effectiveConfiguration))
                {
                    throw new GatewayOperationException(
                        ErrorCodes.BuildConfigurationNotFound,
                        "The active XAE solution configuration "
                        + "could not be identified.",
                        stage: stage);
                }

                ComObject.Release(active);
                active = null;
                ActivateSolutionConfiguration(
                    solutionBuild,
                    effectiveConfiguration!,
                    requestedPlatform);
                active = solutionBuild.ActiveConfiguration;
                activeName = EmptyToNull(active.Name);
                activePlatforms =
                    ReadSolutionConfigurationPlatforms(active);
            }

            ValidateBuildConfiguration(
                activeName,
                activePlatforms,
                requestedConfiguration,
                requestedPlatform);
            _snapshot.ActiveConfiguration = activeName;
            _snapshot.ActivePlatform =
                activePlatforms.Length == 1
                    ? activePlatforms[0]
                    : null;
            return changed;
        }
        catch (GatewayOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new GatewayOperationException(
                ErrorCodes.BuildConfigurationFailed,
                "The requested XAE build configuration "
                + "could not be selected or verified.",
                stage: stage,
                innerException: exception);
        }
        finally
        {
            ComObject.Release(active);
            ComObject.Release(solutionBuild);
            ComObject.Release(solution);
        }
    }

    private static void ActivateSolutionConfiguration(
        SolutionBuild solutionBuild,
        string requestedConfiguration,
        string? requestedPlatform)
    {
        const string stage = "xae.build.configuration";
        SolutionConfigurations? configurations = null;
        SolutionConfiguration? selected = null;
        try
        {
            configurations =
                solutionBuild.SolutionConfigurations;
            int count = configurations.Count;
            for (int index = 1; index <= count; index++)
            {
                SolutionConfiguration? candidate = null;
                try
                {
                    object itemIndex = index;
                    candidate =
                        configurations.Item(itemIndex);
                    if (!string.Equals(
                            EmptyToNull(candidate.Name),
                            requestedConfiguration,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string[] candidatePlatforms =
                        ReadSolutionConfigurationPlatforms(
                            candidate);
                    if (!string.IsNullOrWhiteSpace(
                            requestedPlatform)
                        && (candidatePlatforms.Length != 1
                            || !string.Equals(
                                candidatePlatforms[0],
                                requestedPlatform,
                                StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (selected is not null)
                    {
                        throw new GatewayOperationException(
                            ErrorCodes
                                .BuildConfigurationAmbiguous,
                            "Multiple XAE solution "
                            + $"configurations match "
                            + $"'{requestedConfiguration}'.",
                            stage: stage);
                    }

                    selected = candidate;
                    candidate = null;
                }
                finally
                {
                    ComObject.Release(candidate);
                }
            }

            if (selected is null)
            {
                string selection = string.IsNullOrWhiteSpace(
                        requestedPlatform)
                    ? $"configuration '{requestedConfiguration}'"
                    : "configuration/platform "
                        + $"'{requestedConfiguration}/"
                        + $"{requestedPlatform}'";
                throw new GatewayOperationException(
                    ErrorCodes.BuildConfigurationNotFound,
                    $"XAE solution {selection} was not found.",
                    stage: stage);
            }

            selected.Activate();
        }
        finally
        {
            ComObject.Release(selected);
            ComObject.Release(configurations);
        }
    }

    private static void ValidateBuildConfiguration(
        string? activeConfiguration,
        IReadOnlyList<string> activePlatforms,
        string? requestedConfiguration,
        string? requestedPlatform)
    {
        const string stage = "xae.build.configuration";
        if (!string.IsNullOrWhiteSpace(requestedConfiguration)
            && !string.Equals(
                activeConfiguration,
                requestedConfiguration,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayOperationException(
                ErrorCodes.BuildConfigurationNotFound,
                "XAE did not activate solution configuration "
                + $"'{requestedConfiguration}'.",
                stage: stage);
        }

        if (string.IsNullOrWhiteSpace(requestedPlatform))
        {
            return;
        }

        if (activePlatforms.Count != 1)
        {
            string available = activePlatforms.Count == 0
                ? "none"
                : string.Join(", ", activePlatforms);
            throw new GatewayOperationException(
                ErrorCodes.BuildConfigurationAmbiguous,
                "The active XAE solution configuration does not "
                + "have one unambiguous project platform; "
                + $"observed: {available}.",
                stage: stage);
        }

        if (!string.Equals(
                activePlatforms[0],
                requestedPlatform,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayOperationException(
                ErrorCodes.BuildConfigurationNotFound,
                $"XAE platform '{requestedPlatform}' is not active; "
                + $"active platform is '{activePlatforms[0]}'.",
                stage: stage);
        }
    }

    private XaeBuildExecutionResult CompleteBuildOnSta(
        XaeBuildEventEvidence evidence,
        ExternalChangeSynchronizationResult synchronization)
    {
        XaeBuildEventLease lease = _activeBuild
            ?? throw new GatewayOperationException(
                ErrorCodes.BuildResultInconsistent,
                "The XAE build event lease is no longer active.",
                retryable: true,
                stage: "xae.build.verify");
        try
        {
            return lease.Complete(
                evidence,
                synchronization);
        }
        finally
        {
            _activeBuild = null;
            lease.Dispose();
        }
    }

    private async Task TryAbortActiveBuildAsync()
    {
        try
        {
            await _dispatcher.InvokeAsync(
                () =>
                {
                    AbortActiveBuildOnSta();
                    return true;
                },
                TimeSpan.FromSeconds(5),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "XAE build cleanup did not complete: {0}",
                exception);
        }
    }

    private void AbortActiveBuildOnSta()
    {
        XaeBuildEventLease? lease = _activeBuild;
        _activeBuild = null;
        if (lease is null)
        {
            return;
        }

        try
        {
            lease.Cancel();
        }
        finally
        {
            lease.Dispose();
        }
    }

    private void ConfirmFingerprintBaseline(
        string solutionPath,
        CancellationToken cancellationToken)
    {
        ProjectFileFingerprintSnapshot baseline =
            CaptureProjectGraph(
                solutionPath,
                _twinCatProjectPath
                    ?? throw new GatewayOperationException(
                        ErrorCodes.SysManagerNotAvailable,
                        "The selected TwinCAT project path is unavailable.",
                        stage: "xae.workspace.fingerprint"),
                cancellationToken);
        ConfirmFingerprintBaseline(solutionPath, baseline);
    }

    private static ProjectFileFingerprintSnapshot
        CaptureProjectGraph(
            string solutionPath,
            string twinCatProjectPath,
            CancellationToken cancellationToken)
    {
        try
        {
            return ProjectFileFingerprintScanner
                .CaptureProjectGraph(
                    solutionPath,
                    twinCatProjectPath,
                    cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new GatewayOperationException(
                ErrorCodes.ProjectGraphInvalid,
                "The selected TwinCAT project graph could not be "
                + "validated: "
                + exception.Message,
                stage: "xae.workspace.graph",
                innerException: exception);
        }
    }

    private void ConfirmFingerprintBaseline(
        string solutionPath,
        ProjectFileFingerprintSnapshot baseline)
    {
        lock (_fingerprintSync)
        {
            _fingerprintSolution = solutionPath;
            _fingerprintBaseline = baseline;
            _fingerprintProcessId =
                _snapshot.SelectedInstance?.ProcessId;
            _fingerprintMoniker =
                _snapshot.SelectedInstance?.Moniker;
            _fingerprintLaunchedByGateway =
                _snapshot.LaunchedByGateway;
            _fingerprintTwinCatProjectPath =
                _twinCatProjectPath;
            _projectGraphPaths =
                baseline.Files.Select(file => file.Path).ToArray();
            _snapshot.SynchronizationState =
                SynchronizationState.Confirmed;
            _snapshot.UnsynchronizedFiles =
                Array.Empty<ProjectFileChange>();
            _fingerprintState =
                SynchronizationState.Confirmed;
        }
    }

    private void RequireSynchronization(
        string solutionPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_fingerprintSync)
        {
            _fingerprintSolution = solutionPath;
            _fingerprintBaseline = null;
            _fingerprintProcessId = null;
            _fingerprintMoniker = null;
            _fingerprintLaunchedByGateway = false;
            _fingerprintTwinCatProjectPath = null;
            _snapshot.SynchronizationState =
                SynchronizationState.SyncRequired;
            _snapshot.UnsynchronizedFiles =
                Array.Empty<ProjectFileChange>();
            _fingerprintState =
                SynchronizationState.SyncRequired;
        }
    }

    private FingerprintState GetFingerprintState()
    {
        lock (_fingerprintSync)
        {
            if (_fingerprintSolution is null)
            {
                throw new GatewayOperationException(
                    ErrorCodes.GatewayNotReady,
                    "Agent workspace fingerprints are not initialized.",
                    retryable: true,
                    stage: "xae.workspace.fingerprint");
            }

            return new FingerprintState(
                _fingerprintSolution,
                _fingerprintBaseline,
                _twinCatProjectPath
                    ?? throw new GatewayOperationException(
                        ErrorCodes.SysManagerNotAvailable,
                        "The selected TwinCAT project path is unavailable.",
                        stage: "xae.workspace.fingerprint"));
        }
    }

    private void ClearFingerprintBaseline()
    {
        lock (_fingerprintSync)
        {
            _fingerprintSolution = null;
            _fingerprintBaseline = null;
            _fingerprintProcessId = null;
            _fingerprintMoniker = null;
            _fingerprintLaunchedByGateway = false;
            _fingerprintTwinCatProjectPath = null;
            _projectGraphPaths = Array.Empty<string>();
            _snapshot.UnsynchronizedFiles =
                Array.Empty<ProjectFileChange>();
            _fingerprintState =
                SynchronizationState.Uninitialized;
        }
    }

    private void SetUnsynchronizedFiles(
        IEnumerable<ProjectFileChange> changes)
    {
        ProjectFileChange[] snapshot =
            changes.Select(CloneProjectFileChange).ToArray();
        lock (_fingerprintSync)
        {
            _snapshot.UnsynchronizedFiles = snapshot;
            if (snapshot.Length != 0)
            {
                _snapshot.SynchronizationState =
                    SynchronizationState.SyncRequired;
                _fingerprintState =
                    SynchronizationState.SyncRequired;
            }
        }
    }

    private bool CanRetainFingerprintBaseline(
        int? processId,
        string moniker,
        string solutionPath,
        string twinCatProjectPath)
    {
        lock (_fingerprintSync)
        {
            return processId.HasValue
                && _fingerprintBaseline is not null
                && _fingerprintState
                    == SynchronizationState.Confirmed
                && _fingerprintProcessId == processId
                && string.Equals(
                    _fingerprintMoniker,
                    moniker,
                    StringComparison.Ordinal)
                && string.Equals(
                    _fingerprintSolution,
                    solutionPath,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    _fingerprintTwinCatProjectPath,
                    twinCatProjectPath,
                    StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static string NormalizeChangedPath(
        string solutionPath,
        IReadOnlyCollection<string> projectGraphPaths,
        string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Changed project file path is required.",
                stage: "xae.workspace.validate");
        }

        string root = Path.GetDirectoryName(solutionPath)
            ?? throw new GatewayOperationException(
                ErrorCodes.SolutionMismatch,
                "The tracked solution path has no parent directory.",
                stage: "xae.workspace.validate");
        string fullPath = Path.GetFullPath(
            Path.IsPathRooted(path)
                ? path
                : Path.Combine(root, path));
        if (!projectGraphPaths.Contains(fullPath))
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Changed project file must belong to the selected "
                + "TwinCAT project graph.",
                stage: "xae.workspace.validate");
        }

        if (!File.Exists(fullPath))
        {
            throw new GatewayOperationException(
                ErrorCodes.ExternalEditUnsupported,
                $"Changed project file '{fullPath}' does not exist.",
                stage: "xae.workspace.validate");
        }

        return fullPath;
    }

    internal static void ValidateChangedPlcObjects(
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        foreach (string path in paths.OrderBy(
            value => value,
            StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using FileStream content = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            TwinCatPlcObjectValidationResult validation =
                TwinCatPlcObjectValidator.Validate(content);
            if (!validation.IsValid)
            {
                throw new GatewayOperationException(
                    ErrorCodes.PlcObjectInvalid,
                    $"PLC object '{path}' failed pinned XSD "
                    + $"validation: {validation.Error}",
                    stage: "xae.workspace.validate");
            }
        }
    }

    internal static SynchronizationScope SelectSynchronizationScope(
        IReadOnlyList<ProjectFileChange> changes,
        ExternalChangePolicy policy,
        bool force,
        bool baselineMissing)
    {
        ProjectFileChange[] sourceChanges = changes
            .Where(change =>
                change.Role
                    != ProjectGraphFileRole.GeneratedArtifact)
            .ToArray();
        if (baselineMissing)
        {
            return SynchronizationScope.TwinCatProject;
        }

        if (sourceChanges.Length == 0)
        {
            return force
                ? SynchronizationScope.TwinCatProject
                : SynchronizationScope.None;
        }

        if (!force && policy == ExternalChangePolicy.Error)
        {
            throw CreateExternalChangeError(sourceChanges, policy);
        }

        bool sourcesOnly = sourceChanges.All(change =>
            change.Role == ProjectGraphFileRole.PlcSource
            && change.Kind == ProjectFileChangeKind.Modified);
        if (!force
            && policy == ExternalChangePolicy.ReloadModified
            && !sourcesOnly)
        {
            throw CreateExternalChangeError(sourceChanges, policy);
        }

        if (sourcesOnly)
        {
            return SynchronizationScope.ModifiedSources;
        }

        if (sourceChanges.Any(change =>
            change.Role == ProjectGraphFileRole.TwinCatProject))
        {
            return SynchronizationScope.TwinCatProject;
        }

        return SynchronizationScope.PlcProject;
    }

    private static GatewayOperationException CreateExternalChangeError(
        IReadOnlyList<ProjectFileChange> changes,
        ExternalChangePolicy policy)
    {
        ProjectFileChange first = changes[0];
        return new GatewayOperationException(
            ErrorCodes.ExternalChangeDetected,
            $"Disk state differs from the confirmed XAE baseline "
            + $"under policy '{policy}': {first.Kind} "
            + $"{first.Role} '{first.Path}'.",
            stage: "xae.workspace.policy");
    }

    private IReadOnlyList<XaeProjectFileChangeResult>
        ReloadTwinCatProjectOnSta(
        string solutionPath,
        string twinCatProjectPath)
    {
        DTE2 dte = _dte
            ?? throw new GatewayOperationException(
                ErrorCodes.XaeNotFound,
                "No XAE session is currently attached.",
                retryable: true,
                stage: "xae.workspace.reloadProject");
        XaeProjectFileChangeLease? fileChanges = null;
        IVsSolution? vsSolution = null;
        IVsHierarchy? hierarchy = null;
        Project? project = null;
        Projects? projects = null;
        Solution? solution = null;
        try
        {
            fileChanges = XaeProjectFileChangeLease.Acquire(
                dte,
                solutionPath,
                _workspaceFileChangeGuard);
            solution = dte.Solution;
            projects = solution.Projects;
            for (int index = 1; index <= projects.Count; index++)
            {
                Project candidate = projects.Item(index);
                if (string.Equals(
                    NormalizeOptionalPath(candidate.FullName),
                    twinCatProjectPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    project = candidate;
                    break;
                }

                ComObject.Release(candidate);
            }

            if (project is null)
            {
                throw new GatewayOperationException(
                    ErrorCodes.SysManagerNotAvailable,
                    "The selected TwinCAT project is no longer present "
                    + "in the attached solution.",
                    retryable: true,
                    stage: "xae.workspace.reloadProject");
            }

            vsSolution = QueryService<IVsSolution>(
                dte,
                typeof(SVsSolution).GUID);
            Marshal.ThrowExceptionForHR(
                vsSolution.GetProjectOfUniqueName(
                    project.UniqueName,
                    out hierarchy));
            Marshal.ThrowExceptionForHR(
                vsSolution.GetGuidOfProject(
                    hierarchy,
                    out Guid projectId));
            ComObject.Release(_sysManager);
            _sysManager = null;
            Marshal.ThrowExceptionForHR(
                ((IVsSolution4)vsSolution).ReloadProject(
                    ref projectId));
            ITcSysManager reloaded = AcquireSysManager(
                dte,
                solutionPath,
                out string reloadedPath);
            if (!string.Equals(
                reloadedPath,
                twinCatProjectPath,
                StringComparison.OrdinalIgnoreCase))
            {
                ComObject.Release(reloaded);
                throw new GatewayOperationException(
                    ErrorCodes.SolutionMismatch,
                    "XAE reloaded a different TwinCAT project.",
                    retryable: true,
                    stage: "xae.workspace.reloadProject");
            }

            _sysManager = reloaded;
            IReadOnlyList<XaeProjectFileChangeResult> generated =
                fileChanges.ClassifyChangesAndRelease(
                    BuildAction.Build);
            fileChanges = null;
            XaeProjectFileChangeResult? unsupported =
                generated.FirstOrDefault(change =>
                    change.Classification
                        != ProjectChangeClassification
                            .ExpectedReorderOnly
                    && change.Classification
                        != ProjectChangeClassification
                            .WhitespaceOnly);
            if (unsupported is not null)
            {
                throw new GatewayOperationException(
                    ErrorCodes.ExternalChangeDetected,
                    "TwinCAT project metadata changed during the tracked "
                    + "reload and was not proven generated noise: "
                    + $"'{unsupported.Path}'.",
                    retryable: true,
                    stage: "xae.workspace.reloadProject");
            }

            return generated;
        }
        finally
        {
            fileChanges?.Dispose();
            ComObject.Release(project);
            ComObject.Release(hierarchy);
            ComObject.Release(vsSolution);
            ComObject.Release(projects);
            ComObject.Release(solution);
        }
    }

    internal static T QueryService<T>(
        DTE2 dte,
        Guid service)
        where T : class
    {
        IntPtr unknownPointer = IntPtr.Zero;
        IntPtr providerPointer = IntPtr.Zero;
        OleServiceProvider? serviceProvider = null;
        IntPtr servicePointer = IntPtr.Zero;
        try
        {
            unknownPointer = Marshal.GetIUnknownForObject(dte);
            Guid providerIid = typeof(OleServiceProvider).GUID;
            Marshal.ThrowExceptionForHR(
                Marshal.QueryInterface(
                    unknownPointer,
                    ref providerIid,
                    out providerPointer));
            serviceProvider = (OleServiceProvider)
                Marshal.GetTypedObjectForIUnknown(
                    providerPointer,
                    typeof(OleServiceProvider));
            Guid iid = typeof(T).GUID;
            Marshal.ThrowExceptionForHR(
                serviceProvider.QueryService(
                    ref service,
                    ref iid,
                    out servicePointer));
            return (T)Marshal.GetTypedObjectForIUnknown(
                servicePointer,
                typeof(T));
        }
        finally
        {
            ComObject.Release(serviceProvider);
            if (servicePointer != IntPtr.Zero)
            {
                Marshal.Release(servicePointer);
            }

            if (providerPointer != IntPtr.Zero)
            {
                Marshal.Release(providerPointer);
            }

            if (unknownPointer != IntPtr.Zero)
            {
                Marshal.Release(unknownPointer);
            }
        }
    }

    private void AcquireWorkspaceFileChangeGuardOnSta(
        ProjectFileFingerprintSnapshot graph)
    {
        if (_workspaceFileChangeGuard is not null)
        {
            throw new GatewayOperationException(
                ErrorCodes.XaeFileChangeGuardFailed,
                "An XAE workspace file-change guard is already active.",
                stage: "xae.workspace.guard");
        }

        DTE2 dte = _dte
            ?? throw new GatewayOperationException(
                ErrorCodes.XaeNotFound,
                "No XAE session is currently attached.",
                retryable: true,
                stage: "xae.workspace.guard");
        _workspaceFileChangeGuard =
            XaeWorkspaceFileChangeGuard.Acquire(
                dte,
                graph.Files);
        _projectGraphPaths =
            graph.Files.Select(file => file.Path).ToArray();
    }

    private void UpdateWorkspaceFileChangeGuardOnSta(
        ProjectFileFingerprintSnapshot graph)
    {
        XaeWorkspaceFileChangeGuard guard =
            _workspaceFileChangeGuard
            ?? throw new GatewayOperationException(
                ErrorCodes.XaeFileChangeGuardFailed,
                "The attached XAE session has no active file-change guard.",
                retryable: true,
                stage: "xae.workspace.guard");
        guard.UpdatePaths(graph.Files);
        _projectGraphPaths =
            graph.Files.Select(file => file.Path).ToArray();
        _snapshot.AgentWorkspaceOwned = guard.IsActive;
    }

    private void EnsurePendingLaunchedProcess(int processId)
    {
        if (_dte is null
            || _snapshot.SelectedInstance?.ProcessId != processId
            || !_snapshot.LaunchedByGateway)
        {
            throw new GatewayOperationException(
                ErrorCodes.XaeNotFound,
                $"Gateway-launched XAE process {processId} is no longer attached.",
                retryable: true,
                stage: "xae.launch");
        }
    }

    private bool CloseGatewayLaunchedOnSta()
    {
        if (_dte is null || !_snapshot.LaunchedByGateway)
        {
            return false;
        }

        Solution? solution = null;
        try
        {
            DTE2 automation = _dte;
            solution = automation.Solution;
            string? solutionPath = solution.FullName;
            if (!string.IsNullOrWhiteSpace(solutionPath))
            {
                solution.Close(false);
            }

            Exception? silentModeException = null;
            try
            {
                ReleaseSilentModeLeaseOnSta();
            }
            catch (Exception exception)
            {
                silentModeException = exception;
            }

            automation.Quit();
            if (silentModeException is not null)
            {
                ExceptionDispatchInfo.Capture(
                    silentModeException).Throw();
            }

            return true;
        }
        finally
        {
            ComObject.Release(solution);
            ReleaseSessionOnSta();
        }
    }

    private XaeCloseCommandOutcome CloseAttachedOnSta(
        string normalizedSolution,
        int processId,
        XaeSaveMode saveMode)
    {
        if (_dte is null
            || _snapshot.SelectedInstance?.ProcessId != processId)
        {
            throw new GatewayOperationException(
                ErrorCodes.XaeNotFound,
                $"XAE process {processId} is no longer attached.",
                retryable: true,
                stage: "xae.close.verify");
        }

        string? selectedSolution =
            NormalizeOptionalPath(
                _snapshot.SelectedInstance.Solution);
        if (!string.Equals(
            selectedSolution,
            normalizedSolution,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayOperationException(
                ErrorCodes.SolutionMismatch,
                "The selected XAE solution changed before close.",
                retryable: true,
                stage: "xae.close.verify");
        }

        DTE2 automation = _dte;
        Solution? solution = null;
        try
        {
            solution = automation.Solution;
            string? actualSolution =
                NormalizeOptionalPath(solution.FullName);
            if (!string.Equals(
                actualSolution,
                normalizedSolution,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new GatewayOperationException(
                    ErrorCodes.SolutionMismatch,
                    "The active XAE solution changed before close.",
                    retryable: true,
                    stage: "xae.close.verify");
            }
        }
        catch
        {
            ComObject.Release(solution);
            throw;
        }

        Exception? commandError = null;
        try
        {
            ReleaseSilentModeLeaseOnSta();
            switch (saveMode)
            {
                case XaeSaveMode.Save:
                    solution.Close(true);
                    automation.Quit();
                    break;
                case XaeSaveMode.Discard:
                    solution.Close(false);
                    automation.Quit();
                    break;
                case XaeSaveMode.Prompt:
                    automation.Quit();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(saveMode));
            }
        }
        catch (Exception exception)
        {
            commandError = exception;
        }
        finally
        {
            ComObject.Release(solution);
            try
            {
                ReleaseSessionOnSta();
            }
            catch (Exception exception)
            {
                commandError ??= exception;
            }
        }

        return new XaeCloseCommandOutcome
        {
            Error = commandError is null
                ? null
                : $"{commandError.GetType().FullName}: "
                    + $"{commandError.Message} "
                    + $"(HRESULT 0x{commandError.HResult:X8})",
        };
    }

    private bool CloseCleanAttachedOnSta(
        string normalizedSolution,
        int processId)
    {
        if (_dte is null
            || _snapshot.SelectedInstance?.ProcessId != processId)
        {
            throw new GatewayOperationException(
                ErrorCodes.XaeNotFound,
                $"XAE process {processId} is no longer attached.",
                retryable: true,
                stage: "xae.close.shutdown.verify");
        }

        string? selectedSolution = NormalizeOptionalPath(
            _snapshot.SelectedInstance.Solution);
        if (!string.Equals(
            selectedSolution,
            normalizedSolution,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayOperationException(
                ErrorCodes.SolutionMismatch,
                "The selected XAE solution changed before shutdown cleanup.",
                retryable: true,
                stage: "xae.close.shutdown.verify");
        }

        if (AgentWorkspaceOwnership.HasAnyDirtyDocument(_dte))
        {
            return false;
        }

        DTE2 automation = _dte;
        Solution? solution = null;
        try
        {
            solution = automation.Solution;
            string? actualSolution = NormalizeOptionalPath(
                solution.FullName);
            if (!string.Equals(
                actualSolution,
                normalizedSolution,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new GatewayOperationException(
                    ErrorCodes.SolutionMismatch,
                    "The active XAE solution changed before shutdown cleanup.",
                    retryable: true,
                    stage: "xae.close.shutdown.verify");
            }

            ReleaseSilentModeLeaseOnSta();
            solution.Close(false);
            automation.Quit();
            return true;
        }
        finally
        {
            ComObject.Release(solution);
            ReleaseSessionOnSta();
        }
    }

    private static DiagnosticsProcess? TryGetProcess(int processId)
    {
        try
        {
            return DiagnosticsProcess.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static async Task<bool> WaitForProcessExitAsync(
        DiagnosticsProcess process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (process.HasExited)
        {
            return true;
        }

        TaskCompletionSource<bool> exited =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler handler = (_, _) => exited.TrySetResult(true);
        process.EnableRaisingEvents = true;
        process.Exited += handler;
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            Task delay = Task.Delay(timeout, cancellationToken);
            Task completed =
                await Task.WhenAny(exited.Task, delay)
                    .ConfigureAwait(false);
            if (completed == exited.Task)
            {
                return true;
            }

            await delay.ConfigureAwait(false);
            return process.HasExited;
        }
        finally
        {
            process.Exited -= handler;
        }
    }

    private void ReleaseSessionOnSta()
    {
        Exception? cleanupException = null;
        try
        {
            AbortActiveBuildOnSta();
        }
        catch (Exception exception)
        {
            cleanupException = exception;
        }

        try
        {
            ReleaseSilentModeLeaseOnSta();
        }
        catch (Exception exception)
        {
            cleanupException = cleanupException is null
                ? exception
                : new AggregateException(
                    cleanupException,
                    exception);
        }

        try
        {
            _workspaceFileChangeGuard?.Dispose();
        }
        catch (Exception exception)
        {
            cleanupException = cleanupException is null
                ? exception
                : new AggregateException(
                    cleanupException,
                    exception);
        }
        finally
        {
            _workspaceFileChangeGuard = null;
            ComObject.Release(_sysManager);
            ComObject.Release(_dte);
            _sysManager = null;
            _dte = null;
            _twinCatProjectPath = null;
            _snapshot = new XaeSessionSnapshot();
        }

        if (cleanupException is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupException).Throw();
        }
    }

    private sealed class XaeCloseCommandOutcome
    {
        public string? Error { get; set; }
    }

    private TwinCatSilentModeLease? CreateUserSilentModeLease()
    {
        return _snapshot.LaunchedByGateway
            ? null
            : TwinCatSilentModeLease.Enable(
                _dte
                    ?? throw new GatewayOperationException(
                        ErrorCodes.XaeNotFound,
                        "No XAE session is currently attached.",
                        retryable: true,
                        stage: "xae.silentMode"),
                restoreOnDispose: true);
    }

    private bool ReadSilentModeOnSta()
    {
        return TwinCatSilentModeLease.Read(
            _dte
                ?? throw new GatewayOperationException(
                    ErrorCodes.XaeNotFound,
                    "No XAE session is currently attached.",
                    retryable: true,
                    stage: "xae.silentMode"));
    }

    private void ReleaseSilentModeLeaseOnSta()
    {
        TwinCatSilentModeLease? lease = _silentModeLease;
        _silentModeLease = null;
        lease?.Dispose();
    }

    private void EnsureDialogSupervisor(int processId)
    {
        XaeDialogSupervisor? previous;
        XaeDialogSupervisor next;
        lock (_dialogSync)
        {
            if (_dialogSupervisor?.ProcessId == processId)
            {
                return;
            }

            next = new XaeDialogSupervisor(processId);
            next.DialogObserved += ForwardDialogObservation;
            previous = _dialogSupervisor;
            _dialogSupervisor = next;
        }

        if (previous is not null)
        {
            previous.DialogObserved -= ForwardDialogObservation;
            previous.Dispose();
        }
    }

    private void StopDialogSupervisor()
    {
        XaeDialogSupervisor? supervisor;
        lock (_dialogSync)
        {
            supervisor = _dialogSupervisor;
            _dialogSupervisor = null;
        }

        if (supervisor is not null)
        {
            supervisor.DialogObserved -= ForwardDialogObservation;
            supervisor.Dispose();
        }
    }

    private void ForwardDialogObservation(
        object sender,
        XaeDialogObservationEventArgs eventArgs)
    {
        _ = sender;
        DialogObserved?.Invoke(this, eventArgs);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(XaeSession));
        }
    }

    private static string NormalizeSolutionPath(string solutionPath)
    {
        return Path.GetFullPath(
            solutionPath
                ?? throw new ArgumentNullException(nameof(solutionPath)));
    }

    private static string? NormalizeOptionalPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(path);
    }

    private static DateTimeOffset CreateDeadline(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return DateTimeOffset.UtcNow.Add(timeout);
    }

    private static TimeSpan GetRemaining(
        DateTimeOffset deadlineUtc,
        string stage)
    {
        TimeSpan remaining = deadlineUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            throw new GatewayOperationException(
                ErrorCodes.OperationTimeout,
                "The XAE session operation exceeded its overall deadline.",
                retryable: true,
                stage: stage);
        }

        return remaining;
    }

    private static TimeSpan GetPollCallTimeout(
        DateTimeOffset deadlineUtc)
    {
        TimeSpan remaining = GetRemaining(
            deadlineUtc,
            "xae.poll");
        return remaining < PollCallLimit
            ? remaining
            : PollCallLimit;
    }

    private static Task DelayForPollAsync(
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        TimeSpan remaining = deadlineUtc - DateTimeOffset.UtcNow;
        TimeSpan delay = remaining < PollInterval
            ? remaining
            : PollInterval;
        return delay <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(delay, cancellationToken);
    }

    private static async Task<XaeBuildEventEvidence>
        WaitForBuildEventAsync(
            Task<XaeBuildEventEvidence> completion,
            DateTimeOffset deadlineUtc,
            CancellationToken cancellationToken)
    {
        TimeSpan remaining = GetRemaining(
            deadlineUtc,
            "xae.build.wait");
        Task delay = Task.Delay(
            remaining,
            cancellationToken);
        Task completed = await Task.WhenAny(
            completion,
            delay).ConfigureAwait(false);
        if (completed == completion)
        {
            return await completion.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new GatewayOperationException(
            ErrorCodes.OperationTimeout,
            "XAE did not raise OnBuildDone before the deadline.",
            retryable: true,
            stage: "xae.build.wait");
    }

    private static XaeSessionSnapshot CloneSnapshot(
        XaeSessionSnapshot source)
    {
        return new XaeSessionSnapshot
        {
            Connected = source.Connected,
            SelectedInstance = CloneInfo(source.SelectedInstance),
            SysManagerAvailable = source.SysManagerAvailable,
            LaunchedByGateway = source.LaunchedByGateway,
            Ownership = source.Ownership,
            AgentWorkspaceOwned = source.AgentWorkspaceOwned,
            ClosedDocumentCount = source.ClosedDocumentCount,
            DiscardedDocumentCount = source.DiscardedDocumentCount,
            SynchronizationState =
                source.SynchronizationState,
            DirtyDocumentCount =
                source.DirtyDocumentCount,
            UnsynchronizedFiles =
                source.UnsynchronizedFiles
                    .Select(CloneProjectFileChange)
                    .ToArray(),
            ActiveConfiguration = source.ActiveConfiguration,
            ActivePlatform = source.ActivePlatform,
            TargetAmsNetId = source.TargetAmsNetId,
            TwinCatSystem = CloneTwinCatSystem(
                source.TwinCatSystem),
            TwinCatProjectPath = source.TwinCatProjectPath,
            LastErrorMessages =
                source.LastErrorMessages.ToArray(),
            ErrorListMessages =
                source.ErrorListMessages
                    .Select(CloneBuildDiagnostic)
                    .ToArray(),
            DiagnosticIssues =
                source.DiagnosticIssues.ToArray(),
            DiscoveredInstances = source.DiscoveredInstances
                .Select(instance => CloneInfo(instance)!)
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

    private static ProjectFileChange CloneProjectFileChange(
        ProjectFileChange source)
    {
        return new ProjectFileChange(
            source.Path,
            source.Kind,
            source.Role);
    }

    private static DteInstanceInfo? CloneInfo(DteInstanceInfo? source)
    {
        return source is null
            ? null
            : new DteInstanceInfo
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

    private sealed class FingerprintState
    {
        public FingerprintState(
            string solutionPath,
            ProjectFileFingerprintSnapshot? baseline,
            string twinCatProjectPath)
        {
            SolutionPath = solutionPath;
            Baseline = baseline;
            TwinCatProjectPath = twinCatProjectPath;
        }

        public string SolutionPath { get; }

        public ProjectFileFingerprintSnapshot? Baseline { get; }

        public string TwinCatProjectPath { get; }
    }

    private sealed class StartedXaeProcess
    {
        public StartedXaeProcess(
            string progId,
            int processId)
        {
            ProgId = progId;
            ProcessId = processId;
        }

        public string ProgId { get; }

        public int ProcessId { get; }
    }
}
