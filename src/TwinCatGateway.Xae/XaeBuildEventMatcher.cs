using EnvDTE;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Xae;

internal static class XaeBuildEventMatcher
{
    public static bool IsCompletionEvent(
        XaeBuildScope? expectedScope,
        BuildAction requestedAction,
        vsBuildScope scope,
        vsBuildAction action,
        vsBuildState buildState)
    {
        if (buildState != vsBuildState.vsBuildStateDone)
        {
            return false;
        }

        if (expectedScope == XaeBuildScope.Plc
            && scope != vsBuildScope.vsBuildScopeProject)
        {
            return false;
        }

        if (expectedScope == XaeBuildScope.Solution
            && scope != vsBuildScope.vsBuildScopeSolution)
        {
            return false;
        }

        return requestedAction switch
        {
            BuildAction.Build =>
                action == vsBuildAction.vsBuildActionBuild,
            BuildAction.Clean =>
                action == vsBuildAction.vsBuildActionClean,
            BuildAction.Rebuild =>
                action == vsBuildAction.vsBuildActionRebuildAll
                || action == vsBuildAction.vsBuildActionBuild,
            _ => false,
        };
    }
}
