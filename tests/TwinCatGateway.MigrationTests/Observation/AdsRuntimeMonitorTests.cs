using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TwinCAT.Ads;
using TwinCatGateway.Ads;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Desktop;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class AdsRuntimeMonitorTests
{
    [Fact]
    public async Task PublishesIndependentTargetAndPlcObservations()
    {
        using TemporaryDirectory temporary = new();
        ProfileObservationStore store = CreateStore();
        FakeProbe probe = new();
        probe.SetState(10000, AdsState.Run);
        probe.SetState(851, AdsState.Run);
        probe.SetState(852, AdsState.Exception);
        RecordingEventSink events = new();
        using AdsRuntimeMonitor monitor = CreateMonitor(
            store,
            probe,
            events);
        monitor.UpdateProject(
            temporary.WriteTwinCatProject());
        using CancellationTokenSource cancellation = new();
        Task task = monitor.RunAsync(cancellation.Token);
        try
        {
            await WaitUntilAsync(
                () =>
                {
                    ProfileObservationSnapshot snapshot =
                        monitor.Read();
                    return snapshot.Target.State
                            == TargetSystemState.Run
                        && snapshot.PlcRuntimes.Count == 2
                        && snapshot.PlcRuntimes.Any(
                            runtime =>
                                runtime.State
                                == PlcRuntimeState.Exception);
                });

            ProfileObservationSnapshot result =
                monitor.Read();
            Assert.Equal(
                TargetSystemState.Run,
                result.Target.State);
            Assert.Equal(
                PlcRuntimeState.Exception,
                result.PlcRuntimes.Single(
                    runtime => runtime.Port == 852).State);
            Assert.Equal(
                TimeSpan.FromMilliseconds(100),
                probe.LastTimeout(10000));
            int eventCount = events.Events.Count;
            await Task.Delay(150);
            Assert.Equal(eventCount, events.Events.Count);
            Assert.Contains(
                events.Events,
                    gatewayEvent =>
                    gatewayEvent.Type
                        == GatewayEventTypes
                            .PlcRuntimeStateChanged
                    && gatewayEvent.Properties["runtimeId"]
                        == "verification");
        }
        finally
        {
            cancellation.Cancel();
            await task;
        }
    }

    [Fact]
    public async Task ConfigPublishesUnavailablePlcsWithoutReadingThem()
    {
        using TemporaryDirectory temporary = new();
        ProfileObservationStore store = CreateStore();
        FakeProbe probe = new();
        probe.SetState(10000, AdsState.Reconfig);
        RecordingEventSink events = new();
        using AdsRuntimeMonitor monitor = CreateMonitor(
            store,
            probe,
            events);
        monitor.UpdateProject(
            temporary.WriteTwinCatProject());
        using CancellationTokenSource cancellation = new();
        Task task = monitor.RunAsync(cancellation.Token);
        try
        {
            await WaitUntilAsync(
                () =>
                {
                    ProfileObservationSnapshot snapshot =
                        monitor.Read();
                    return snapshot.Target.State
                            == TargetSystemState.Config
                        && snapshot.PlcRuntimes.Count == 2
                        && snapshot.PlcRuntimes.All(
                            runtime =>
                                runtime.Freshness
                                == ObservationFreshness
                                    .Unavailable);
                });

            ProfileObservationSnapshot snapshot =
                monitor.Read();
            Assert.All(
                snapshot.PlcRuntimes,
                runtime =>
                {
                    Assert.Equal(
                        ObservationFreshness.Unavailable,
                        runtime.Freshness);
                    Assert.Equal(
                        ErrorCodes.PlcStateNotObserved,
                        runtime.Error?.Code);
                });
            Assert.Equal(0, probe.ReadCount(851));
            Assert.Equal(0, probe.ReadCount(852));
        }
        finally
        {
            cancellation.Cancel();
            await task;
        }
    }

    [Fact]
    public async Task DirectAdsContinuesWhenXaeIsUnavailable()
    {
        ProfileObservationStore store = CreateStore();
        FakeProbe probe = new();
        probe.SetState(10000, AdsState.Run);
        RecordingEventSink events = new();
        using AdsRuntimeMonitor monitor = CreateMonitor(
            store,
            probe,
            events);
        monitor.MarkXaeUnavailable(
            "XAE is disconnected.");
        using CancellationTokenSource cancellation = new();
        Task task = monitor.RunAsync(cancellation.Token);
        try
        {
            await WaitUntilAsync(
                () => monitor.Read().Target.Freshness
                    == ObservationFreshness.Fresh);

            ProfileObservationSnapshot snapshot =
                monitor.Read();
            Assert.Equal(
                ObservationFreshness.Unavailable,
                snapshot.Xae?.Freshness);
            Assert.Equal(
                TargetSystemState.Run,
                snapshot.Target.State);
        }
        finally
        {
            cancellation.Cancel();
            await task;
        }
    }

    [Fact]
    public async Task InitialFailureRecoversWithoutAggregateState()
    {
        ProfileObservationStore store = CreateStore();
        FakeProbe probe = new();
        probe.SetFailure(10000);
        RecordingEventSink events = new();
        using AdsRuntimeMonitor monitor = CreateMonitor(
            store,
            probe,
            events);
        using CancellationTokenSource cancellation = new();
        Task task = monitor.RunAsync(cancellation.Token);
        try
        {
            await WaitUntilAsync(
                () => monitor.Read().Target.Freshness
                    == ObservationFreshness.Unavailable);

            probe.SetState(10000, AdsState.Run);
            await WaitUntilAsync(
                () => monitor.Read().Target.Freshness
                        == ObservationFreshness.Fresh
                    && monitor.Read().Target.State
                        == TargetSystemState.Run);

            Assert.Contains(
                events.Events,
                gatewayEvent =>
                    gatewayEvent.Type
                        == GatewayEventTypes
                            .TargetSystemStateReadFailed);
            Assert.Contains(
                events.Events,
                gatewayEvent =>
                    gatewayEvent.Type
                        == GatewayEventTypes
                            .TargetSystemStateChanged);
        }
        finally
        {
            cancellation.Cancel();
            await task;
        }
    }

    [Fact]
    public async Task AlreadyCancelledMonitorDoesNotReadOrPublish()
    {
        ProfileObservationStore store = CreateStore();
        FakeProbe probe = new();
        probe.SetState(10000, AdsState.Run);
        RecordingEventSink events = new();
        using AdsRuntimeMonitor monitor = CreateMonitor(
            store,
            probe,
            events);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await monitor.RunAsync(cancellation.Token);

        Assert.Equal(0, probe.ReadCount(10000));
        Assert.Empty(events.Events);
        Assert.Equal(
            ObservationFreshness.Unknown,
            monitor.Read().Target.Freshness);
    }

    [Fact]
    public async Task CancellationDuringReadDoesNotPublish()
    {
        ProfileObservationStore store = CreateStore();
        FakeProbe probe = new();
        using ManualResetEventSlim readEntered = new();
        using ManualResetEventSlim releaseRead = new();
        probe.SetBlockingState(
            10000,
            AdsState.Run,
            readEntered,
            releaseRead);
        RecordingEventSink events = new();
        using AdsRuntimeMonitor monitor = CreateMonitor(
            store,
            probe,
            events);
        using CancellationTokenSource cancellation = new();
        Task task = monitor.RunAsync(cancellation.Token);

        Assert.True(readEntered.Wait(TimeSpan.FromSeconds(3)));
        cancellation.Cancel();
        releaseRead.Set();
        await task;

        Assert.Empty(events.Events);
        Assert.Equal(
            ObservationFreshness.Unknown,
            monitor.Read().Target.Freshness);
    }

    [Fact]
    public async Task PublishesDivergenceAndConvergenceTransitions()
    {
        ProfileObservationStore store = CreateStore();
        FakeProbe probe = new();
        probe.SetState(10000, AdsState.Reconfig);
        RecordingEventSink events = new();
        using AdsRuntimeMonitor monitor = CreateMonitor(
            store,
            probe,
            events);
        monitor.PublishXaeObservation(
            new XaeTwinCatSystemObservation
            {
                State = TargetSystemState.Run,
                RawState = "IsTwinCATStarted=true",
                SelectedTarget = AmsNetId,
                ObservedAtUtc = DateTimeOffset.UtcNow,
                Freshness = ObservationFreshness.Fresh,
            });
        using CancellationTokenSource cancellation = new();
        Task task = monitor.RunAsync(cancellation.Token);
        try
        {
            await WaitUntilAsync(
                () => events.Events.Any(
                    gatewayEvent =>
                        gatewayEvent.Type
                            == GatewayEventTypes
                                .StateObservationsDiverged));

            probe.SetState(10000, AdsState.Run);
            await WaitUntilAsync(
                () => events.Events.Any(
                    gatewayEvent =>
                        gatewayEvent.Type
                            == GatewayEventTypes
                                .StateObservationsConverged));

            Assert.Null(monitor.Read().Divergence);
        }
        finally
        {
            cancellation.Cancel();
            await task;
        }
    }

    private static AdsRuntimeMonitor CreateMonitor(
        ProfileObservationStore store,
        IAdsStateProbe probe,
        IGatewayEventSink events)
    {
        return new AdsRuntimeMonitor(
            store,
            NullLogger<AdsRuntimeMonitor>.Instance,
            events,
            "bench",
            AmsNetId,
            pollIntervalMilliseconds: 25,
            readTimeoutMilliseconds: 100,
            tcUnitRuntimeId: "verification",
            tcUnitPort: 852,
            probe: probe);
    }

    private static ProfileObservationStore CreateStore()
    {
        return new ProfileObservationStore(
            "bench",
            AmsNetId);
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

            await Task.Delay(20);
        }

        Assert.True(condition(), "Condition was not reached.");
    }

    private sealed class FakeProbe : IAdsStateProbe
    {
        private readonly ConcurrentDictionary<
            int,
            Func<string, AdsStateReadResult>> _results =
                new();
        private readonly ConcurrentDictionary<int, int>
            _readCounts = new();
        private readonly ConcurrentDictionary<
            int,
            TimeSpan> _lastTimeouts = new();

        public AdsStateReadResult Read(
            string amsNetId,
            int port,
            TimeSpan timeout)
        {
            _readCounts.AddOrUpdate(
                port,
                1,
                (_, count) => count + 1);
            _lastTimeouts[port] = timeout;
            return _results[port](amsNetId);
        }

        public void SetState(int port, AdsState state)
        {
            _results[port] = amsNetId =>
                new AdsStateReadResult(
                    amsNetId,
                    port,
                    DateTimeOffset.UtcNow,
                    (int)state,
                    state.ToString(),
                    rawDeviceState: 2,
                    error: null,
                    failure: null);
        }

        public void SetFailure(int port)
        {
            _results[port] = amsNetId =>
                new AdsStateReadResult(
                    amsNetId,
                    port,
                    DateTimeOffset.UtcNow,
                    rawAdsState: null,
                    rawAdsStateName: null,
                    rawDeviceState: null,
                    new ObservationError
                    {
                        Code =
                            ErrorCodes.AdsStateReadFailed,
                        Message = "ADS state read failed.",
                        Retryable = true,
                    },
                    failure: null);
        }

        public void SetBlockingState(
            int port,
            AdsState state,
            ManualResetEventSlim readEntered,
            ManualResetEventSlim releaseRead)
        {
            _results[port] = amsNetId =>
            {
                readEntered.Set();
                releaseRead.Wait(TimeSpan.FromSeconds(3));
                return new AdsStateReadResult(
                    amsNetId,
                    port,
                    DateTimeOffset.UtcNow,
                    (int)state,
                    state.ToString(),
                    rawDeviceState: 2,
                    error: null,
                    failure: null);
            };
        }

        public int ReadCount(int port)
        {
            return _readCounts.TryGetValue(
                port,
                out int count)
                ? count
                : 0;
        }

        public TimeSpan LastTimeout(int port)
        {
            return _lastTimeouts[port];
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingEventSink :
        IGatewayEventSink
    {
        private long _cursor;

        public ConcurrentQueue<GatewayEvent> Events { get; } =
            new();

        public long Record(
            GatewayEvent gatewayEvent,
            DateTimeOffset occurredAtUtc)
        {
            gatewayEvent.Cursor =
                Interlocked.Increment(ref _cursor);
            gatewayEvent.OccurredAtUtc = occurredAtUtc;
            Events.Enqueue(gatewayEvent);

            return gatewayEvent.Cursor;
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
                      <Project Name="MachinePlc" AmsPort="851" />
                      <Project Name="Tests" AmsPort="852" />
                    </Plc>
                  </Project>
                </TcSmProject>
                """);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private const string AmsNetId = "192.168.3.31.1.1";
}
