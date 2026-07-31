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

    [Theory]
    [InlineData(TargetSystemState.Config, TargetTransitionAction.Start)]
    [InlineData(TargetSystemState.Stop, TargetTransitionAction.Start)]
    [InlineData(TargetSystemState.Run, TargetTransitionAction.Restart)]
    public async Task StartRestartUsesExplicitInitialStateSemantics(
        TargetSystemState initialState,
        TargetTransitionAction expectedAction)
    {
        Fixture fixture = new();
        fixture.Observations.Enqueue(fixture.Observation(initialState));
        fixture.Observations.Enqueue(
            fixture.Observation(TargetSystemState.Run, seconds: 2));

        TargetStartRestartResult result =
            await fixture.ExecuteStartRestartAsync();

        Assert.Equal(expectedAction, result.Action);
        Assert.Equal(initialState, result.Before.State);
        Assert.Equal(TargetSystemState.Run, result.After.State);
        Assert.Equal(1, fixture.CommandCalls);
    }

    [Theory]
    [InlineData(TargetSystemState.Exception)]
    [InlineData(TargetSystemState.Transitioning)]
    [InlineData(TargetSystemState.Unknown)]
    public async Task StartRestartRejectsUnsupportedInitialState(
        TargetSystemState initialState)
    {
        Fixture fixture = new();
        fixture.Observations.Enqueue(fixture.Observation(initialState));

        GatewayOperationException exception = await Assert.ThrowsAsync<
            GatewayOperationException>(
                fixture.ExecuteStartRestartAsync);

        Assert.Equal(ErrorCodes.TargetStartRestartFailed, exception.Code);
        Assert.False(exception.SideEffectsStarted);
        Assert.Equal(0, fixture.CommandCalls);
    }

    [Fact]
    public async Task StartRestartRequiresAvailableDirectObservation()
    {
        Fixture fixture = new();
        TargetSystemObservation unavailable =
            fixture.Observation(TargetSystemState.Unknown);
        unavailable.Freshness = ObservationFreshness.Unavailable;
        unavailable.Error = new ObservationError
        {
            Code = ErrorCodes.AdsStateReadFailed,
            Message = "route unavailable",
            Retryable = true,
        };
        fixture.Observations.Enqueue(unavailable);

        GatewayOperationException exception = await Assert.ThrowsAsync<
            GatewayOperationException>(
                fixture.ExecuteStartRestartAsync);

        Assert.Equal(ErrorCodes.TargetAdsUnavailable, exception.Code);
        Assert.Equal("route unavailable", exception.Details);
        Assert.False(exception.SideEffectsStarted);
    }

    [Fact]
    public async Task StartRestartMissingRunPreservesSideEffectEvidence()
    {
        Fixture fixture = new()
        {
            AdvanceClockAfterCommand = TimeSpan.FromSeconds(1),
        };
        fixture.Observations.Enqueue(
            fixture.Observation(TargetSystemState.Config));
        fixture.Observations.Enqueue(
            fixture.Observation(TargetSystemState.Config, seconds: 2));

        GatewayOperationException exception = await Assert.ThrowsAsync<
            GatewayOperationException>(
                fixture.ExecuteStartRestartAsync);

        Assert.Equal(
            ErrorCodes.TargetRunPostconditionMissing,
            exception.Code);
        Assert.True(exception.SideEffectsStarted);
    }

    [Fact]
    public async Task ConfigStaticCapabilityDenialPrecedesNoOp()
    {
        Fixture fixture = new(configEnabled: false);
        fixture.Observations.Enqueue(
            fixture.Observation(TargetSystemState.Config));

        GatewayOperationException exception = await Assert.ThrowsAsync<
            GatewayOperationException>(fixture.ExecuteConfigAsync);

        Assert.Equal(ErrorCodes.CapabilityDisabled, exception.Code);
        Assert.False(exception.SideEffectsStarted);
        Assert.Equal(0, fixture.CommandCalls);
    }

    [Fact]
    public async Task StartRestartOperatorLockDeniesBeforeObservation()
    {
        Fixture fixture = new(lockTargetOperations: true);
        fixture.Observations.Enqueue(
            fixture.Observation(TargetSystemState.Run));

        GatewayOperationException exception = await Assert.ThrowsAsync<
            GatewayOperationException>(
                fixture.ExecuteStartRestartAsync);

        Assert.Equal(ErrorCodes.OperatorLocked, exception.Code);
        Assert.False(exception.SideEffectsStarted);
        Assert.Equal(0, fixture.CommandCalls);
    }

    [Fact]
    public async Task StartRestartPreservesPreCommandTargetMismatch()
    {
        Fixture fixture = new()
        {
            CommandFailure = new GatewayOperationException(
                ErrorCodes.XaeTargetMismatch,
                "Selected target differs from the profile.",
                stage: "target.startRestart.command",
                component: GatewayComponent.Xae,
                sideEffectsStarted: false),
        };
        fixture.Observations.Enqueue(
            fixture.Observation(TargetSystemState.Run));

        GatewayOperationException exception = await Assert.ThrowsAsync<
            GatewayOperationException>(
                fixture.ExecuteStartRestartAsync);

        Assert.Equal(ErrorCodes.XaeTargetMismatch, exception.Code);
        Assert.Equal(GatewayComponent.Xae, exception.Component);
        Assert.False(exception.SideEffectsStarted);
    }

    [Fact]
    public async Task CancellationBeforeCommandHasNoSideEffectEvidence()
    {
        Fixture fixture = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.ExecuteStartRestartAsync(cancellation.Token));

        Assert.Equal(0, fixture.CommandCalls);
    }

    [Fact]
    public async Task CancellationDuringCommandPreservesSideEffectEvidence()
    {
        Fixture fixture = new()
        {
            CancelDuringCommand = true,
        };
        fixture.Observations.Enqueue(
            fixture.Observation(TargetSystemState.Run));

        GatewayOperationCanceledException exception =
            await Assert.ThrowsAsync<GatewayOperationCanceledException>(
                fixture.ExecuteStartRestartAsync);

        Assert.Equal(
            ErrorCodes.TargetStartRestartFailed,
            exception.Code);
        Assert.Equal("target.startRestart.command", exception.Stage);
        Assert.Equal(GatewayComponent.Target, exception.Component);
        Assert.True(exception.SideEffectsStarted);
        Assert.Equal(1, fixture.CommandCalls);
    }

    [Fact]
    public async Task CancellationAfterCommandPreservesPostconditionEvidence()
    {
        using CancellationTokenSource cancellation = new();
        Fixture fixture = new()
        {
            CancelAfterCommand = cancellation,
        };
        fixture.Observations.Enqueue(
            fixture.Observation(TargetSystemState.Config));

        GatewayOperationCanceledException exception =
            await Assert.ThrowsAsync<GatewayOperationCanceledException>(
                () => fixture.ExecuteStartRestartAsync(cancellation.Token));

        Assert.Equal(
            ErrorCodes.TargetRunPostconditionMissing,
            exception.Code);
        Assert.Equal("target.startRestart.postcondition", exception.Stage);
        Assert.True(exception.SideEffectsStarted);
        Assert.Equal(1, fixture.CommandCalls);
    }

    private sealed class Fixture
    {
        public Fixture(
            bool configEnabled = true,
            bool startRestartEnabled = true,
            bool lockTargetOperations = false)
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
                                Config = configEnabled,
                                StartRestart = startRestartEnabled,
                            },
                        },
                    },
                },
            };
            ProfileResolver profiles = new(configuration);
            Profile = profiles.Resolve("fixture");
            OperatorLockStore locks = new();
            if (lockTargetOperations)
            {
                locks.SetLocked(
                    Profile.Name,
                    OperatorLockKey.TargetConfigStartRestart,
                    locked: true);
            }

            CapabilityEvaluator evaluator = new(
                configuration,
                sessionConsent: null,
                operatorLocks: locks);
            Guard = new OperationCapabilityGuard(
                evaluator,
                Profile,
                CapabilityKey.TargetConfig);
            StartRestartGuard = new OperationCapabilityGuard(
                evaluator,
                Profile,
                CapabilityKey.TargetStartRestart);
            Service = new TargetOperationService(Clock);
        }

        public Queue<TargetSystemObservation> Observations { get; } = new();

        public AdvancingClock Clock { get; } = new();

        public ResolvedProfile Profile { get; }

        public OperationCapabilityGuard Guard { get; }

        public OperationCapabilityGuard StartRestartGuard { get; }

        public TargetOperationService Service { get; }

        public Exception? EvidenceFailure { get; set; }

        public Exception? CommandFailure { get; set; }

        public bool CancelDuringCommand { get; set; }

        public CancellationTokenSource? CancelAfterCommand { get; set; }

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

        public Task<TargetStartRestartResult>
            ExecuteStartRestartAsync()
        {
            return ExecuteStartRestartAsync(CancellationToken.None);
        }

        public Task<TargetStartRestartResult>
            ExecuteStartRestartAsync(CancellationToken cancellationToken)
        {
            return Service.ExecuteStartRestartAsync(
                "op-s7-start-restart",
                Profile,
                StartRestartGuard,
                ReadObservationAsync,
                ExecuteCommandAsync,
                TimeSpan.FromSeconds(4),
                cancellationToken);
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
            if (CancelDuringCommand)
            {
                using CancellationTokenSource cancellation = new();
                cancellation.Cancel();
                return Task.FromCanceled(cancellation.Token);
            }

            if (CommandFailure is not null)
            {
                return Task.FromException(CommandFailure);
            }

            CancelAfterCommand?.Cancel();
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
