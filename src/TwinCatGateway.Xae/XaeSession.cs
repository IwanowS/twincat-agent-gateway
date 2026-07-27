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
using TCatSysManagerLib;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;

namespace TwinCatGateway.Xae;

public sealed class XaeSession : IDisposable
{
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
    private readonly object _fingerprintSync = new();
    private readonly bool _ownsDispatcher;
    private DTE2? _dte;
    private XaeBuildEventLease? _activeBuild;
    private ProjectFileFingerprintSnapshot? _fingerprintBaseline;
    private string? _fingerprintSolution;
    private ITcSysManager? _sysManager;
    private TwinCatSilentModeLease? _silentModeLease;
    private XaeSessionSnapshot _snapshot = new();
    private int _disposed;

    public XaeSession(ComStaDispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? new ComStaDispatcher();
        _ownsDispatcher = dispatcher is null;
    }

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
        ThrowIfDisposed();
        string normalizedSolution = NormalizeSolutionPath(solutionPath);
        DateTimeOffset deadlineUtc = CreateDeadline(timeout);
        XaeSessionSnapshot snapshot =
            await _dispatcher.InvokeAsync(
                () => AttachOnSta(normalizedSolution),
                GetRemaining(
                    deadlineUtc,
                    "xae.attach"),
                cancellationToken).ConfigureAwait(false);
        InitializeFingerprintBaseline(
            normalizedSolution,
            cancellationToken);
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
        ThrowIfDisposed();
        DateTimeOffset deadlineUtc = CreateDeadline(timeout);
        try
        {
            return await AttachAsync(
                solutionPath,
                GetRemaining(deadlineUtc, "xae.attach"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayOperationException exception) when (
            exception.Code == ErrorCodes.XaeNotFound
            && allowLaunch)
        {
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

        await WaitForLaunchedDteAsync(
            started,
            deadlineUtc,
            cancellationToken).ConfigureAwait(false);
        await _dispatcher.InvokeAsync(
            () =>
            {
                OpenSolutionOnSta(
                    normalizedSolution,
                    started.ProcessId);
                return true;
            },
            GetRemaining(deadlineUtc, "xae.openSolution"),
            cancellationToken).ConfigureAwait(false);
        XaeSessionSnapshot snapshot =
            await WaitForLaunchedSolutionAsync(
            normalizedSolution,
            started.ProcessId,
            deadlineUtc,
            cancellationToken).ConfigureAwait(false);
        InitializeFingerprintBaseline(
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

    public Task<XaeSessionSnapshot> VerifyAttachedAsync(
        string solutionPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string normalizedSolution = NormalizeSolutionPath(solutionPath);
        return _dispatcher.InvokeAsync(
            () => VerifyAttachedOnSta(normalizedSolution),
            timeout,
            cancellationToken);
    }

    public async Task ActivateConfigurationAsync(
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
                ActivateConfigurationOnSta(
                    normalizedSolution,
                    expectedAmsNetId);
                return true;
            },
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task StartRestartTwinCatAsync(
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
                StartRestartTwinCatOnSta(
                    normalizedSolution,
                    expectedAmsNetId);
                return true;
            },
            timeout,
            cancellationToken).ConfigureAwait(false);
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
        try
        {
            await _dispatcher.InvokeAsync(
                () =>
                {
                    ReleaseSessionOnSta();
                    return true;
                },
                timeout,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ClearFingerprintBaseline();
        }
    }

    public Task<AgentWorkspaceOwnershipResult> AcquireAgentWorkspaceAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _dispatcher.InvokeAsync(
            AcquireAgentWorkspaceOnSta,
            timeout,
            cancellationToken);
    }

    public async Task<ExternalChangeSynchronizationResult>
        SynchronizeExternalChangesAsync(
            IEnumerable<string>? changedPaths,
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        DateTimeOffset deadlineUtc = CreateDeadline(timeout);
        FingerprintState state = GetFingerprintState();
        ProjectFileFingerprintSnapshot current =
            ProjectFileFingerprintScanner.Capture(
                state.SolutionPath,
                cancellationToken);
        IReadOnlyList<ProjectFileChange> detected =
            ProjectFileFingerprintScanner.Compare(
                state.Baseline,
                current);
        ProjectFileChange? structuralChange =
            detected.FirstOrDefault(change =>
                change.Kind != ProjectFileChangeKind.Modified);
        if (structuralChange is not null)
        {
            throw new GatewayOperationException(
                ErrorCodes.ExternalEditUnsupported,
                $"External source file {structuralChange.Kind.ToString().ToLowerInvariant()}: "
                + $"'{structuralChange.Path}'. Adding and deleting project "
                + "sources is not supported by the MVP synchronizer.",
                stage: "xae.workspace.fingerprint");
        }

        HashSet<string> paths = new(
            detected.Select(change => change.Path),
            StringComparer.OrdinalIgnoreCase);
        if (changedPaths is not null)
        {
            foreach (string path in changedPaths)
            {
                paths.Add(
                    NormalizeChangedPath(
                        state.SolutionPath,
                        path));
            }
        }

        ValidateChangedPlcObjects(
            paths,
            cancellationToken);
        GetRemaining(
            deadlineUtc,
            "xae.workspace.validate");
        XaeDocumentSynchronizationResult synchronized =
            await _dispatcher.InvokeAsync(
                () => SynchronizeExternalChangesOnSta(
                    state.SolutionPath,
                    paths),
                GetRemaining(
                    deadlineUtc,
                    "xae.workspace.synchronize"),
                cancellationToken).ConfigureAwait(false);
        ProjectFileFingerprintSnapshot verified =
            ProjectFileFingerprintScanner.Capture(
                state.SolutionPath,
                cancellationToken);
        IReadOnlyList<ProjectFileChange> concurrentChanges =
            ProjectFileFingerprintScanner.Compare(
                current,
                verified);
        if (concurrentChanges.Count != 0)
        {
            throw new GatewayOperationException(
                ErrorCodes.ExternalEditSyncFailed,
                "Project source files changed while XAE synchronization "
                + "was running. Retry the operation.",
                retryable: true,
                stage: "xae.workspace.verify");
        }

        GetRemaining(
            deadlineUtc,
            "xae.workspace.verify");
        ReplaceFingerprintBaseline(
            state.SolutionPath,
            verified);
        return new ExternalChangeSynchronizationResult(
            detected,
            synchronized.SynchronizedDocuments,
            synchronized.DiscardedDocuments);
    }

    public async Task<XaeBuildExecutionResult> ExecuteBuildAsync(
        BuildAction action,
        IEnumerable<string>? changedPaths,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        DateTimeOffset deadlineUtc = CreateDeadline(timeout);
        ExternalChangeSynchronizationResult synchronization =
            await SynchronizeExternalChangesAsync(
                changedPaths,
                GetRemaining(
                    deadlineUtc,
                    "xae.build.synchronize"),
                cancellationToken).ConfigureAwait(false);
        Task<XaeBuildEventEvidence> completion =
            await _dispatcher.InvokeAsync(
                () => StartBuildOnSta(action),
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
            return await _dispatcher.InvokeAsync(
                () => CompleteBuildOnSta(
                    evidence,
                    synchronization),
                GetRemaining(
                    deadlineUtc,
                    "xae.build.verify"),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TryAbortActiveBuildAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ComDiagnostics GetComDiagnostics()
    {
        ThrowIfDisposed();
        return _dispatcher.GetDiagnostics();
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
        try
        {
            sysManager = AcquireSysManager(
                _dte!,
                normalizedSolution);
        }
        catch (GatewayOperationException exception) when (
            exception.Code == ErrorCodes.SysManagerNotAvailable)
        {
            return null;
        }

        AgentWorkspaceOwnershipResult ownership;
        try
        {
            ownership = AgentWorkspaceOwnership.Acquire(
                _dte!,
                normalizedSolution);
        }
        catch
        {
            ComObject.Release(sysManager);
            throw;
        }

        ComObject.Release(_sysManager);
        _sysManager = sysManager;
        info.Selected = true;
        info.SelectionReason =
            "Gateway-launched XAE opened the exact normalized solution path.";
        _snapshot = new XaeSessionSnapshot
        {
            Connected = true,
            SelectedInstance = CloneInfo(info),
            SysManagerAvailable = true,
            LaunchedByGateway = true,
            AgentWorkspaceOwned = true,
            ClosedDocumentCount = ownership.ClosedDocuments.Count,
            DiscardedDocumentCount = ownership.DiscardedDocuments.Count,
            DiscoveredInstances = new[] { CloneInfo(info)! },
        };
        using (CreateUserSilentModeLease())
        {
            RefreshDiagnosticsOnSta();
        }
        return CloneSnapshot(_snapshot);
    }

    private XaeSessionSnapshot AttachOnSta(string normalizedSolution)
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
        AgentWorkspaceOwnershipResult? ownership = null;
        try
        {
            using (TwinCatSilentModeLease.Enable(
                selectedDte,
                restoreOnDispose: true))
            {
                selectedSysManager = AcquireSysManager(
                    selectedDte,
                    normalizedSolution);
                ownership = AgentWorkspaceOwnership.Acquire(
                    selectedDte,
                    normalizedSolution);
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
        instances[selectedIndex].Selected = true;
        instances[selectedIndex].SelectionReason =
            "Exact normalized Solution.FullName match.";
        _snapshot = new XaeSessionSnapshot
        {
            Connected = true,
            SelectedInstance = CloneInfo(instances[selectedIndex]),
            SysManagerAvailable = true,
            LaunchedByGateway = false,
            AgentWorkspaceOwned = true,
            ClosedDocumentCount =
                ownership?.ClosedDocuments.Count ?? 0,
            DiscardedDocumentCount =
                ownership?.DiscardedDocuments.Count ?? 0,
            DiscoveredInstances = instances,
        };
        using (CreateUserSilentModeLease())
        {
            RefreshDiagnosticsOnSta();
        }
        return CloneSnapshot(_snapshot);
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
        _snapshot = new XaeSessionSnapshot
        {
            Connected = true,
            SelectedInstance = CloneInfo(info),
            SysManagerAvailable = true,
            LaunchedByGateway = _snapshot.LaunchedByGateway,
            AgentWorkspaceOwned = _snapshot.AgentWorkspaceOwned,
            ClosedDocumentCount = _snapshot.ClosedDocumentCount,
            DiscardedDocumentCount = _snapshot.DiscardedDocumentCount,
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
        using (CreateUserSilentModeLease())
        {
            VerifyActivationBoundaryOnSta(
                normalizedSolution,
                expectedAmsNetId,
                stage);
            try
            {
                _sysManager!.ActivateConfiguration();
            }
            catch (Exception exception)
            {
                throw new GatewayOperationException(
                    ErrorCodes.ActivateConfigurationFailed,
                    "TwinCAT configuration activation failed.",
                    retryable: false,
                    stage: stage,
                    innerException: exception);
            }
        }
    }

    private void StartRestartTwinCatOnSta(
        string normalizedSolution,
        string expectedAmsNetId)
    {
        const string stage = "activation.restart";
        using (CreateUserSilentModeLease())
        {
            VerifyActivationBoundaryOnSta(
                normalizedSolution,
                expectedAmsNetId,
                stage);
            try
            {
                _sysManager!.StartRestartTwinCAT();
            }
            catch (Exception exception)
            {
                throw new GatewayOperationException(
                    ErrorCodes.TwinCatRestartFailed,
                    "TwinCAT restart request failed.",
                    retryable: true,
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
        IReadOnlyList<string> lastErrorMessages =
            Array.Empty<string>();

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

        _snapshot.ActiveConfiguration =
            activeConfiguration;
        _snapshot.ActivePlatform = activePlatform;
        _snapshot.TargetAmsNetId = targetAmsNetId;
        _snapshot.LastErrorMessages =
            lastErrorMessages;
        _snapshot.DiagnosticIssues = issues;
    }

    private static void ReadActiveSolutionConfiguration(
        DTE2 dte,
        out string? activeConfiguration,
        out string? activePlatform)
    {
        Solution? solution = null;
        SolutionBuild? solutionBuild = null;
        SolutionConfiguration? configuration = null;
        SolutionContexts? contexts = null;
        List<string> platforms = new();
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

            string[] uniquePlatforms = platforms
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            activePlatform = uniquePlatforms.Length == 1
                ? uniquePlatforms[0]
                : null;
        }
        finally
        {
            ComObject.Release(contexts);
            ComObject.Release(configuration);
            ComObject.Release(solutionBuild);
            ComObject.Release(solution);
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
        string normalizedSolution)
    {
        Solution? solution = null;
        Projects? projects = null;
        List<ITcSysManager> sysManagers = new();
        bool ownershipTransferred = false;
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

    private AgentWorkspaceOwnershipResult AcquireAgentWorkspaceOnSta()
    {
        DTE2 dte = _dte
            ?? throw new GatewayOperationException(
                ErrorCodes.XaeNotFound,
                "No XAE session is currently attached.",
                retryable: true,
                stage: "xae.workspace.acquire");
        string solutionPath = _snapshot.SelectedInstance?.Solution
            ?? throw new GatewayOperationException(
                ErrorCodes.SolutionMismatch,
                "The attached XAE solution path is unavailable.",
                retryable: true,
                stage: "xae.workspace.acquire");
        using (CreateUserSilentModeLease())
        {
            AgentWorkspaceOwnershipResult ownership =
                AgentWorkspaceOwnership.Acquire(
                    dte,
                    solutionPath);
            _snapshot.AgentWorkspaceOwned = true;
            _snapshot.ClosedDocumentCount =
                ownership.ClosedDocuments.Count;
            _snapshot.DiscardedDocumentCount =
                ownership.DiscardedDocuments.Count;
            return ownership;
        }
    }

    private XaeDocumentSynchronizationResult
        SynchronizeExternalChangesOnSta(
            string solutionPath,
            IEnumerable<string> changedPaths)
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
                    solutionPath,
                    changedPaths);
            _snapshot.AgentWorkspaceOwned = true;
            _snapshot.ClosedDocumentCount = 0;
            _snapshot.DiscardedDocumentCount =
                result.DiscardedDocuments.Count;
            return result;
        }
    }

    private Task<XaeBuildEventEvidence> StartBuildOnSta(
        BuildAction action)
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
            action);
        return _activeBuild.Completion;
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

    private void InitializeFingerprintBaseline(
        string solutionPath,
        CancellationToken cancellationToken)
    {
        ProjectFileFingerprintSnapshot baseline =
            ProjectFileFingerprintScanner.Capture(
                solutionPath,
                cancellationToken);
        lock (_fingerprintSync)
        {
            _fingerprintSolution = solutionPath;
            _fingerprintBaseline = baseline;
        }
    }

    private FingerprintState GetFingerprintState()
    {
        lock (_fingerprintSync)
        {
            if (_fingerprintSolution is null
                || _fingerprintBaseline is null)
            {
                throw new GatewayOperationException(
                    ErrorCodes.GatewayNotReady,
                    "Agent workspace fingerprints are not initialized.",
                    retryable: true,
                    stage: "xae.workspace.fingerprint");
            }

            return new FingerprintState(
                _fingerprintSolution,
                _fingerprintBaseline);
        }
    }

    private void ReplaceFingerprintBaseline(
        string solutionPath,
        ProjectFileFingerprintSnapshot baseline)
    {
        lock (_fingerprintSync)
        {
            if (!string.Equals(
                _fingerprintSolution,
                solutionPath,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new GatewayOperationException(
                    ErrorCodes.SolutionMismatch,
                    "The tracked XAE solution changed while "
                    + "external files were synchronized.",
                    retryable: true,
                    stage: "xae.workspace.verify");
            }

            _fingerprintBaseline = baseline;
        }
    }

    private void ClearFingerprintBaseline()
    {
        lock (_fingerprintSync)
        {
            _fingerprintSolution = null;
            _fingerprintBaseline = null;
        }
    }

    private static string NormalizeChangedPath(
        string solutionPath,
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
        string rootPrefix = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(
            rootPrefix,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayOperationException(
                ErrorCodes.RequestInvalid,
                "Changed project file must be inside the selected "
                + "solution directory.",
                stage: "xae.workspace.validate");
        }

        if (!ProjectFileFingerprintScanner.IsSupportedPath(fullPath))
        {
            throw new GatewayOperationException(
                ErrorCodes.ExternalEditUnsupported,
                $"External synchronization does not support '{fullPath}'.",
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
        finally
        {
            ComObject.Release(_sysManager);
            ComObject.Release(_dte);
            _sysManager = null;
            _dte = null;
            _snapshot = new XaeSessionSnapshot();
        }

        if (cleanupException is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupException).Throw();
        }
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
            AgentWorkspaceOwned = source.AgentWorkspaceOwned,
            ClosedDocumentCount = source.ClosedDocumentCount,
            DiscardedDocumentCount = source.DiscardedDocumentCount,
            ActiveConfiguration = source.ActiveConfiguration,
            ActivePlatform = source.ActivePlatform,
            TargetAmsNetId = source.TargetAmsNetId,
            LastErrorMessages =
                source.LastErrorMessages.ToArray(),
            DiagnosticIssues =
                source.DiagnosticIssues.ToArray(),
            DiscoveredInstances = source.DiscoveredInstances
                .Select(instance => CloneInfo(instance)!)
                .ToArray(),
        };
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
            ProjectFileFingerprintSnapshot baseline)
        {
            SolutionPath = solutionPath;
            Baseline = baseline;
        }

        public string SolutionPath { get; }

        public ProjectFileFingerprintSnapshot Baseline { get; }
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
