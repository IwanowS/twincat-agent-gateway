using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;

namespace TwinCatGateway.Xae;

public sealed class XaeSession : IDisposable
{
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PollCallLimit =
        TimeSpan.FromSeconds(3);
    private readonly ComStaDispatcher _dispatcher;
    private readonly bool _ownsDispatcher;
    private object? _dte;
    private object? _sysManager;
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

    public Task<XaeSessionSnapshot> AttachAsync(
        string solutionPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string normalizedSolution = NormalizeSolutionPath(solutionPath);
        return _dispatcher.InvokeAsync(
            () => AttachOnSta(normalizedSolution),
            timeout,
            cancellationToken);
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
        return await WaitForLaunchedSolutionAsync(
            normalizedSolution,
            started.ProcessId,
            deadlineUtc,
            cancellationToken).ConfigureAwait(false);
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

    public Task DisconnectAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _dispatcher.InvokeAsync(
            () =>
            {
                ReleaseSessionOnSta();
                return true;
            },
            timeout,
            cancellationToken);
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
                using Process process = Process.Start(startInfo)
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

        object dte = candidate.TakeDte();
        ReleaseSessionOnSta();
        _dte = dte;
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
        object? solution = null;
        try
        {
            dynamic automation = _dte!;
            automation.UserControl = true;
            automation.SuppressUI = true;
            solution = automation.Solution;
            string? currentSolution = NormalizeOptionalPath(
                Convert.ToString(
                    ((dynamic)solution).FullName,
                    CultureInfo.InvariantCulture));
            if (currentSolution is null)
            {
                ((dynamic)solution).Open(normalizedSolution);
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

        object sysManager;
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
            DiscoveredInstances = new[] { CloneInfo(info)! },
        };
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
        object selectedDte = selected.TakeDte();
        object selectedSysManager;
        try
        {
            selectedSysManager = AcquireSysManager(
                selectedDte,
                normalizedSolution);
        }
        catch
        {
            ComObject.Release(selectedDte);
            throw;
        }

        ReleaseSessionOnSta();
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
            DiscoveredInstances = instances,
        };
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

        DteInstanceInfo info = RunningObjectTableScanner.InspectDte(
            _snapshot.SelectedInstance?.Moniker ?? string.Empty,
            _dte);
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
            DiscoveredInstances = _snapshot.DiscoveredInstances
                .Select(instance =>
                    instance.Selected
                        ? CloneInfo(info)!
                        : CloneInfo(instance)!)
                .ToArray(),
        };
        return CloneSnapshot(_snapshot);
    }

    private static object AcquireSysManager(
        object dte,
        string normalizedSolution)
    {
        object? solution = null;
        object? projects = null;
        List<object> sysManagers = new();
        bool ownershipTransferred = false;
        try
        {
            solution = ((dynamic)dte).Solution;
            string? actualSolution = Convert.ToString(
                ((dynamic)solution).FullName,
                CultureInfo.InvariantCulture);
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

            projects = ((dynamic)solution).Projects;
            int count = Convert.ToInt32(
                ((dynamic)projects).Count,
                CultureInfo.InvariantCulture);
            for (int index = 1; index <= count; index++)
            {
                object? project = null;
                try
                {
                    project = ((dynamic)projects).Item(index);
                    string? projectPath = Convert.ToString(
                        ((dynamic)project).FullName,
                        CultureInfo.InvariantCulture);
                    if (!string.Equals(
                        Path.GetExtension(projectPath),
                        ".tsproj",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    object? sysManager = ((dynamic)project).Object;
                    if (sysManager is not null)
                    {
                        sysManagers.Add(sysManager);
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
                foreach (object sysManager in sysManagers)
                {
                    ComObject.Release(sysManager);
                }
            }

            ComObject.Release(projects);
            ComObject.Release(solution);
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

        object? solution = null;
        try
        {
            dynamic automation = _dte;
            solution = automation.Solution;
            string? solutionPath = Convert.ToString(
                ((dynamic)solution).FullName,
                CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(solutionPath))
            {
                ((dynamic)solution).Close(false);
            }

            automation.Quit();
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
        ComObject.Release(_sysManager);
        ComObject.Release(_dte);
        _sysManager = null;
        _dte = null;
        _snapshot = new XaeSessionSnapshot();
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

    private static XaeSessionSnapshot CloneSnapshot(
        XaeSessionSnapshot source)
    {
        return new XaeSessionSnapshot
        {
            Connected = source.Connected,
            SelectedInstance = CloneInfo(source.SelectedInstance),
            SysManagerAvailable = source.SysManagerAvailable,
            LaunchedByGateway = source.LaunchedByGateway,
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
