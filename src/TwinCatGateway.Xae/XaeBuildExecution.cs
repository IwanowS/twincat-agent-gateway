using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;

namespace TwinCatGateway.Xae;

public sealed class XaeBuildExecutionResult
{
    internal XaeBuildExecutionResult(
        BuildAction action,
        long durationMs,
        int failedProjects,
        vsBuildState buildState,
        vsBuildScope eventScope,
        vsBuildAction eventAction,
        IEnumerable<BuildDiagnostic> diagnostics,
        ExternalChangeSynchronizationResult synchronization)
    {
        Action = action;
        DurationMs = durationMs;
        FailedProjects = failedProjects;
        BuildState = buildState;
        EventScope = eventScope;
        EventAction = eventAction;
        Diagnostics = diagnostics.ToArray();
        Synchronization = synchronization;
    }

    public BuildAction Action { get; }

    public long DurationMs { get; }

    public int FailedProjects { get; }

    public vsBuildState BuildState { get; }

    public vsBuildScope EventScope { get; }

    public vsBuildAction EventAction { get; }

    public IReadOnlyList<BuildDiagnostic> Diagnostics { get; }

    public ExternalChangeSynchronizationResult Synchronization { get; }
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
    private vsBuildAction _expectedAction;
    private bool _disposed;

    private XaeBuildEventLease(
        DTE2 dte,
        BuildAction requestedAction)
    {
        _dte = dte;
        _requestedAction = requestedAction;
        _events = dte.Events;
        _buildEvents = _events.BuildEvents;
        _solution = dte.Solution;
        _solutionBuild = _solution.SolutionBuild;
        _doneHandler = OnBuildDone;
        _buildEvents.OnBuildDone += _doneHandler;
    }

    public Task<XaeBuildEventEvidence> Completion =>
        _completion.Task;

    public static XaeBuildEventLease Start(
        DTE2 dte,
        BuildAction action)
    {
        XaeBuildEventLease? lease = null;
        try
        {
            lease = new XaeBuildEventLease(dte, action);
            lease.StartFirstPhase();
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

        int failedProjects = _requestedAction == BuildAction.Clean
            ? 0
            : _solutionBuild.LastBuildInfo;
        IEnumerable<BuildDiagnostic> diagnostics =
            _requestedAction == BuildAction.Clean
                ? Array.Empty<BuildDiagnostic>()
                : ReadErrorList();
        return new XaeBuildExecutionResult(
            _requestedAction,
            _stopwatch.ElapsedMilliseconds,
            failedProjects,
            _solutionBuild.BuildState,
            evidence.Scope,
            evidence.Action,
            diagnostics,
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
            _buildEvents.OnBuildDone -= _doneHandler;
        }
        finally
        {
            ComObject.Release(_solutionBuild);
            ComObject.Release(_solution);
            ComObject.Release(_buildEvents);
            ComObject.Release(_events);
        }
    }

    private void StartFirstPhase()
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

        switch (_requestedAction)
        {
            case BuildAction.Build:
                _expectedAction =
                    vsBuildAction.vsBuildActionBuild;
                _solutionBuild.Build(
                    WaitForBuildToFinish: false);
                break;
            case BuildAction.Clean:
                _expectedAction =
                    vsBuildAction.vsBuildActionClean;
                _solutionBuild.Clean(
                    WaitForCleanToFinish: false);
                break;
            case BuildAction.Rebuild:
                _expectedAction =
                    vsBuildAction.vsBuildActionRebuildAll;
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
        if (action != _expectedAction)
        {
            return;
        }

        if ((action == vsBuildAction.vsBuildActionBuild
                || action
                    == vsBuildAction.vsBuildActionRebuildAll)
            && scope != vsBuildScope.vsBuildScopeSolution)
        {
            return;
        }

        if (action == vsBuildAction.vsBuildActionClean
            && _solutionBuild.BuildState
                != vsBuildState.vsBuildStateDone)
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

    private List<BuildDiagnostic> ReadErrorList()
    {
        ToolWindows? toolWindows = null;
        ErrorList? errorList = null;
        ErrorItems? items = null;
        List<BuildDiagnostic> diagnostics = new();
        try
        {
            toolWindows = _dte.ToolWindows;
            errorList = toolWindows.ErrorList;
            items = errorList.ErrorItems;
            int count = items.Count;
            for (int index = 1; index <= count; index++)
            {
                ErrorItem? item = null;
                try
                {
                    item = items.Item(index);
                    diagnostics.Add(
                        new BuildDiagnostic
                        {
                            Severity = MapSeverity(
                                item.ErrorLevel),
                            Source = "xae-error-list",
                            Message = item.Description
                                ?? string.Empty,
                            File = string.IsNullOrWhiteSpace(
                                item.FileName)
                                ? null
                                : item.FileName,
                            Line = item.Line > 0
                                ? item.Line
                                : null,
                            Column = item.Column > 0
                                ? item.Column
                                : null,
                        });
                }
                finally
                {
                    ComObject.Release(item);
                }
            }

            return diagnostics;
        }
        finally
        {
            ComObject.Release(items);
            ComObject.Release(errorList);
            ComObject.Release(toolWindows);
        }
    }

    private static DiagnosticSeverity MapSeverity(
        vsBuildErrorLevel level)
    {
        switch (level)
        {
            case vsBuildErrorLevel.vsBuildErrorLevelHigh:
                return DiagnosticSeverity.Error;
            case vsBuildErrorLevel.vsBuildErrorLevelMedium:
                return DiagnosticSeverity.Warning;
            default:
                return DiagnosticSeverity.Info;
        }
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
