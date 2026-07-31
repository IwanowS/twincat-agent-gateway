using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class TwinCatProjectGraphResolverTests
{
    private static readonly string[] ExpectedOrderedProjects =
        { "First", "Second" };

    [Fact]
    public void ResolvePreservesProjectAssociationAndExternalPaths()
    {
        using TemporaryDirectory solution = new();
        using TemporaryDirectory external = new();
        string solutionPath = solution.Write("Machine.sln", string.Empty);
        string sourcePath = external.Write(
            "Plc\\POUs\\MAIN.TcPOU",
            "pou");
        string projectPath = external.Write(
            "Plc\\Machine.plcproj",
            "<Project><ItemGroup>"
            + "<Compile Include=\"POUs\\MAIN.TcPOU\" />"
            + "</ItemGroup></Project>");
        string twinCatProjectPath = external.Write(
            "Machine.tsproj",
            "<TcSmProject><Project Name=\"Motion\" "
            + "PrjFilePath=\"Plc\\Machine.plcproj\" />"
            + "</TcSmProject>");

        TwinCatProjectGraphSnapshot graph =
            TwinCatProjectGraphResolver.Resolve(
                solutionPath,
                twinCatProjectPath,
                CancellationToken.None);

        Assert.True(graph.IsComplete);
        TwinCatProjectGraphEntry source = Assert.Single(
            graph.Entries,
            entry => entry.Kind == SourceEntryKind.Editable);
        Assert.Equal(Path.GetFullPath(sourcePath), source.Path);
        Assert.Equal("Motion", source.Project);
        Assert.Equal(Path.GetFullPath(projectPath), source.ProjectFile);
        Assert.True(source.Exists);
        Assert.True(source.OutsideSolutionDirectory);
    }

    [Fact]
    public void ResolveIncludesMissingRequiredAndGeneratedEntries()
    {
        using TemporaryDirectory project = new();
        string solutionPath = project.Write("Machine.sln", string.Empty);
        string plcProjectPath = project.Write(
            "Plc\\Machine.plcproj",
            "<Project><ItemGroup>"
            + "<Compile Include=\"Missing.TcPOU\" />"
            + "<None Include=\"Machine.tmc\" />"
            + "</ItemGroup></Project>");
        string twinCatProjectPath = project.Write(
            "Machine.tsproj",
            "<TcSmProject><Project Name=\"Machine\" "
            + "PrjFilePath=\"Plc\\Machine.plcproj\" "
            + "TmcFilePath=\"Plc\\Machine.tmc\" />"
            + "</TcSmProject>");

        TwinCatProjectGraphSnapshot graph =
            TwinCatProjectGraphResolver.Resolve(
                solutionPath,
                twinCatProjectPath,
                CancellationToken.None);

        Assert.False(graph.IsComplete);
        Assert.Contains(
            graph.Entries,
            entry => entry.Path == Path.Combine(
                    Path.GetDirectoryName(plcProjectPath)!,
                    "Missing.TcPOU")
                && entry.Kind == SourceEntryKind.Editable
                && !entry.Exists);
        Assert.Contains(
            graph.Entries,
            entry => entry.Path == Path.Combine(
                    Path.GetDirectoryName(plcProjectPath)!,
                    "Machine.tmc")
                && entry.Kind == SourceEntryKind.Generated
                && !entry.Exists);
    }

    [Fact]
    public void ResolveIncludesMissingPlcProjectWithoutOpeningIt()
    {
        using TemporaryDirectory project = new();
        string solutionPath = project.Write("Machine.sln", string.Empty);
        string twinCatProjectPath = project.Write(
            "Machine.tsproj",
            "<TcSmProject><Project Name=\"Missing\" "
            + "PrjFilePath=\"Plc\\Missing.plcproj\" "
            + "TmcFilePath=\"Plc\\Missing.tmc\" />"
            + "</TcSmProject>");

        TwinCatProjectGraphSnapshot graph =
            TwinCatProjectGraphResolver.Resolve(
                solutionPath,
                twinCatProjectPath,
                CancellationToken.None);

        Assert.False(graph.IsComplete);
        Assert.Contains(
            graph.Entries,
            entry => entry.Role == ProjectGraphFileRole.PlcProject
                && entry.Project == "Missing"
                && !entry.Exists);
        Assert.Contains(
            graph.Entries,
            entry => entry.Kind == SourceEntryKind.Generated
                && !entry.Exists);
    }

    [Fact]
    public void ResolveMarksUnsupportedItemsAndDoesNotScanNeighbors()
    {
        using TemporaryDirectory project = new();
        string solutionPath = project.Write("Machine.sln", string.Empty);
        string unused = project.Write(
            "Plc\\Unused.TcPOU",
            "not referenced");
        string unsupported = project.Write(
            "Plc\\Library.library",
            "library");
        project.Write(
            "Plc\\Machine.plcproj",
            "<Project><ItemGroup>"
            + "<Compile Include=\"Library.library\" />"
            + "</ItemGroup></Project>");
        string twinCatProjectPath = project.Write(
            "Machine.tsproj",
            "<TcSmProject><Project Name=\"Machine\" "
            + "PrjFilePath=\"Plc\\Machine.plcproj\" />"
            + "</TcSmProject>");

        TwinCatProjectGraphSnapshot graph =
            TwinCatProjectGraphResolver.Resolve(
                solutionPath,
                twinCatProjectPath,
                CancellationToken.None);

        Assert.True(graph.IsComplete);
        Assert.Contains(
            graph.Entries,
            entry => entry.Path == Path.GetFullPath(unsupported)
                && entry.Kind == SourceEntryKind.Unsupported);
        Assert.DoesNotContain(
            graph.Entries,
            entry => entry.Path == Path.GetFullPath(unused));
    }

    [Fact]
    public void ResolveSupportsMultiplePlcProjectsDeterministically()
    {
        using TemporaryDirectory project = new();
        string solutionPath = project.Write("Machine.sln", string.Empty);
        project.Write(
            "Second\\Second.plcproj",
            "<Project><ItemGroup>"
            + "<Compile Include=\"MAIN.TcPOU\" />"
            + "</ItemGroup></Project>");
        project.Write("Second\\MAIN.TcPOU", "second");
        project.Write(
            "First\\First.plcproj",
            "<Project><ItemGroup>"
            + "<Compile Include=\"MAIN.TcPOU\" />"
            + "</ItemGroup></Project>");
        project.Write("First\\MAIN.TcPOU", "first");
        string twinCatProjectPath = project.Write(
            "Machine.tsproj",
            "<TcSmProject><Plc>"
            + "<Project Name=\"Second\" "
            + "PrjFilePath=\"Second\\Second.plcproj\" />"
            + "<Project Name=\"First\" "
            + "PrjFilePath=\"First\\First.plcproj\" />"
            + "</Plc></TcSmProject>");

        TwinCatProjectGraphSnapshot graph =
            TwinCatProjectGraphResolver.Resolve(
                solutionPath,
                twinCatProjectPath,
                CancellationToken.None);

        Assert.True(graph.IsComplete);
        Assert.Equal(
            ExpectedOrderedProjects,
            graph.Entries
                .Where(entry => entry.Kind == SourceEntryKind.Editable)
                .Select(entry => entry.Project)
                .ToArray());
    }

    [Fact]
    public void ResolveRejectsMalformedXml()
    {
        using TemporaryDirectory project = new();
        string solutionPath = project.Write("Machine.sln", string.Empty);
        string twinCatProjectPath = project.Write(
            "Machine.tsproj",
            "<TcSmProject>");

        Assert.Throws<XmlException>(
            () => TwinCatProjectGraphResolver.Resolve(
                solutionPath,
                twinCatProjectPath,
                CancellationToken.None));
    }

    [Fact]
    public void ResolveHonorsCancellation()
    {
        using TemporaryDirectory project = new();
        string solutionPath = project.Write("Machine.sln", string.Empty);
        string twinCatProjectPath = project.Write(
            "Machine.tsproj",
            "<TcSmProject />");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => TwinCatProjectGraphResolver.Resolve(
                solutionPath,
                twinCatProjectPath,
                cancellation.Token));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "TwinCatGatewayTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Write(string relativePath, string content)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
