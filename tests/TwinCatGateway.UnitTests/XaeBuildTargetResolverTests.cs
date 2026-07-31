using System;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class XaeBuildTargetResolverTests
{
    [Fact]
    public void SelectsOnlyPlcWhenProjectIsOmitted()
    {
        ResolvedXaeBuildTarget result =
            XaeBuildTargetResolver.Resolve(
                Graph(Project("MachinePlc", @"C:\Machine\A.plcproj")),
                XaeBuildScope.Plc,
                requestedProject: null);

        Assert.Equal(XaeBuildScope.Plc, result.Scope);
        Assert.Equal("MachinePlc", result.Project);
        Assert.Equal(@"C:\Machine\A.plcproj", result.ProjectFile);
    }

    [Fact]
    public void RequiresProjectWhenGraphContainsMultiplePlcs()
    {
        GatewayOperationException exception = Assert.Throws<
            GatewayOperationException>(() =>
            XaeBuildTargetResolver.Resolve(
                Graph(
                    Project("First", @"C:\Machine\A.plcproj"),
                    Project("Second", @"C:\Machine\B.plcproj")),
                XaeBuildScope.Plc,
                requestedProject: null));

        Assert.Equal(ErrorCodes.BuildProjectAmbiguous, exception.Code);
        Assert.Equal("xae.build.project.resolve", exception.Stage);
    }

    [Fact]
    public void SelectsExplicitProjectCaseInsensitively()
    {
        ResolvedXaeBuildTarget result =
            XaeBuildTargetResolver.Resolve(
                Graph(
                    Project("First", @"C:\Machine\A.plcproj"),
                    Project("Second", @"C:\Machine\B.plcproj")),
                XaeBuildScope.Plc,
                " second ");

        Assert.Equal("Second", result.Project);
        Assert.Equal(@"C:\Machine\B.plcproj", result.ProjectFile);
    }

    [Fact]
    public void RejectsUnknownProject()
    {
        GatewayOperationException exception = Assert.Throws<
            GatewayOperationException>(() =>
            XaeBuildTargetResolver.Resolve(
                Graph(Project("First", @"C:\Machine\A.plcproj")),
                XaeBuildScope.Plc,
                "Missing"));

        Assert.Equal(ErrorCodes.BuildProjectNotFound, exception.Code);
    }

    [Fact]
    public void RejectsDuplicateLogicalProjectIdentity()
    {
        GatewayOperationException exception = Assert.Throws<
            GatewayOperationException>(() =>
            XaeBuildTargetResolver.Resolve(
                Graph(
                    Project("Duplicate", @"C:\Machine\A.plcproj"),
                    Project("duplicate", @"C:\Machine\B.plcproj")),
                XaeBuildScope.Plc,
                "Duplicate"));

        Assert.Equal(ErrorCodes.BuildProjectAmbiguous, exception.Code);
    }

    [Fact]
    public void RejectsProjectForSolutionScope()
    {
        GatewayOperationException exception = Assert.Throws<
            GatewayOperationException>(() =>
            XaeBuildTargetResolver.Resolve(
                Graph(Project("First", @"C:\Machine\A.plcproj")),
                XaeBuildScope.Solution,
                "First"));

        Assert.Equal(ErrorCodes.RequestInvalid, exception.Code);
    }

    [Fact]
    public void SolutionScopeDoesNotRequireCompleteProjectGraph()
    {
        ResolvedXaeBuildTarget result =
            XaeBuildTargetResolver.Resolve(
                Graph(isComplete: false),
                XaeBuildScope.Solution,
                requestedProject: null);

        Assert.Equal(XaeBuildScope.Solution, result.Scope);
        Assert.Null(result.Project);
        Assert.Null(result.ProjectFile);
    }

    private static TwinCatProjectGraphSnapshot Graph(
        params TwinCatProjectGraphEntry[] entries)
    {
        return Graph(true, entries);
    }

    private static TwinCatProjectGraphSnapshot Graph(
        bool isComplete,
        params TwinCatProjectGraphEntry[] entries)
    {
        return new TwinCatProjectGraphSnapshot(
            @"C:\Machine\Machine.sln",
            @"C:\Machine\Machine.tsproj",
            entries,
            isComplete);
    }

    private static TwinCatProjectGraphEntry Project(
        string name,
        string path)
    {
        return new TwinCatProjectGraphEntry(
            path,
            ProjectGraphFileRole.PlcProject,
            name,
            path,
            SourceEntryKind.Unsupported,
            exists: true,
            outsideSolutionDirectory: false);
    }
}
