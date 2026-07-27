using System;
using System.IO;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class TcUnitReportFileTests
{
    [Fact]
    public void UnchangedReportIsNotFresh()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(
            temporary.Path,
            "tcunit.xml");
        File.WriteAllText(path, "<testsuites />");
        TcUnitReportBaseline baseline =
            TcUnitReportFile.CaptureBaseline(
                path,
                allowDeleteExistingReport: false);

        TcUnitReportSnapshot current =
            TcUnitReportFile.Capture(path);

        Assert.False(
            TcUnitReportFile.IsFresh(
                baseline,
                current));
    }

    [Fact]
    public void RewrittenSameContentIsFresh()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(
            temporary.Path,
            "tcunit.xml");
        File.WriteAllText(path, "<testsuites />");
        TcUnitReportBaseline baseline =
            TcUnitReportFile.CaptureBaseline(
                path,
                allowDeleteExistingReport: false);
        DateTime updated =
            File.GetLastWriteTimeUtc(path)
                .AddSeconds(2);
        File.SetLastWriteTimeUtc(path, updated);

        TcUnitReportSnapshot current =
            TcUnitReportFile.Capture(path);

        Assert.True(
            TcUnitReportFile.IsFresh(
                baseline,
                current));
    }

    [Fact]
    public void ContentChangeIsFreshDespitePreservedTimestamp()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(
            temporary.Path,
            "tcunit.xml");
        File.WriteAllText(path, "old");
        TcUnitReportBaseline baseline =
            TcUnitReportFile.CaptureBaseline(
                path,
                allowDeleteExistingReport: false);
        DateTime timestamp =
            File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "new");
        File.SetLastWriteTimeUtc(path, timestamp);

        TcUnitReportSnapshot current =
            TcUnitReportFile.Capture(path);

        Assert.True(
            TcUnitReportFile.IsFresh(
                baseline,
                current));
    }

    [Fact]
    public void ExplicitDeletionMakesNextReportFresh()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(
            temporary.Path,
            "tcunit.xml");
        File.WriteAllText(path, "old");

        TcUnitReportBaseline baseline =
            TcUnitReportFile.CaptureBaseline(
                path,
                allowDeleteExistingReport: true);

        Assert.True(
            baseline.ExistingReportDeleted);
        Assert.False(File.Exists(path));
        File.WriteAllText(path, "old");
        TcUnitReportSnapshot current =
            TcUnitReportFile.Capture(path);
        Assert.True(
            TcUnitReportFile.IsFresh(
                baseline,
                current));
    }

    [Fact]
    public void StableStateRequiresFingerprintAndMetadata()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(
            temporary.Path,
            "tcunit.xml");
        File.WriteAllText(path, "same");
        TcUnitReportSnapshot first =
            TcUnitReportFile.Capture(path);
        TcUnitReportSnapshot second =
            TcUnitReportFile.Capture(path);

        Assert.True(
            TcUnitReportFile.HasSameFileState(
                first,
                second));

        File.WriteAllText(path, "changed");
        TcUnitReportSnapshot changed =
            TcUnitReportFile.Capture(path);
        Assert.False(
            TcUnitReportFile.HasSameFileState(
                first,
                changed));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "TwinCatGatewayTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(
                    Path,
                    recursive: true);
            }
        }
    }
}
