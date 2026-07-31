using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class TargetOperationServiceTests
{
    [Fact]
    public async Task ConfigNoOpRequiresFreshDirectObservation()
    {
        Fixture fixture = new();
        fixture.Observations.Enqueue(
            fixture.Observation(TargetSystemState.Config));

        TargetConfigResult result = await fixture.ExecuteConfigAsync();

        Assert.Equal(TargetTransitionAction.NoOp, result.Action);
        Assert.Equal(TargetSystemState.Config, result.Before.State);
        Assert.Same(result.Before, result.After);
        Assert.Equal(0, fixture.CommandCalls);
        Assert.Equal(0, fixture.EvidenceCalls);
    }

    [Theory]
    [InlineData(TargetSystemState.Run)]
    [InlineData(TargetSystemState.Stop)]
    [InlineData(TargetSystemState.Exception)]
    [InlineData(TargetSystemState.Transitioning)]
    [InlineData(TargetSystemState.Unknown)]
    public async Task ConfigTransitionsFromAnyObservedState(
        TargetSystemState initialState)
    {
        Fixture fixture = new();
        fixture.Observations.Enqueue(fixture.Observation(initialState));
        fixture.Observations.Enqueue(
            fixture.Observation(TargetSystemState.Config, seconds: 2));

        TargetConfigResult result = await fixture.ExecuteConfigAsync();

        Assert.Equal(TargetTransitionAction.Config, result.Action);
        Assert.Equal(initialState, result.Before.State);
        Assert.Equal(TargetSystemState.Config, result.After.State);
        Assert.Equal(1, fixture.CommandCalls);
        Assert.Equal(1, fixture.EvidenceCalls);
        Assert.Equal(5, result.Before.RawAdsState);
        Assert.Equal(2, result.Before.RawDeviceState);
        Assert.Equal(fixture.Profile.Target!.AmsNetId, result.Target.AmsNetId);
    }

    [Fact]
    public async Task FailedInitialAdsReadDoesNotBlockConfig()
    {
        Fixture fixture = new();
        TargetSystemObservation unavailable =
            fixture.Observation(TargetSystemState.Unknown);
        unavailable.Freshness = ObservationFreshness.Unavailable;
        unavailable.Error = new ObservationError
        {
            Code = ErrorCodes.AdsStateReadFailed,
            Message = "read failed",
            Retryable = true,
        };
        fixture.Observations.Enqueue(unavailable);
        fixture.Observations.Enqueue(
            fixture.Observation(TargetSystemState.Config, seconds: 2));

        TargetConfigResult result = await fixture.ExecuteConfigAsync();

        Assert.Equal(TargetTransitionAction.Config, result.Action);
        Assert.Equal(ErrorCodes.AdsStateReadFailed, result.Before.Error!.Code);
        Assert.Equal(1, fixture.CommandCalls);
    }

    [Fact]
    public async Task FaultEvidenceFailureIsDiagnosticNotGate()
    {
        Fixture fixture = new()
        {
            EvidenceFailure = new InvalidOperationException("Error List unavailable"),
        };
        fixture.Observations.Enqueue(
            fixture.Observation(TargetSystemState.Exception));
        fixture.Observations.Enqueue(
            fixture.Observation(TargetSystemState.Config, seconds: 2));

        TargetConfigResult result = await fixture.ExecuteConfigAsync();

        OperationDiagnostic diagnostic = Assert.Single(
            result.FaultSnapshot!.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("Error List unavailable", diagnostic.Message);
        Assert.Equal(1, fixture.CommandCalls);
    }

    [Fact]
    public async Task MissingPostconditionPreservesSideEffectEvidence()
    {
        Fixture fixture = new();
        fixture.Observations.Enqueue(
            fixture.Observation(TargetSystemState.Run));
        fixture.Observations.Enqueue(
            fixture.Observation(TargetSystemState.Run, seconds: 2));
        fixture.AdvanceClockAfterCommand = TimeSpan.FromSeconds(1);

        GatewayOperationException exception = await Assert.ThrowsAsync<
            GatewayOperationException>(fixture.ExecuteConfigAsync);

        Assert.Equal(
            ErrorCodes.TargetConfigPostconditionMissing,
            exception.Code);
        Assert.Equal(GatewayComponent.Target, exception.Component);
        Assert.True(exception.SideEffectsStarted);
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            GatewayConfiguration configuration = new()
            {
                Profiles = new List<ProjectProfile>
                {
                    new()
                    {
                        Name = "fixture",
                        Xae = new XaeProfileConfiguration
                        {
                            Solution = @"C:\fixture\Machine.sln",
                        },
                        Target = new TargetProfileConfiguration
                        {
                            Name = "bench",
                            AmsNetId = "192.168.3.31.1.1",
                            Capabilities = new TargetCapabilitiesConfiguration
                            {
                                Config = true,
                            },
                        },
                    },
                },
            };
            ProfileResolver profiles = new(configuration);
            Profile = profiles.Resolve("fixture");
            CapabilityEvaluator evaluator = new(configuration);
            Guard = new OperationCapabilityGuard(
                evaluator,
                Profile,
                CapabilityKey.TargetConfig);
            Service = new TargetOperationService(Clock);
        }

        public Queue<TargetSystemObservation> Observations { get; } = new();

        public AdvancingClock Clock { get; } = new();

        public ResolvedProfile Profile { get; }

        public OperationCapabilityGuard Guard { get; }

        public TargetOperationService Service { get; }

        public Exception? EvidenceFailure { get; set; }

        public int EvidenceCalls { get; private set; }

        public int CommandCalls { get; private set; }

        public TimeSpan AdvanceClockAfterCommand { get; set; }

        public Task<TargetConfigResult> ExecuteConfigAsync()
        {
            return Service.ExecuteConfigAsync(
                "op-s7-config",
                Profile,
                Guard,
                ReadObservationAsync,
                ReadEvidenceAsync,
                ExecuteCommandAsync,
                TimeSpan.FromSeconds(4),
                CancellationToken.None);
        }

        public TargetSystemObservation Observation(
            TargetSystemState state,
            int seconds = 0)
        {
            return new TargetSystemObservation
            {
                Profile = Profile.Name,
                AmsNetId = Profile.Target!.AmsNetId,
                Port = 10000,
                RawAdsState = 5,
                RawAdsStateName = state.ToString(),
                RawDeviceState = 2,
                State = state,
                ObservedAtUtc = Clock.Value.AddSeconds(seconds),
                Freshness = ObservationFreshness.Fresh,
            };
        }

        private Task<TargetSystemObservation> ReadObservationAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            _ = timeout;
            cancellationToken.ThrowIfCancellationRequested();
            if (Observations.Count > 1)
            {
                return Task.FromResult(Observations.Dequeue());
            }

            return Task.FromResult(Observations.Peek());
        }

        private Task<XaeMessagesResult?> ReadEvidenceAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            _ = timeout;
            cancellationToken.ThrowIfCancellationRequested();
            EvidenceCalls++;
            if (EvidenceFailure is not null)
            {
                return Task.FromException<XaeMessagesResult?>(
                    EvidenceFailure);
            }

            return Task.FromResult<XaeMessagesResult?>(new XaeMessagesResult
            {
                Solution = Profile.Xae.Solution,
                ReadAtUtc = Clock.UtcNow,
            });
        }

        private Task ExecuteCommandAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            _ = timeout;
            cancellationToken.ThrowIfCancellationRequested();
            CommandCalls++;
            Clock.AdvanceOnRead = AdvanceClockAfterCommand;
            return Task.CompletedTask;
        }
    }

    private sealed class AdvancingClock : IClock
    {
        private DateTimeOffset _value =
            new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);

        public TimeSpan AdvanceOnRead { get; set; }

        public DateTimeOffset Value => _value;

        public DateTimeOffset UtcNow
        {
            get
            {
                DateTimeOffset value = _value;
                _value = _value.Add(AdvanceOnRead);
                return value;
            }
        }
    }
}
