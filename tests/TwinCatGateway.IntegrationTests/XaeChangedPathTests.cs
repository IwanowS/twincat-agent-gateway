using System;
using System.IO;
using System.Collections.Generic;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Xae;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class XaeChangedPathTests
{
    [Fact]
    public void ReferencedTwinCatProjectOutsideSolutionIsAccepted()
    {
        using TemporaryWorkspace workspace = new();
        string twinCatProject = workspace.WriteProject(
            "Machine.tsproj");

        Assert.Equal(
            twinCatProject,
            XaeSession.NormalizeChangedPath(
                workspace.SolutionPath,
                new[] { twinCatProject },
                twinCatProject));
    }

    [Fact]
    public void PlcProjectMetadataInsideGraphIsAccepted()
    {
        using TemporaryWorkspace workspace = new();
        string twinCatProject = workspace.WriteProject(
            "Machine.tsproj");
        string plcProject = workspace.WriteProject(
            "Plc\\Machine.plcproj");

        Assert.Equal(
            plcProject,
            XaeSession.NormalizeChangedPath(
                workspace.SolutionPath,
                new[] { twinCatProject, plcProject },
                plcProject));
    }

    [Fact]
    public void PathOutsideSelectedWorkspaceIsRejected()
    {
        using TemporaryWorkspace workspace = new();
        string unrelated = Path.Combine(
            workspace.Root,
            "unrelated.tsproj");
        File.WriteAllText(unrelated, string.Empty);

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => XaeSession.NormalizeChangedPath(
                    workspace.SolutionPath,
                    new[]
                    {
                        Path.Combine(
                            workspace.ProjectRoot,
                            "Machine.tsproj"),
                    },
                    unrelated));

        Assert.Equal(ErrorCodes.RequestInvalid, exception.Code);
    }

    public static IEnumerable<object[]> PolicyCases()
    {
        yield return new object[]
        {
            ExternalChangePolicy.ReloadModified,
            ProjectGraphFileRole.PlcSource,
            ProjectFileChangeKind.Modified,
            SynchronizationScope.ModifiedSources,
        };
        yield return new object[]
        {
            ExternalChangePolicy.ReloadAll,
            ProjectGraphFileRole.PlcProject,
            ProjectFileChangeKind.Modified,
            SynchronizationScope.PlcProject,
        };
        yield return new object[]
        {
            ExternalChangePolicy.ReloadAll,
            ProjectGraphFileRole.TwinCatProject,
            ProjectFileChangeKind.Modified,
            SynchronizationScope.TwinCatProject,
        };
    }

    [Theory]
    [MemberData(nameof(PolicyCases))]
    public void PolicySelectsExpectedSmartReloadScope(
        ExternalChangePolicy policy,
        ProjectGraphFileRole role,
        ProjectFileChangeKind kind,
        SynchronizationScope expected)
    {
        SynchronizationScope actual =
            XaeSession.SelectSynchronizationScope(
                new[]
                {
                    new ProjectFileChange(
                        @"C:\fixture\changed.xml",
                        kind,
                        role),
                },
                policy,
                force: false,
                baselineMissing: false);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(ExternalChangePolicy.Error)]
    [InlineData(ExternalChangePolicy.ReloadModified)]
    public void RestrictivePoliciesRejectMetadataChanges(
        ExternalChangePolicy policy)
    {
        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => XaeSession.SelectSynchronizationScope(
                    new[]
                    {
                        new ProjectFileChange(
                            @"C:\fixture\Machine.plcproj",
                            ProjectFileChangeKind.Modified,
                            ProjectGraphFileRole.PlcProject),
                    },
                    policy,
                    force: false,
                    baselineMissing: false));

        Assert.Equal(
            ErrorCodes.ExternalChangeDetected,
            exception.Code);
    }

    [Fact]
    public void ForceWithoutBaselineRequiresFullTwinCatReload()
    {
        Assert.Equal(
            SynchronizationScope.TwinCatProject,
            XaeSession.SelectSynchronizationScope(
                Array.Empty<ProjectFileChange>(),
                ExternalChangePolicy.Error,
                force: true,
                baselineMissing: true));
    }

    [Fact]
    public void ForceWithMatchingFingerprintStillReloadsTwinCatProject()
    {
        Assert.Equal(
            SynchronizationScope.TwinCatProject,
            XaeSession.SelectSynchronizationScope(
                Array.Empty<ProjectFileChange>(),
                ExternalChangePolicy.ReloadModified,
                force: true,
            baselineMissing: false));
    }

    [Theory]
    [InlineData(ExternalChangePolicy.Error)]
    [InlineData(ExternalChangePolicy.ReloadModified)]
    [InlineData(ExternalChangePolicy.ReloadAll)]
    public void TmcArtifactNeverRequiresSynchronization(
        ExternalChangePolicy policy)
    {
        SynchronizationScope actual =
            XaeSession.SelectSynchronizationScope(
                new[]
                {
                    new ProjectFileChange(
                        @"C:\fixture\Machine.tmc",
                        ProjectFileChangeKind.Modified,
                        ProjectGraphFileRole.GeneratedArtifact),
                },
                policy,
                force: false,
                baselineMissing: false);

        Assert.Equal(SynchronizationScope.None, actual);
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "TwinCatGatewayTests",
                Guid.NewGuid().ToString("N"));
            string solutionRoot = Path.Combine(Root, "solution");
            ProjectRoot = Path.Combine(Root, "referenced-project");
            Directory.CreateDirectory(solutionRoot);
            Directory.CreateDirectory(ProjectRoot);
            SolutionPath = Path.Combine(solutionRoot, "Machine.sln");
            File.WriteAllText(SolutionPath, string.Empty);
            Roots = new[] { solutionRoot, ProjectRoot };
        }

        public string Root { get; }

        public string ProjectRoot { get; }

        public string SolutionPath { get; }

        public string[] Roots { get; }

        public string WriteProject(string relativePath)
        {
            string path = Path.Combine(ProjectRoot, relativePath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(path));
            File.WriteAllText(path, string.Empty);
            return Path.GetFullPath(path);
        }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
