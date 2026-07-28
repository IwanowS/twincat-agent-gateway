using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Xae;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class XaeProjectGraphChangeScopeTests
{
    [Fact]
    public async Task SettleWaitsForQuietAndCapturesGraphChange()
    {
        using TemporaryProject project = new();
        ProjectFileFingerprintSnapshot baseline =
            project.Capture();
        using XaeProjectGraphChangeScope scope = new(
            project.SolutionPath,
            project.TwinCatProjectPath,
            baseline,
            TimeSpan.FromMilliseconds(100));
        project.WriteSource("second");

        Stopwatch stopwatch = Stopwatch.StartNew();
        XaeAcceptedProjectGraphChanges accepted =
            await scope.SettleAsync(
                _ => project.Capture(),
                TimeSpan.FromSeconds(2),
                CancellationToken.None);

        Assert.True(stopwatch.ElapsedMilliseconds >= 70);
        ProjectFileChange change = Assert.Single(
            accepted.Changes);
        Assert.Equal(
            Path.GetFullPath(project.SourcePath),
            change.Path);
        Assert.Equal(
            ProjectFileChangeKind.Modified,
            change.Kind);
        Assert.Equal(
            ProjectGraphFileRole.PlcSource,
            change.Role);
    }

    [Fact]
    public async Task LaterFileEventRestartsQuietPeriod()
    {
        using TemporaryProject project = new();
        ProjectFileFingerprintSnapshot baseline =
            project.Capture();
        using XaeProjectGraphChangeScope scope = new(
            project.SolutionPath,
            project.TwinCatProjectPath,
            baseline,
            TimeSpan.FromMilliseconds(120));
        Stopwatch stopwatch = Stopwatch.StartNew();

        Task writer = Task.Run(
            async () =>
            {
                await Task.Delay(50);
                project.WriteSource("second");
                await Task.Delay(80);
                project.WriteSource("third");
            });
        XaeAcceptedProjectGraphChanges accepted =
            await scope.SettleAsync(
                _ => project.Capture(),
                TimeSpan.FromSeconds(3),
                CancellationToken.None);
        await writer;

        Assert.True(stopwatch.ElapsedMilliseconds >= 210);
        Assert.Single(accepted.Changes);
        Assert.Equal("third", File.ReadAllText(project.SourcePath));
    }

    [Fact]
    public async Task CaptureIoFailureIsRetriedAfterAnotherQuietPeriod()
    {
        using TemporaryProject project = new();
        ProjectFileFingerprintSnapshot baseline =
            project.Capture();
        using XaeProjectGraphChangeScope scope = new(
            project.SolutionPath,
            project.TwinCatProjectPath,
            baseline,
            TimeSpan.FromMilliseconds(50));
        int attempts = 0;

        XaeAcceptedProjectGraphChanges accepted =
            await scope.SettleAsync(
                _ =>
                {
                    if (Interlocked.Increment(ref attempts) == 1)
                    {
                        throw new IOException("File is still changing.");
                    }

                    return project.Capture();
                },
                TimeSpan.FromSeconds(2),
                CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Empty(accepted.Changes);
        Assert.True(accepted.WatcherEventCount >= 1);
    }

    [Fact]
    public async Task WatcherOverflowIsReportedWithoutSkippingFinalScan()
    {
        using TemporaryProject project = new();
        ProjectFileFingerprintSnapshot baseline =
            project.Capture();
        using XaeProjectGraphChangeScope scope = new(
            project.SolutionPath,
            project.TwinCatProjectPath,
            baseline,
            TimeSpan.FromMilliseconds(50));
        project.WriteSource("after overflow");
        scope.NotifyWatcherOverflow();

        XaeAcceptedProjectGraphChanges accepted =
            await scope.SettleAsync(
                _ => project.Capture(),
                TimeSpan.FromSeconds(2),
                CancellationToken.None);

        Assert.True(accepted.WatcherOverflow);
        Assert.True(accepted.WatcherEventCount >= 1);
        Assert.Single(accepted.Changes);
    }

    [Fact]
    public async Task SettleFailsWhenQuietPeriodExceedsDeadline()
    {
        using TemporaryProject project = new();
        ProjectFileFingerprintSnapshot baseline =
            project.Capture();
        using XaeProjectGraphChangeScope scope = new(
            project.SolutionPath,
            project.TwinCatProjectPath,
            baseline,
            TimeSpan.FromMilliseconds(500));

        GatewayOperationException exception =
            await Assert.ThrowsAsync<GatewayOperationException>(
                () => scope.SettleAsync(
                    _ => project.Capture(),
                    TimeSpan.FromMilliseconds(50),
                    CancellationToken.None));

        Assert.Equal(ErrorCodes.OperationTimeout, exception.Code);
        Assert.Equal("xae.workspace.settle", exception.Stage);
    }

    private sealed class TemporaryProject : IDisposable
    {
        public TemporaryProject()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "TwinCatGatewayProjectGraphScopeTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            SolutionPath = Path.Combine(Root, "Fixture.sln");
            TwinCatProjectPath =
                Path.Combine(Root, "Fixture.tsproj");
            PlcProjectPath =
                Path.Combine(Root, "Plc", "Plc.plcproj");
            SourcePath =
                Path.Combine(Root, "Plc", "MAIN.TcPOU");
            Directory.CreateDirectory(
                Path.GetDirectoryName(PlcProjectPath)!);
            File.WriteAllText(SolutionPath, string.Empty);
            File.WriteAllText(
                TwinCatProjectPath,
                "<Project><Plc><Project "
                    + "PrjFilePath=\"Plc\\Plc.plcproj\" />"
                    + "</Plc></Project>");
            File.WriteAllText(
                PlcProjectPath,
                "<Project><ItemGroup>"
                    + "<Compile Include=\"MAIN.TcPOU\" />"
                    + "</ItemGroup></Project>");
            WriteSource("first");
        }

        public string Root { get; }

        public string SolutionPath { get; }

        public string TwinCatProjectPath { get; }

        public string PlcProjectPath { get; }

        public string SourcePath { get; }

        public void WriteSource(string content)
        {
            File.WriteAllText(SourcePath, content);
        }

        public ProjectFileFingerprintSnapshot Capture()
        {
            return ProjectFileFingerprintScanner.CaptureProjectGraph(
                SolutionPath,
                TwinCatProjectPath,
                CancellationToken.None);
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
