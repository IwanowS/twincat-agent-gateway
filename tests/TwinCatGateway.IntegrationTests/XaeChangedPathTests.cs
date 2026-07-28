using System;
using System.IO;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Xae;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class XaeChangedPathTests
{
    [Fact]
    public void ReferencedTwinCatProjectMetadataOutsideSolutionIsAccepted()
    {
        using TemporaryWorkspace workspace = new();
        string twinCatProject = workspace.WriteProject(
            "Machine.tsproj");
        string plcProject = workspace.WriteProject(
            "Plc\\Machine.plcproj");

        Assert.Equal(
            twinCatProject,
            XaeSession.NormalizeChangedPath(
                workspace.SolutionPath,
                workspace.Roots,
                twinCatProject));
        Assert.Equal(
            plcProject,
            XaeSession.NormalizeChangedPath(
                workspace.SolutionPath,
                workspace.Roots,
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
                    workspace.Roots,
                    unrelated));

        Assert.Equal(ErrorCodes.RequestInvalid, exception.Code);
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
