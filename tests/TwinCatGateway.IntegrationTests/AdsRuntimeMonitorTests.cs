using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Ads;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Desktop;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class AdsRuntimeMonitorTests
{
    [Fact]
    public async Task PollsSystemAndPlcsAndDeduplicatesTransitions()
    {
        using TemporaryDirectory temporary = new();
        string project = temporary.WriteTwinCatProject();
        GatewayStatusSnapshotStore status = new(
            GatewayStatusSnapshotStore.CreateInitial("test"));
        GatewayEventJournal events = new(status);
        FakeProbe probe = new();
        probe.SetMode(10000, RuntimeMode.Run);
        probe.SetMode(851, RuntimeMode.Run);
        probe.SetMode(852, RuntimeMode.Exception);
        using AdsRuntimeMonitor monitor = new(
            status,
            new StructuredFileLogger(temporary.Path),
            events,
            CreateConfiguration(),
            probe);
        monitor.UpdateTarget(
            "192.168.3.31.1.1",
            project);
        using CancellationTokenSource cancellation = new();
        Task monitorTask = monitor.RunAsync(cancellation.Token);
        try
        {
            await WaitUntilAsync(
                () =>
                    status.Read().TwinCat.Mode
                        == RuntimeMode.Exception);

            GatewayStatusResult faulted = status.Read();
            Assert.Equal(
                RuntimeMode.Run,
                faulted.TwinCat.SystemMode);
            Assert.Equal(
                "PLC_RUNTIME_EXCEPTION",
                faulted.TwinCat.Alert?.Code);
            Assert.Equal(
                "PlcProject2",
                faulted.TwinCat.Alert?.RuntimeName);
            Assert.Equal(
                852,
                faulted.TwinCat.Alert?.AdsPort);
            Assert.True(
                faulted.TwinCat.Alert?.EventCursor > 0);
            Assert.Equal(
                2,
                monitor.GetPlcDiagnostics().Count);
            long faultCursor =
                faulted.TwinCat.Alert!.EventCursor;
            await Task.Delay(300);
            Assert.Equal(
                faultCursor,
                status.Read().TwinCat.Alert?.EventCursor);

            probe.SetMode(852, RuntimeMode.Run);
            await WaitUntilAsync(
                () =>
                    status.Read().TwinCat.Mode
                        == RuntimeMode.Run
                    && status.Read().TwinCat.Alert is null);
            long recoveredCursor =
                status.Read().LatestEventCursor;
            await Task.Delay(300);

            Assert.Equal(
                recoveredCursor,
                status.Read().LatestEventCursor);
            GatewayEventPage page = events.ReadAfter(
                eventStreamId: null,
                afterCursor: 0,
                maximumCount: 20);
            Assert.Contains(
                page.Events,
                gatewayEvent =>
                    gatewayEvent.Severity
                        == DiagnosticSeverity.Error
                    && gatewayEvent.Properties[
                        "runtimeName"]
                        == "PlcProject2");
        }
        finally
        {
            cancellation.Cancel();
            await monitorTask;
        }
    }

    [Fact]
    public async Task ReportsDisconnectAndRecoversWithoutPollingPlcs()
    {
        using TemporaryDirectory temporary = new();
        GatewayStatusSnapshotStore status = new(
            GatewayStatusSnapshotStore.CreateInitial("test"));
        GatewayEventJournal events = new(status);
        FakeProbe probe = new();
        probe.SetFailure(
            10000,
            "TargetUnreachable");
        using AdsRuntimeMonitor monitor = new(
            status,
            new StructuredFileLogger(temporary.Path),
            events,
            CreateConfiguration(),
            probe);
        monitor.UpdateTarget(
            "192.168.3.31.1.1",
            temporary.WriteTwinCatProject());
        using CancellationTokenSource cancellation = new();
        Task monitorTask = monitor.RunAsync(cancellation.Token);
        try
        {
            await WaitUntilAsync(
                () =>
                    status.Read().TwinCat.Alert?.Code
                        == "RUNTIME_UNAVAILABLE");
            Assert.Equal(
                RuntimeMode.Unknown,
                status.Read().TwinCat.Mode);
            Assert.Equal(
                0,
                probe.ReadCount(851));
            Assert.Equal(
                0,
                probe.ReadCount(852));

            probe.SetMode(10000, RuntimeMode.Config);
            await WaitUntilAsync(
                () =>
                    status.Read().TwinCat.Mode
                        == RuntimeMode.Config
                    && status.Read().TwinCat.Alert is null);
        }
        finally
        {
            cancellation.Cancel();
            await monitorTask;
        }
    }

    private static RuntimeMonitoringConfiguration
        CreateConfiguration()
    {
        return new RuntimeMonitoringConfiguration
        {
            PollIntervalMilliseconds = 100,
            ReadTimeoutMilliseconds = 100,
        };
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition)
    {
        DateTimeOffset deadline =
            DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(condition(), "Condition was not reached.");
    }

    private sealed class FakeProbe :
        IAdsRuntimeStatusProbe
    {
        private readonly ConcurrentDictionary<
            int,
            Func<AdsRuntimeStatusReadResult>> _results =
                new();
        private readonly ConcurrentDictionary<int, int>
            _readCounts = new();

        public AdsRuntimeStatusReadResult Read(
            string amsNetId,
            int port,
            TimeSpan timeout)
        {
            _readCounts.AddOrUpdate(
                port,
                1,
                (_, count) => count + 1);
            AdsRuntimeStatusReadResult result =
                _results[port]();
            result.Diagnostics.AmsNetId = amsNetId;
            result.Diagnostics.Port = port;
            result.Diagnostics.ReadAtUtc =
                DateTimeOffset.UtcNow;
            return result;
        }

        public void SetMode(
            int port,
            RuntimeMode mode)
        {
            _results[port] = () =>
                new AdsRuntimeStatusReadResult(
                    new TwinCatStatus
                    {
                        Started = mode == RuntimeMode.Run
                            || mode == RuntimeMode.Exception,
                        Mode = mode,
                    },
                    new AdsRuntimeDiagnostics
                    {
                        AdsState = mode.ToString(),
                    });
        }

        public void SetFailure(
            int port,
            string errorCode)
        {
            _results[port] = () =>
                new AdsRuntimeStatusReadResult(
                    new TwinCatStatus
                    {
                        Mode = RuntimeMode.Unknown,
                    },
                    new AdsRuntimeDiagnostics
                    {
                        ErrorCode = errorCode,
                    });
        }

        public int ReadCount(int port)
        {
            return _readCounts.TryGetValue(
                port,
                out int count)
                ? count
                : 0;
        }

        public void Dispose()
        {
        }
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

        public string WriteTwinCatProject()
        {
            string path = System.IO.Path.Combine(
                Path,
                "Project.tsproj");
            File.WriteAllText(
                path,
                """
                <TcSmProject>
                  <Project>
                    <Plc>
                      <Project Name="PlcProject1" AmsPort="851" />
                      <Project Name="PlcProject2" AmsPort="852" />
                    </Plc>
                  </Project>
                </TcSmProject>
                """);
            return path;
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
