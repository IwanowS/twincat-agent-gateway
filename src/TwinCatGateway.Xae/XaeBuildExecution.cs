using System;
using System.Diagnostics;
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
        ExternalChangeSynchronizationResult synchronization)
    {
        Action = action;
        DurationMs = durationMs;
        FailedProjects = failedProjects;
        BuildState = buildState;
        EventScope = eventScope;
        EventAction = eventAction;
        Synchronization = synchronization;
    }

    public BuildAction Action { get; }

    public long DurationMs { get; }

    public int FailedProjects { get; }

    public vsBuildState BuildState { get; }

    public vsBuildScope EventScope { get; }

    public vsBuildAction EventAction { get; }

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
    private bool _rebuildCleanCompleted;
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
        return new XaeBuildExecutionResult(
            _requestedAction,
            _stopwatch.ElapsedMilliseconds,
            failedProjects,
            _solutionBuild.BuildState,
            evidence.Scope,
            evidence.Action,
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
                    vsBuildAction.vsBuildActionClean;
                _solutionBuild.Clean(
                    WaitForCleanToFinish: false);
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

        try
        {
            if (_requestedAction == BuildAction.Rebuild
                && !_rebuildCleanCompleted)
            {
                _rebuildCleanCompleted = true;
                _expectedAction =
                    vsBuildAction.vsBuildActionBuild;
                _solutionBuild.Build(
                    WaitForBuildToFinish: false);
                return;
            }

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
