using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class SourceManifestResourceReaderTests
{
    [Fact]
    public void CompactManifestIsAtomicCamelCaseJson()
    {
        using ReaderFixture fixture = new();
        SourceManifestResourceReader reader = fixture.CreateReader();

        ResourceContent resource = reader.ReadManifest(
            "bench",
            64 * 1024,
            offset: 0);

        using JsonDocument json = JsonDocument.Parse(resource.Content);
        Assert.Equal(
            "bench",
            json.RootElement.GetProperty("profile").GetString());
        Assert.Equal(
            "confirmed",
            json.RootElement
                .GetProperty("discoveryState")
                .GetString());
        Assert.Equal("application/json", resource.ContentType);
        Assert.False(resource.Truncated);
        Assert.Null(resource.NextOffset);
    }

    [Fact]
    public void FilesPagesContainOnlyCompleteJsonEntries()
    {
        using ReaderFixture fixture = new();
        SourceManifestResourceReader reader = fixture.CreateReader();
        ResourceContent all = reader.ReadFiles(
            "bench",
            64 * 1024,
            offset: 0);
        using JsonDocument allJson = JsonDocument.Parse(all.Content);
        int firstLength = allJson.RootElement[0]
            .GetRawText()
            .Length;

        ResourceContent first = reader.ReadFiles(
            "bench",
            firstLength + 2,
            offset: 0);
        using JsonDocument firstJson = JsonDocument.Parse(first.Content);
        Assert.Equal(1, firstJson.RootElement.GetArrayLength());
        Assert.True(first.Truncated);
        Assert.Equal(1, first.NextOffset);

        ResourceContent remainder = reader.ReadFiles(
            "bench",
            64 * 1024,
            first.NextOffset!.Value);
        using JsonDocument remainderJson =
            JsonDocument.Parse(remainder.Content);
        Assert.Equal(
            allJson.RootElement.GetArrayLength() - 1,
            remainderJson.RootElement.GetArrayLength());
        Assert.False(remainder.Truncated);
        Assert.Null(remainder.NextOffset);
    }

    [Fact]
    public void FilesEndOffsetReturnsEmptyArray()
    {
        using ReaderFixture fixture = new();
        SourceManifestResourceReader reader = fixture.CreateReader();
        int fileCount = fixture.Store.ReadFiles().Count;

        ResourceContent resource = reader.ReadFiles(
            "bench",
            64,
            fileCount);

        Assert.Equal("[]", resource.Content);
        Assert.False(resource.Truncated);
        Assert.Null(resource.NextOffset);
    }

    [Fact]
    public void TooSmallPageAndInvalidOffsetFail()
    {
        using ReaderFixture fixture = new();
        SourceManifestResourceReader reader = fixture.CreateReader();

        Assert.Throws<ArgumentException>(
            () => reader.ReadFiles("bench", 2, offset: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => reader.ReadFiles(
                "bench",
                64 * 1024,
                fixture.Store.ReadFiles().Count + 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => reader.ReadManifest(
                "bench",
                64 * 1024,
                offset: 1));
    }

    [Fact]
    public void ProfileMismatchFailsWithoutSideEffects()
    {
        using ReaderFixture fixture = new();
        SourceManifestResourceReader reader = fixture.CreateReader();

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => reader.ReadManifest(
                    "other",
                    64 * 1024,
                    offset: 0));

        Assert.Equal(
            ErrorCodes.XaeSolutionMismatch,
            exception.Code);
        Assert.False(exception.SideEffectsStarted);
        Assert.Equal("other", exception.Expected?.Profile);
        Assert.Equal("bench", exception.Observed?.Profile);
    }

    [Fact]
    public void StaleAndIncompleteStatesRemainReadable()
    {
        using ReaderFixture fixture = new();
        SourceManifestResourceReader reader = fixture.CreateReader();
        fixture.Store.MarkStale(
            ErrorCodes.ProjectGraphInvalid,
            "Graph changed.");

        ResourceContent stale = reader.ReadManifest(
            "bench",
            64 * 1024,
            offset: 0);
        using JsonDocument staleJson = JsonDocument.Parse(stale.Content);
        Assert.Equal(
            "stale",
            staleJson.RootElement
                .GetProperty("discoveryState")
                .GetString());

        fixture.MakeIncomplete();
        ResourceContent incomplete = reader.ReadManifest(
            "bench",
            64 * 1024,
            offset: 0);
        using JsonDocument incompleteJson =
            JsonDocument.Parse(incomplete.Content);
        Assert.Equal(
            "incomplete",
            incompleteJson.RootElement
                .GetProperty("discoveryState")
                .GetString());
    }

    private sealed class ReaderFixture : IDisposable
    {
        public ReaderFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "TwinCatGatewayTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            SolutionPath = Write("Machine.sln", string.Empty);
            Write("Plc\\MAIN.TcPOU", "main");
            Store = new SourceManifestStore("bench", SolutionPath);
            Refresh("<Compile Include=\"MAIN.TcPOU\" />");
        }

        public string Root { get; }

        public string SolutionPath { get; }

        public SourceManifestStore Store { get; }

        public SourceManifestResourceReader CreateReader()
        {
            return new SourceManifestResourceReader(Store);
        }

        public void MakeIncomplete()
        {
            Refresh("<Compile Include=\"Missing.TcPOU\" />");
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private void Refresh(string item)
        {
            string plcProject = Write(
                "Plc\\Machine.plcproj",
                "<Project><ItemGroup>"
                + item
                + "</ItemGroup></Project>");
            string twinCatProject = Write(
                "Machine.tsproj",
                "<TcSmProject><Project Name=\"Machine\" "
                + "PrjFilePath=\"Plc\\Machine.plcproj\" />"
                + "</TcSmProject>");
            TwinCatProjectGraphSnapshot graph =
                TwinCatProjectGraphResolver.Resolve(
                    SolutionPath,
                    twinCatProject,
                    CancellationToken.None);
            Assert.Equal(
                Path.GetFullPath(plcProject),
                graph.Entries.Single(entry =>
                    entry.Role == ProjectGraphFileRole.PlcProject)
                    .Path);
            Store.Refresh(graph);
        }

        private string Write(string relativePath, string content)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }
    }
}
