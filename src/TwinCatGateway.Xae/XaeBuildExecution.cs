using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell.Interop;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;

namespace TwinCatGateway.Xae;

public sealed class XaeBuildExecutionResult
{
    internal XaeBuildExecutionResult(
        BuildAction action,
        XaeBuildScope scope,
        string? project,
        long durationMs,
        int failedProjects,
        vsBuildState buildState,
        vsBuildScope eventScope,
        vsBuildAction eventAction,
        IEnumerable<BuildDiagnostic> diagnostics,
        IEnumerable<XaeOutputDelta> output,
        IEnumerable<XaeProjectFileChangeResult> projectChanges,
        ExternalChangeSynchronizationResult synchronization,
        XaeAcceptedProjectGraphChanges? acceptedProjectChanges = null)
    {
        Action = action;
        Scope = scope;
        Project = project;
        DurationMs = durationMs;
        FailedProjects = failedProjects;
        BuildState = buildState;
        EventScope = eventScope;
        EventAction = eventAction;
        Diagnostics = diagnostics.ToArray();
        Output = output.ToArray();
        ProjectChanges = projectChanges.ToArray();
        Synchronization = synchronization;
        AcceptedProjectChanges = acceptedProjectChanges;
    }

    public BuildAction Action { get; }

    public XaeBuildScope Scope { get; }

    public string? Project { get; }

    public long DurationMs { get; }

    public int FailedProjects { get; }

    public vsBuildState BuildState { get; }

    public vsBuildScope EventScope { get; }

    public vsBuildAction EventAction { get; }

    public IReadOnlyList<BuildDiagnostic> Diagnostics { get; }

    public IReadOnlyList<XaeOutputDelta> Output { get; }

    public IReadOnlyList<XaeProjectFileChangeResult> ProjectChanges { get; }

    public ExternalChangeSynchronizationResult Synchronization { get; }

    public XaeAcceptedProjectGraphChanges? AcceptedProjectChanges
    {
        get;
        internal set;
    }
}

internal sealed class XaeBuildEventLease : IDisposable
{
    private readonly BuildAction _requestedAction;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly TaskCompletionSource<XaeBuildEventEvidence> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly _dispBuildEvents_OnBuildDoneEventHandler _doneHandler;
    private readonly DTE2 _dte;
    private readonly Events _events;
    private readonly BuildEvents _buildEvents;
    private readonly Solution _solution;
    private readonly SolutionBuild _solutionBuild;
    private readonly XaeOutputSnapshot _outputSnapshot;
    private readonly XaeProjectFileChangeLease _projectFileLease;
    private readonly XaeBuildScope? _expectedScope;
    private readonly string? _project;
    private readonly IReadOnlyList<BuildDiagnostic>
        _errorListBaseline = Array.Empty<BuildDiagnostic>();
    private bool _disposed;

    private XaeBuildEventLease(
        DTE2 dte,
        BuildAction requestedAction,
        XaeBuildScope? expectedScope,
        string? project,
        IXaeProjectFileChangeGuard? workspaceGuard)
    {
        _dte = dte;
        _requestedAction = requestedAction;
        _expectedScope = expectedScope;
        _project = project;
        _events = dte.Events;
        _buildEvents = _events.BuildEvents;
        _solution = dte.Solution;
        _solutionBuild = _solution.SolutionBuild;
        _doneHandler = OnBuildDone;
        XaeProjectFileChangeLease? projectFileLease = null;
        bool subscribed = false;
        try
        {
            projectFileLease =
                XaeProjectFileChangeLease.Acquire(
                    dte,
                    _solution.FullName,
                    workspaceGuard);
            _projectFileLease = projectFileLease;
            _outputSnapshot =
                XaeOutputCollector.Capture(dte);
            _errorListBaseline =
                XaeErrorListReader.Read(dte);
            _buildEvents.OnBuildDone += _doneHandler;
            subscribed = true;
        }
        catch
        {
            if (subscribed)
            {
                _buildEvents.OnBuildDone -= _doneHandler;
            }

            projectFileLease?.Dispose();
            ComObject.Release(_solutionBuild);
            ComObject.Release(_solution);
            ComObject.Release(_buildEvents);
            ComObject.Release(_events);
            throw;
        }
    }

    public Task<XaeBuildEventEvidence> Completion =>
        _completion.Task;

