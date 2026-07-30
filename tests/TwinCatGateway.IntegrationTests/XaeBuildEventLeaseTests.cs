using EnvDTE;
using TwinCatGateway.Xae;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class XaeBuildEventLeaseTests
{
    [Fact]
    public void ActivationObserverAcceptsFinalProjectScopeBuild()
    {
        Assert.True(
            XaeBuildEventLease.IsCompletionEvent(
                requireSolutionScope: false,
                vsBuildAction.vsBuildActionBuild,
                vsBuildScope.vsBuildScopeProject,
                vsBuildAction.vsBuildActionBuild,
                vsBuildState.vsBuildStateDone));
    }

    [Fact]
    public void ActivationObserverRejectsProjectEventWhileBuildContinues()
    {
        Assert.False(
            XaeBuildEventLease.IsCompletionEvent(
                requireSolutionScope: false,
                vsBuildAction.vsBuildActionBuild,
                vsBuildScope.vsBuildScopeProject,
                vsBuildAction.vsBuildActionBuild,
                vsBuildState.vsBuildStateInProgress));
    }

    [Fact]
    public void StandaloneBuildStillRequiresSolutionScope()
    {
        Assert.False(
            XaeBuildEventLease.IsCompletionEvent(
                requireSolutionScope: true,
                vsBuildAction.vsBuildActionBuild,
                vsBuildScope.vsBuildScopeProject,
                vsBuildAction.vsBuildActionBuild,
                vsBuildState.vsBuildStateDone));
        Assert.True(
            XaeBuildEventLease.IsCompletionEvent(
                requireSolutionScope: true,
                vsBuildAction.vsBuildActionBuild,
                vsBuildScope.vsBuildScopeSolution,
                vsBuildAction.vsBuildActionBuild,
                vsBuildState.vsBuildStateDone));
    }
}
