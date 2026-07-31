using System;
using System.IO;
using System.Linq;
using System.Threading;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class SourceManifestStoreTests
{
    private static readonly string[] ExpectedExtensions =
        { ".TcGVL", ".TcPOU" };

    [Fact]
    public void StartsUnknownAndReturnsDefensiveSnapshots()
    {
        using ManifestFixture fixture = new();
        SourceManifestStore store = fixture.CreateStore();

        SourceManifest first = store.ReadManifest();
        first.Profile = "mutated";
        first.Roots.Add(new SourceRootEntry());

        SourceManifest second = store.ReadManifest();
        Assert.Equal("bench", second.Profile);
        Assert.Equal(SourceDiscoveryState.Unknown, second.DiscoveryState);
        Assert.Empty(second.Roots);
        Assert.Empty(store.ReadFiles());
    }

    [Fact]
    public void RefreshBuildsConfirmedDeterministicManifest()
    {
        using ManifestFixture fixture = new();
        string sourceB = fixture.Write("Plc\\Nested\\B.TcPOU", "b");
        string sourceA = fixture.Write("Plc\\A.TcGVL", "a");
        TwinCatProjectGraphSnapshot graph = fixture.Resolve(
            "<Project><ItemGroup>"
            + "<Compile Include=\"Nested\\B.TcPOU\" />"
            + "<Compile Include=\"A.TcGVL\" />"
            + "</ItemGroup></Project>");
        SourceManifestStore store = fixture.CreateStore();

        store.Refresh(graph);

        SourceManifest manifest = store.ReadManifest();
        Assert.Equal(SourceDiscoveryState.Confirmed, manifest.DiscoveryState);
        Assert.Null(manifest.Error);
        Assert.Equal(graph.Entries.Count, manifest.FileCount);
        SourceRootEntry root = Assert.Single(manifest.Roots);
        Assert.Equal(
            Path.GetDirectoryName(sourceA),
            root.Path);
        Assert.Equal("Machine", root.Project);
        Assert.Equal(
            ExpectedExtensions,
            root.Extensions);
        string[] editablePaths = store.ReadFiles()
            .Where(entry => entry.Kind == SourceEntryKind.Editable)
            .Select(entry => entry.Path)
            .ToArray();
        Assert.Equal(
            new[]
            {
                Path.GetFullPath(sourceA),
                Path.GetFullPath(sourceB),
            },
            editablePaths);
    }

    [Fact]
    public void RefreshMarksMissingRequiredSourceIncomplete()
    {
        using ManifestFixture fixture = new();
        TwinCatProjectGraphSnapshot graph = fixture.Resolve(
            "<Project><ItemGroup>"
            + "<Compile Include=\"Missing.TcPOU\" />"
            + "</ItemGroup></Project>");
        SourceManifestStore store = fixture.CreateStore();

        store.Refresh(graph);

        SourceManifest manifest = store.ReadManifest();
        Assert.Equal(
            SourceDiscoveryState.Incomplete,
            manifest.DiscoveryState);
        Assert.Equal(
            ErrorCodes.ProjectGraphInvalid,
            manifest.Error?.Code);
        Assert.Contains(
            store.ReadFiles(),
            entry => entry.Kind == SourceEntryKind.Editable
                && !entry.Exists);
    }

    [Fact]
    public void StalePreservesLastConfirmedFilesAndRefreshesAgain()
    {
        using ManifestFixture fixture = new();
        fixture.Write("Plc\\MAIN.TcPOU", "main");
        TwinCatProjectGraphSnapshot graph = fixture.Resolve(
            "<Project><ItemGroup>"
            + "<Compile Include=\"MAIN.TcPOU\" />"
            + "</ItemGroup></Project>");
        TestClock clock = new();
        SourceManifestStore store = fixture.CreateStore(clock);
        store.Refresh(graph);
        int confirmedCount = store.ReadFiles().Count;

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        store.MarkStale(
            ErrorCodes.ProjectGraphInvalid,
            "Graph changed.");

        SourceManifest stale = store.ReadManifest();
        Assert.Equal(SourceDiscoveryState.Stale, stale.DiscoveryState);
        Assert.Equal(confirmedCount, stale.FileCount);
        Assert.Equal(confirmedCount, store.ReadFiles().Count);
        Assert.Equal(clock.UtcNow, stale.ObservedAtUtc);

        store.Refresh(graph);
        Assert.Equal(
            SourceDiscoveryState.Confirmed,
            store.ReadManifest().DiscoveryState);
    }

    [Fact]
    public void UnavailableClearsFiles()
    {
        using ManifestFixture fixture = new();
        fixture.Write("Plc\\MAIN.TcPOU", "main");
        TwinCatProjectGraphSnapshot graph = fixture.Resolve(
            "<Project><ItemGroup>"
            + "<Compile Include=\"MAIN.TcPOU\" />"
            + "</ItemGroup></Project>");
        SourceManifestStore store = fixture.CreateStore();
        store.Refresh(graph);

        store.MarkUnavailable(
            ErrorCodes.XaeNotFound,
            "XAE is unavailable.");

        SourceManifest manifest = store.ReadManifest();
        Assert.Equal(
            SourceDiscoveryState.Unavailable,
            manifest.DiscoveryState);
        Assert.Empty(store.ReadFiles());
        Assert.Equal(0, manifest.FileCount);
    }

    [Fact]
    public void RefreshRejectsGraphFromAnotherSolution()
    {
        using ManifestFixture first = new();
        using ManifestFixture second = new();
        TwinCatProjectGraphSnapshot graph = second.Resolve(
            "<Project><ItemGroup /></Project>");
        SourceManifestStore store = first.CreateStore();

        Assert.Throws<ArgumentException>(() => store.Refresh(graph));
    }

    private sealed class ManifestFixture : IDisposable
    {
        public ManifestFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "TwinCatGatewayTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            SolutionPath = Write("Machine.sln", string.Empty);
        }

        public string Root { get; }

        public string SolutionPath { get; }

        public string Write(string relativePath, string content)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public TwinCatProjectGraphSnapshot Resolve(
            string plcProjectContent)
        {
            Write("Plc\\Machine.plcproj", plcProjectContent);
            string twinCatProject = Write(
                "Machine.tsproj",
                "<TcSmProject><Project Name=\"Machine\" "
                + "PrjFilePath=\"Plc\\Machine.plcproj\" />"
                + "</TcSmProject>");
            return TwinCatProjectGraphResolver.Resolve(
                SolutionPath,
                twinCatProject,
                CancellationToken.None);
        }

        public SourceManifestStore CreateStore(IClock? clock = null)
        {
            return new SourceManifestStore(
                "bench",
                SolutionPath,
                clock);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } =
            new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
    }
}