    public static XaeBuildEventLease Start(
        DTE2 dte,
        BuildAction action,
        XaeBuildScope scope,
        string? project,
        string? projectFile,
        IXaeProjectFileChangeGuard? workspaceGuard = null)
    {
        XaeBuildEventLease? lease = null;
        try
        {
            lease = new XaeBuildEventLease(
                dte,
                action,
                scope,
                project,
                workspaceGuard);
            lease.StartFirstPhase(scope, projectFile);
            return lease;
        }
        catch
        {
            lease?.Dispose();
            throw;
        }
    }

    public static XaeBuildEventLease ObserveNext(
        DTE2 dte,
        BuildAction action,
        IXaeProjectFileChangeGuard? workspaceGuard = null)
    {
        XaeBuildEventLease? lease = null;
        try
        {
            lease = new XaeBuildEventLease(
                dte,
                action,
                expectedScope: null,
                project: null,
                workspaceGuard);
            return lease;
        }
        catch
        {
            lease?.Dispose();
            throw;
        }
    }

    public XaeBuildExecutionResult Complete(
        XaeBuildEventEvidence evidence,
        ExternalChangeSynchronizationResult synchronization)
    {
        if (_solutionBuild.BuildState
            != vsBuildState.vsBuildStateDone)
        {
            throw new GatewayOperationException(
                ErrorCodes.BuildResultInconsistent,
                "XAE raised OnBuildDone but BuildState is not Done.",
                retryable: true,
                stage: "xae.build.verify");
        }

        IReadOnlyList<XaeProjectFileChangeResult> projectChanges =
            _projectFileLease.ClassifyChangesAndRelease(
                _requestedAction);
        int failedProjects = _requestedAction == BuildAction.Clean
            ? 0
            : _solutionBuild.LastBuildInfo;
        List<BuildDiagnostic> diagnostics =
            _requestedAction == BuildAction.Clean
                ? new List<BuildDiagnostic>()
                : BuildDiagnosticMultiset.Except(
                        XaeErrorListReader.Read(_dte),
                        _errorListBaseline)
                    .ToList();
        IReadOnlyList<XaeOutputDelta> output =
            XaeOutputCollector.ReadDelta(
                _dte,
                _outputSnapshot);
        if (_requestedAction != BuildAction.Clean)
        {
            foreach (BuildDiagnostic diagnostic in output
                .SelectMany(pane =>
                    BuildOutputDiagnosticParser.Parse(
                        pane.Text)))
            {
                BuildDiagnostic? existing =
                    diagnostics.FirstOrDefault(item =>
                        IsEquivalentDiagnostic(
                            item,
                            diagnostic));
                if (existing is null)
                {
                    diagnostics.Add(diagnostic);
                    continue;
                }

                existing.Source = diagnostic.Source;
                existing.Code ??= diagnostic.Code;
                existing.Line =
                    diagnostic.Line ?? existing.Line;
                existing.Column =
                    diagnostic.Column ?? existing.Column;
            }
        }
        return new XaeBuildExecutionResult(
            _requestedAction,
            _expectedScope ?? XaeBuildScope.Solution,
            _project,
            _stopwatch.ElapsedMilliseconds,
            failedProjects,
            _solutionBuild.BuildState,
            evidence.Scope,
            evidence.Action,
            diagnostics,
            output,
            projectChanges,
            synchronization);
    }

    public void Cancel()
    {
        if (_solutionBuild.BuildState
            == vsBuildState.vsBuildStateInProgress)
        {
            _dte.ExecuteCommand("Build.Cancel");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            try
            {
                _buildEvents.OnBuildDone -= _doneHandler;
            }
            finally
            {
                _projectFileLease.Dispose();
            }
        }
        finally
        {
            ComObject.Release(_solutionBuild);
            ComObject.Release(_solution);
            ComObject.Release(_buildEvents);
            ComObject.Release(_events);
        }
    }

