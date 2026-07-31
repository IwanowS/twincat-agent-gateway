using System;
using System.Collections.Generic;
using EnvDTE;
using Microsoft.VisualStudio.Shell.Interop;
using TwinCatGateway.Contracts;
using TwinCatGateway.Xae;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class XaeBuildEventLeaseTests
{
    [Fact]
    public void RunningDocumentDirtyPathIsMergedWhenDteOmitsPlcEditor()
    {
        string main = @"C:\Fixture\PlcProject2\POUs\MAIN.TcPOU";

        IReadOnlyList<string> dirty =
            XaeDirtyDocumentSet.MergeProjectGraphPaths(
                new[] { main, @"C:\Fixture\Machine.tsproj" },
                Array.Empty<string>(),
                new[]
                {
                    main.ToLowerInvariant(),
                    @"C:\Unrelated\Other.TcPOU",
                });

        Assert.Equal(new[] { main }, dirty, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(BuildAction.Clean, VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_CLEAN)]
    [InlineData(
        BuildAction.Rebuild,
        VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_CLEAN
            | VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_BUILD)]
    public void ProjectUpdateUsesExplicitSingleProjectFlags(
        BuildAction action,
        VSSOLNBUILDUPDATEFLAGS expected)
    {
        Assert.Equal(
            unchecked((uint)expected),
            XaeProjectBuildRequest.SelectFlags(action));
    }

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
