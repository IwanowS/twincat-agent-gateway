using System;
using System.IO;
using System.Linq;
using System.Threading;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class ProjectFileFingerprintScannerTests
{
    [Fact]
    public void CaptureIncludesSupportedSourcesRecursively()
    {
        using TemporaryProject project = new();
        project.Write("POUs\\MAIN.TcPOU", "pou");
        project.Write("GVLs\\Global.TcGVL", "gvl");
        project.Write("Types\\Axis.TcDUT", "dut");
        project.Write("Project.plcproj", "ignored");
        project.Write("Notes.txt", "ignored");

        ProjectFileFingerprintSnapshot snapshot =
            ProjectFileFingerprintScanner.Capture(
                project.SolutionPath,
                CancellationToken.None);

        Assert.Equal(3, snapshot.Files.Count);
        Assert.All(
            snapshot.Files,
            file => Assert.True(
                ProjectFileFingerprintScanner.IsSupportedPath(
                    file.Path)));
        Assert.All(
            snapshot.Files,
            file => Assert.Equal(64, file.Sha256.Length));
    }

    [Fact]
    public void CompareDetectsSameLengthContentWithRestoredTimestamp()
    {
        using TemporaryProject project = new();
        string path = project.Write("POUs\\MAIN.TcPOU", "first");
        ProjectFileFingerprintSnapshot baseline =
            ProjectFileFingerprintScanner.Capture(
                project.SolutionPath,
                CancellationToken.None);
        DateTime timestamp = File.GetLastWriteTimeUtc(path);

        File.WriteAllText(path, "other");
        File.SetLastWriteTimeUtc(path, timestamp);
        ProjectFileFingerprintSnapshot current =
            ProjectFileFingerprintScanner.Capture(
                project.SolutionPath,
                CancellationToken.None);

        ProjectFileChange change = Assert.Single(
            ProjectFileFingerprintScanner.Compare(
                baseline,
                current));
        Assert.Equal(ProjectFileChangeKind.Modified, change.Kind);
        Assert.Equal(Path.GetFullPath(path), change.Path);
    }

    [Fact]
    public void CaptureIncludesReferencedProjectRootOutsideSolution()
    {
        using TemporaryProject solution = new();
        using TemporaryProject referencedProject = new();
        string local = solution.Write(
            "Local\\MAIN.TcPOU",
            "local");
        string referenced = referencedProject.Write(
            "External\\MAIN.TcPOU",
            "external");

        ProjectFileFingerprintSnapshot snapshot =
            ProjectFileFingerprintScanner.Capture(
                solution.SolutionPath,
                new[] { referencedProject.Root },
                CancellationToken.None);

        Assert.Contains(
            snapshot.Files,
            file => file.Path == Path.GetFullPath(local));
        Assert.Contains(
            snapshot.Files,
            file => file.Path == Path.GetFullPath(referenced));
    }

    [Fact]
    public void CompareDetectsAddedAndDeletedSources()
    {
        using TemporaryProject project = new();
        string deleted = project.Write("POUs\\Old.TcPOU", "old");
        ProjectFileFingerprintSnapshot baseline =
            ProjectFileFingerprintScanner.Capture(
                project.SolutionPath,
                CancellationToken.None);

        File.Delete(deleted);
        string added = project.Write("POUs\\New.TcPOU", "new");
        ProjectFileFingerprintSnapshot current =
            ProjectFileFingerprintScanner.Capture(
                project.SolutionPath,
                CancellationToken.None);

        ProjectFileChange[] changes =
            ProjectFileFingerprintScanner.Compare(
                baseline,
                current)
            .ToArray();
        Assert.Equal(2, changes.Length);
        Assert.Contains(
            changes,
            change => change.Kind == ProjectFileChangeKind.Added
                && change.Path == Path.GetFullPath(added));
        Assert.Contains(
            changes,
            change => change.Kind == ProjectFileChangeKind.Deleted
                && change.Path == Path.GetFullPath(deleted));
    }

    [Fact]
    public void CaptureHonorsCancellation()
    {
        using TemporaryProject project = new();
        project.Write("POUs\\MAIN.TcPOU", "pou");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => ProjectFileFingerprintScanner.Capture(
                project.SolutionPath,
                cancellation.Token));
    }

    private sealed class TemporaryProject : IDisposable
    {
        public TemporaryProject()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "TwinCatGatewayTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            SolutionPath = Path.Combine(Root, "Fixture.sln");
            File.WriteAllText(SolutionPath, string.Empty);
        }

        public string Root { get; }

        public string SolutionPath { get; }

        public string Write(
            string relativePath,
            string content)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