    private void StartFirstPhase(
        XaeBuildScope scope,
        string? projectFile)
    {
        if (_solutionBuild.BuildState
            == vsBuildState.vsBuildStateInProgress)
        {
            throw new GatewayOperationException(
                ErrorCodes.XaeBusy,
                "XAE is already running a build operation.",
                retryable: true,
                stage: "xae.build.start");
        }

        if (scope == XaeBuildScope.Plc)
        {
            if (string.IsNullOrWhiteSpace(projectFile))
            {
                throw new ArgumentException(
                    "PLC scope requires an exact project file.",
                    nameof(projectFile));
            }

            string exactProjectFile = projectFile!;

            if (_requestedAction == BuildAction.Build)
            {
                _solutionBuild.BuildProject(
                    GetActiveSolutionConfiguration(),
                    exactProjectFile,
                    WaitForBuildToFinish: false);
            }
            else
            {
                StartSpecificProjectUpdate(
                    _dte,
                    exactProjectFile,
                    clean: true,
                    build: _requestedAction == BuildAction.Rebuild);
            }

            return;
        }

        switch (_requestedAction)
        {
            case BuildAction.Build:
                _solutionBuild.Build(
                    WaitForBuildToFinish: false);
                break;
            case BuildAction.Clean:
                _solutionBuild.Clean(
                    WaitForCleanToFinish: false);
                break;
            case BuildAction.Rebuild:
                _dte.ExecuteCommand(
                    "Build.RebuildSolution");
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(_requestedAction));
        }
    }

    private void OnBuildDone(
        vsBuildScope scope,
        vsBuildAction action)
    {
        if (!XaeBuildEventMatcher.IsCompletionEvent(
                _expectedScope,
                _requestedAction,
                scope,
                action,
                _solutionBuild.BuildState))
        {
            return;
        }

        try
        {
            _completion.TrySetResult(
                new XaeBuildEventEvidence(
                    scope,
                    action));
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }
    }

    private string GetActiveSolutionConfiguration()
    {
        SolutionConfiguration active =
            _solutionBuild.ActiveConfiguration;
        try
        {
            string name = active.Name;
            string? platform =
                (active as SolutionConfiguration2)?.PlatformName;
            return string.IsNullOrWhiteSpace(platform)
                ? name
                : $"{name}|{platform}";
        }
        finally
        {
            ComObject.Release(active);
        }
    }

    private static void StartSpecificProjectUpdate(
        DTE2 dte,
        string projectFile,
        bool clean,
        bool build)
    {
        IVsSolution? solution = null;
        IVsSolutionBuildManager2? buildManager = null;
        IVsHierarchy? hierarchy = null;
        IVsProjectCfg? projectConfiguration = null;
        try
        {
            solution = XaeSession.QueryService<IVsSolution>(
                dte,
                typeof(SVsSolution).GUID);
            Marshal.ThrowExceptionForHR(
                solution.GetProjectOfUniqueName(
                    projectFile,
                    out hierarchy));
            buildManager =
                XaeSession.QueryService<IVsSolutionBuildManager2>(
                    dte,
                    typeof(SVsSolutionBuildManager).GUID);
            IVsProjectCfg[] active = new IVsProjectCfg[1];
            Marshal.ThrowExceptionForHR(
                buildManager.FindActiveProjectCfg(
                    IntPtr.Zero,
                    IntPtr.Zero,
                    hierarchy,
                    active));
            projectConfiguration = active[0]
                ?? throw new GatewayOperationException(
                    ErrorCodes.BuildConfigurationNotFound,
                    "The active PLC project configuration is unavailable.",
                    stage: "xae.build.start",
                    component: GatewayComponent.Xae);
            uint[]? cleanFlags = clean ? new uint[1] : null;
            uint[]? buildFlags = build ? new uint[1] : null;
            Marshal.ThrowExceptionForHR(
                buildManager.StartUpdateSpecificProjectConfigurations(
                    1,
                    new[] { hierarchy },
                    new IVsCfg[] { projectConfiguration },
                    cleanFlags,
                    buildFlags,
                    rgdwDeployFlags: null,
                    dwFlags: 0,
                    fSuppressUI: 0));
        }
        finally
        {
            ComObject.Release(projectConfiguration);
            ComObject.Release(hierarchy);
            ComObject.Release(buildManager);
            ComObject.Release(solution);
        }
    }

    private static bool IsEquivalentDiagnostic(
        BuildDiagnostic left,
        BuildDiagnostic right)
    {
        return left.Severity == right.Severity
            && (string.IsNullOrWhiteSpace(left.Code)
                || string.IsNullOrWhiteSpace(right.Code)
                || string.Equals(
                    left.Code,
                    right.Code,
                    StringComparison.OrdinalIgnoreCase))
            && string.Equals(
                left.Message,
                right.Message,
                StringComparison.Ordinal)
            && string.Equals(
                left.File,
                right.File,
                StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class XaeBuildEventEvidence
{
    public XaeBuildEventEvidence(
        vsBuildScope scope,
        vsBuildAction action)
    {
        Scope = scope;
        Action = action;
    }

    public vsBuildScope Scope { get; }

    public vsBuildAction Action { get; }
}
