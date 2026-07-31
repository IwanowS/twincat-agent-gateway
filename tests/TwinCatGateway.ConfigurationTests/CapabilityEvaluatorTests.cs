using System.Collections.Generic;
using System.Linq;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.ConfigurationTests;

public sealed class CapabilityEvaluatorTests
{
    [Fact]
    public void StaticFalseCannotBeElevatedBySessionState()
    {
        TestSessionState session = new()
        {
            Consent = true,
        };
        ResolvedProfile profile = ResolveProfile(close: false);
        CapabilityEvaluator evaluator = CreateEvaluator(session);

        CapabilityState state = evaluator.Evaluate(
            profile,
            CapabilityKey.XaeClose,
            new CapabilityEvaluationContext(41));

        Assert.False(state.Configured);
        Assert.True(state.SessionConsented);
        Assert.False(state.Effective);
        Assert.Equal(
            CapabilityDenialReason.CapabilityDisabled,
            state.Reason);
    }

    [Fact]
    public void CloseWithoutExactPidConsentFailsClosed()
    {
        TestSessionState session = new()
        {
            Consent = true,
            ConsentedProcessId = 42,
        };
        ResolvedProfile profile = ResolveProfile(close: true);
        CapabilityEvaluator evaluator = CreateEvaluator(session);

        CapabilityState state = evaluator.Evaluate(
            profile,
            CapabilityKey.XaeClose,
            new CapabilityEvaluationContext(41));

        Assert.False(state.SessionConsented);
        Assert.False(state.Effective);
        Assert.Equal(
            CapabilityDenialReason.XaeCloseConsentRequired,
            state.Reason);
    }

    [Fact]
    public void ConsentDenialPrecedesOperatorLock()
    {
        TestSessionState session = new()
        {
            Locked = true,
        };
        ResolvedProfile profile = ResolveProfile(close: true);
        CapabilityEvaluator evaluator = CreateEvaluator(session);

        CapabilityState state = evaluator.Evaluate(
            profile,
            CapabilityKey.XaeClose,
            new CapabilityEvaluationContext(41));

        Assert.True(state.OperatorLocked);
        Assert.Equal(
            CapabilityDenialReason.XaeCloseConsentRequired,
            state.Reason);
    }

    [Fact]
    public void OperatorLockDeniesConsentedCapability()
    {
        TestSessionState session = new()
        {
            Consent = true,
            ConsentedProcessId = 41,
            Locked = true,
        };
        ResolvedProfile profile = ResolveProfile(close: true);
        CapabilityEvaluator evaluator = CreateEvaluator(session);

        CapabilityState state = evaluator.Evaluate(
            profile,
            CapabilityKey.XaeClose,
            new CapabilityEvaluationContext(41));

        Assert.False(state.Effective);
        Assert.Equal(
            CapabilityDenialReason.OperatorLocked,
            state.Reason);
    }

    [Fact]
    public void ConfiguredCapabilityWithoutDynamicDenialIsEffective()
    {
        ResolvedProfile profile = ResolveProfile(build: true);
        CapabilityEvaluator evaluator = CreateEvaluator();

        CapabilityState state =
            evaluator.Evaluate(profile, CapabilityKey.XaeBuild);

        Assert.True(state.Configured);
        Assert.Null(state.SessionConsented);
        Assert.False(state.OperatorLocked);
        Assert.True(state.Effective);
        Assert.Equal(CapabilityDenialReason.None, state.Reason);
    }

    [Fact]
    public void EnsureAllowedReturnsStableDenialEvidence()
    {
        ResolvedProfile profile = ResolveProfile(build: false);
        CapabilityEvaluator evaluator = CreateEvaluator();

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => evaluator.EnsureAllowed(
                    profile,
                    CapabilityKey.XaeBuild,
                    "build.admission"));

        Assert.Equal(ErrorCodes.CapabilityDisabled, exception.Code);
        Assert.Equal(GatewayComponent.Xae, exception.Component);
        Assert.False(exception.SideEffectsStarted);
        Assert.Equal("bench", exception.Expected?.Profile);
    }

    [Fact]
    public void GatewayCapabilitiesUseTheSameDenialModel()
    {
        GatewayConfiguration configuration = CreateConfiguration();
        configuration.Gateway.ProcessControl.AllowStart = true;
        configuration.Gateway.ProcessControl.AllowShutdown = false;
        CapabilityEvaluator evaluator = new(configuration);

        Assert.True(
            evaluator.EvaluateGateway(
                CapabilityKey.GatewayStart).Effective);
        Assert.Equal(
            CapabilityDenialReason.CapabilityDisabled,
            evaluator.EvaluateGateway(
                CapabilityKey.GatewayShutdown).Reason);
    }

    [Fact]
    public void TargetCapabilitiesAreDisabledForBuildOnlyProfile()
    {
        ResolvedProfile profile = ResolveProfile();
        CapabilityEvaluator evaluator = CreateEvaluator();

        CapabilityState state = evaluator.Evaluate(
            profile,
            CapabilityKey.TargetConfig);

        Assert.False(state.Configured);
        Assert.False(state.Effective);
    }

    [Fact]
    public void SnapshotStoreIsDeterministicAndDefensive()
    {
        ResolvedProfile profile = ResolveProfile(build: true);
        CapabilitySnapshotStore store = new(CreateEvaluator());

        IReadOnlyList<CapabilityState> first =
            store.RefreshProfile(profile);
        first[0].Effective = !first[0].Effective;
        IReadOnlyList<CapabilityState> second =
            store.ReadProfile("BENCH");

        Assert.Equal(
            second.OrderBy(state => state.Key).Select(state => state.Key),
            second.Select(state => state.Key));
        Assert.NotEqual(first[0].Effective, second[0].Effective);
    }

    private static CapabilityEvaluator CreateEvaluator(
        TestSessionState? session = null)
    {
        TestSessionState effectiveSession = session ?? new();
        return new CapabilityEvaluator(
            CreateConfiguration(),
            effectiveSession,
            effectiveSession);
    }

    private static ResolvedProfile ResolveProfile(
        bool build = false,
        bool close = false)
    {
        GatewayConfiguration configuration = CreateConfiguration();
        configuration.Profiles[0].Xae.Capabilities.Build = build;
        configuration.Profiles[0].Xae.Capabilities.Close = close;
        return new ProfileResolver(configuration).Resolve("bench");
    }

    private static GatewayConfiguration CreateConfiguration()
    {
        return new GatewayConfiguration
        {
            DefaultProfile = "bench",
            Profiles =
            {
                new ProjectProfile
                {
                    Name = "bench",
                    Xae = new XaeProfileConfiguration
                    {
                        Solution = @"C:\Project\Machine.sln",
                    },
                },
            },
        };
    }

    private sealed class TestSessionState
        : ISessionConsentProvider, IOperatorLockProvider
    {
        public bool Consent { get; set; }

        public int? ConsentedProcessId { get; set; }

        public bool Locked { get; set; }

        public bool HasConsent(
            string profile,
            CapabilityKey capability,
            int? xaeProcessId)
        {
            return Consent
                && (!ConsentedProcessId.HasValue
                    || ConsentedProcessId == xaeProcessId);
        }

        public bool IsLocked(
            string? profile,
            CapabilityKey capability)
        {
            return Locked;
        }
    }
}
