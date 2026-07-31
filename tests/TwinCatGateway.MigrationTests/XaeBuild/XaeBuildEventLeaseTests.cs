using EnvDTE;
using TwinCatGateway.Contracts;
using TwinCatGateway.Xae;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class XaeBuildEventLeaseTests
{
    [Fact]
    public void ActivationObserverAcceptsFinalProjectScopeBuild()
    {
        Assert.True(
            XaeBuildEventMatcher.IsCompletionEvent(
                expectedScope: null,
                BuildAction.Build,
                vsBuildScope.vsBuildScopeProject,
                vsBuildAction.vsBuildActionBuild,
                vsBuildState.vsBuildStateDone));
    }

    [Fact]
    public void ActivationObserverRejectsProjectEventWhileBuildContinues()
    {
        Assert.False(
            XaeBuildEventMatcher.IsCompletionEvent(
                expectedScope: null,
                BuildAction.Build,
                vsBuildScope.vsBuildScopeProject,
                vsBuildAction.vsBuildActionBuild,
                vsBuildState.vsBuildStateInProgress));
    }

    [Fact]
    public void StandaloneBuildRequiresRequestedScope()
    {
        Assert.False(
            XaeBuildEventMatcher.IsCompletionEvent(
                XaeBuildScope.Solution,
                BuildAction.Build,
                vsBuildScope.vsBuildScopeProject,
                vsBuildAction.vsBuildActionBuild,
                vsBuildState.vsBuildStateDone));
        Assert.True(
            XaeBuildEventMatcher.IsCompletionEvent(
                XaeBuildScope.Solution,
                BuildAction.Build,
                vsBuildScope.vsBuildScopeSolution,
                vsBuildAction.vsBuildActionBuild,
                vsBuildState.vsBuildStateDone));
    }

    [Theory]
    [InlineData(BuildAction.Clean, vsBuildAction.vsBuildActionClean)]
    [InlineData(BuildAction.Rebuild, vsBuildAction.vsBuildActionRebuildAll)]
    [InlineData(BuildAction.Rebuild, vsBuildAction.vsBuildActionBuild)]
    public void ProjectActionsAcceptTheirFinalProjectEvent(
        BuildAction requested,
        vsBuildAction observed)
    {
        Assert.True(
            XaeBuildEventMatcher.IsCompletionEvent(
                XaeBuildScope.Plc,
                requested,
                vsBuildScope.vsBuildScopeProject,
                observed,
                vsBuildState.vsBuildStateDone));
    }

    [Fact]
    public void ProjectActionRejectsFinalSolutionEvent()
    {
        Assert.False(
            XaeBuildEventMatcher.IsCompletionEvent(
                XaeBuildScope.Plc,
                BuildAction.Clean,
                vsBuildScope.vsBuildScopeSolution,
                vsBuildAction.vsBuildActionClean,
                vsBuildState.vsBuildStateDone));
    }
}
