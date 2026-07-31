using System;
using System.Linq;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.ConfigurationTests;

public sealed class OperatorSessionStateTests
{
    [Theory]
    [InlineData(OperatorLockKey.XaeLifecycle, CapabilityKey.XaeLaunch)]
    [InlineData(OperatorLockKey.XaeLifecycle, CapabilityKey.XaeClose)]
    [InlineData(OperatorLockKey.XaeSynchronizationBuild, CapabilityKey.XaeSynchronize)]
    [InlineData(OperatorLockKey.XaeSynchronizationBuild, CapabilityKey.XaeDiscardDirtyDocuments)]
    [InlineData(OperatorLockKey.XaeSynchronizationBuild, CapabilityKey.XaeBuild)]
    [InlineData(OperatorLockKey.XaeActivation, CapabilityKey.XaeActivate)]
    [InlineData(OperatorLockKey.TargetConfigStartRestart, CapabilityKey.TargetConfig)]
    [InlineData(OperatorLockKey.TargetConfigStartRestart, CapabilityKey.TargetStartRestart)]
    [InlineData(OperatorLockKey.TcUnitVerification, CapabilityKey.TargetTcUnitVerification)]
    public void GroupLockMapsToItsMutatingCapabilities(
        OperatorLockKey lockKey,
        CapabilityKey capability)
    {
        OperatorLockStore store = new();

        store.SetLocked("bench", lockKey, locked: true);

        Assert.True(store.IsLocked("bench", capability));
        Assert.False(store.IsLocked("other", capability));
    }

    [Fact]
    public void MasterLockBlocksEveryProfileCapability()
    {
        OperatorLockStore store = new();
        store.SetLocked(
            "bench",
            OperatorLockKey.AllMutating,
            locked: true);

        CapabilityKey[] profileCapabilities =
            Enum.GetValues(typeof(CapabilityKey))
                .Cast<CapabilityKey>()
                .Where(key => key != CapabilityKey.GatewayStart
                    && key != CapabilityKey.GatewayShutdown)
                .ToArray();

        Assert.All(
            profileCapabilities,
            capability => Assert.True(
                store.IsLocked("bench", capability)));
        Assert.False(
            store.IsLocked("other", CapabilityKey.XaeBuild));
    }

    [Fact]
    public void GatewayCapabilitiesRemainOutsideProfileLocks()
    {
        GatewayConfiguration configuration = CreateConfiguration();
        configuration.Gateway.ProcessControl.AllowStart = true;
        configuration.Gateway.ProcessControl.AllowShutdown = true;
        OperatorLockStore locks = new();
        locks.SetLocked(
            "bench",
            OperatorLockKey.AllMutating,
            locked: true);
        CapabilityEvaluator evaluator = new(
            configuration,
            operatorLocks: locks);

        Assert.True(
            evaluator.EvaluateGateway(
                CapabilityKey.GatewayStart).Effective);
        Assert.True(
            evaluator.EvaluateGateway(
                CapabilityKey.GatewayShutdown).Effective);
    }

    [Fact]
    public void LockStateIsSessionOnlyAndResettable()
    {
        OperatorLockStore store = new();
        store.SetLocked(
            "bench",
            OperatorLockKey.XaeLifecycle,
            locked: true);

        store.ResetAll();

        Assert.False(
            store.Read("bench").Single(
                state => state.Key
                    == OperatorLockKey.XaeLifecycle).Locked);
    }

    [Fact]
    public void ReadOnlySessionStateRemainsAvailableUnderMasterLock()
    {
        OperatorLockStore locks = new();
        XaeCloseConsentStore consent = new();
        locks.SetLocked(
            "bench",
            OperatorLockKey.AllMutating,
            locked: true);
        consent.Observe(
            "bench",
            41,
            XaeProcessOwnership.Attached);

        Assert.Equal(6, locks.Read("bench").Count);
        Assert.Equal(41, consent.Read("bench")?.ProcessId);
    }

    [Fact]
    public void StaticCapabilityFalsePrecedesConsentAndOperatorLock()
    {
        GatewayConfiguration configuration = CreateConfiguration();
        ResolvedProfile profile =
            new ProfileResolver(configuration).Resolve("bench");
        OperatorLockStore locks = new();
        XaeCloseConsentStore consent = new();
        locks.SetLocked(
            "bench",
            OperatorLockKey.XaeLifecycle,
            locked: true);
        consent.Observe("bench", 41, XaeProcessOwnership.Attached);
        CapabilityEvaluator evaluator = new(
            configuration,
            consent,
            locks);

        CapabilityState state = evaluator.Evaluate(
            profile,
            CapabilityKey.XaeClose,
            new CapabilityEvaluationContext(41));

        Assert.False(state.Effective);
        Assert.Equal(
            CapabilityDenialReason.CapabilityDisabled,
            state.Reason);
    }

    [Fact]
    public void GuardRechecksLiveLockAndPreservesSideEffectEvidence()
    {
        GatewayConfiguration configuration = CreateConfiguration();
        configuration.Profiles[0].Xae.Capabilities.Build = true;
        ResolvedProfile profile =
            new ProfileResolver(configuration).Resolve("bench");
        OperatorLockStore locks = new();
        OperationCapabilityGuard guard = new(
            new CapabilityEvaluator(
                configuration,
                operatorLocks: locks),
            profile,
            CapabilityKey.XaeBuild);

        guard.EnsureAllowed("build.admission");
        locks.SetLocked(
            "bench",
            OperatorLockKey.XaeSynchronizationBuild,
            locked: true);

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => guard.EnsureAllowed(
                    "build.safeBoundary",
                    sideEffectsStarted: true));

        Assert.Equal(ErrorCodes.OperatorLocked, exception.Code);
        Assert.True(exception.SideEffectsStarted);
        Assert.Equal("build.safeBoundary", exception.Stage);
    }

    [Fact]
    public void GatewayLaunchedPidIsConsentedByDefault()
    {
        XaeCloseConsentStore store = new();

        XaeCloseConsentState state = store.Observe(
            "bench",
            41,
            XaeProcessOwnership.GatewayLaunched);

        Assert.True(state.Consented);
        Assert.True(store.HasConsent(
            "bench",
            CapabilityKey.XaeClose,
            41));
    }

    [Fact]
    public void AttachedPidRequiresExactManualConsent()
    {
        XaeCloseConsentStore store = new();
        store.Observe("bench", 41, XaeProcessOwnership.Attached);

        store.SetConsent("bench", 41, consented: true);

        Assert.True(store.HasConsent(
            "bench",
            CapabilityKey.XaeClose,
            41));
        Assert.False(store.HasConsent(
            "bench",
            CapabilityKey.XaeClose,
            42));
    }

    [Fact]
    public void RepeatedObservationRetainsConsentForExactOwnership()
    {
        XaeCloseConsentStore store = new();
        store.Observe("bench", 41, XaeProcessOwnership.Attached);
        store.SetConsent("bench", 41, consented: true);

        XaeCloseConsentState state = store.Observe(
            "BENCH",
            41,
            XaeProcessOwnership.Attached);

        Assert.True(state.Consented);
    }

    [Fact]
    public void PidReplacementAndDetachResetConsent()
    {
        XaeCloseConsentStore store = new();
        store.Observe("bench", 41, XaeProcessOwnership.Attached);
        store.SetConsent("bench", 41, consented: true);

        XaeCloseConsentState replacement = store.Observe(
            "bench",
            42,
            XaeProcessOwnership.Attached);
        store.Clear("bench", expectedProcessId: 42);

        Assert.False(replacement.Consented);
        Assert.Null(store.Read("bench"));
    }

    [Fact]
    public void OwnershipChangeForExactPidResetsConsent()
    {
        XaeCloseConsentStore store = new();
        store.Observe("bench", 41, XaeProcessOwnership.Attached);
        store.SetConsent("bench", 41, consented: true);

        XaeCloseConsentState state = store.Observe(
            "bench",
            41,
            XaeProcessOwnership.Unknown);

        Assert.False(state.Consented);
    }

    [Fact]
    public void ConsentCannotBeChangedForStalePid()
    {
        XaeCloseConsentStore store = new();
        store.Observe("bench", 42, XaeProcessOwnership.Attached);

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => store.SetConsent(
                    "bench",
                    41,
                    consented: true));

        Assert.Equal(
            ErrorCodes.XaeCloseConsentRequired,
            exception.Code);
        Assert.False(exception.SideEffectsStarted);
    }

    [Fact]
    public void ShutdownCloseRequiresConfiguredConsentAndUnlockedLifecycle()
    {
        GatewayConfiguration configuration = CreateConfiguration();
        configuration.Profiles[0].Xae.Capabilities.Close = true;
        ResolvedProfile profile =
            new ProfileResolver(configuration).Resolve("bench");
        OperatorLockStore locks = new();
        XaeCloseConsentStore consent = new();
        consent.Observe(
            "bench",
            41,
            XaeProcessOwnership.GatewayLaunched);
        CapabilityEvaluator evaluator = new(
            configuration,
            consent,
            locks);

        CapabilityState allowed = evaluator.Evaluate(
            profile,
            CapabilityKey.XaeClose,
            new CapabilityEvaluationContext(41));
        locks.SetLocked(
            "bench",
            OperatorLockKey.XaeLifecycle,
            locked: true);
        CapabilityState locked = evaluator.Evaluate(
            profile,
            CapabilityKey.XaeClose,
            new CapabilityEvaluationContext(41));

        Assert.True(allowed.Effective);
        Assert.False(locked.Effective);
        Assert.Equal(
            CapabilityDenialReason.OperatorLocked,
            locked.Reason);
    }

    [Fact]
    public void AttachedShutdownCloseIsDeniedUntilExactPidConsent()
    {
        GatewayConfiguration configuration = CreateConfiguration();
        configuration.Profiles[0].Xae.Capabilities.Close = true;
        ResolvedProfile profile =
            new ProfileResolver(configuration).Resolve("bench");
        XaeCloseConsentStore consent = new();
        consent.Observe("bench", 41, XaeProcessOwnership.Attached);
        CapabilityEvaluator evaluator = new(
            configuration,
            consent);

        CapabilityState denied = evaluator.Evaluate(
            profile,
            CapabilityKey.XaeClose,
            new CapabilityEvaluationContext(41));
        consent.SetConsent("bench", 41, consented: true);
        CapabilityState allowed = evaluator.Evaluate(
            profile,
            CapabilityKey.XaeClose,
            new CapabilityEvaluationContext(41));

        Assert.Equal(
            CapabilityDenialReason.XaeCloseConsentRequired,
            denied.Reason);
        Assert.True(allowed.Effective);
    }

    [Fact]
    public void ShutdownPolicyAllowsOnlyConsentedCleanExactPid()
    {
        GatewayConfiguration configuration = CreateConfiguration();
        configuration.Profiles[0].Xae.Capabilities.Close = true;
        ResolvedProfile profile =
            new ProfileResolver(configuration).Resolve("bench");
        XaeCloseConsentStore consent = new();
        consent.Observe(
            "bench",
            41,
            XaeProcessOwnership.GatewayLaunched);
        XaeShutdownCleanupPolicy policy = new(
            new CapabilityEvaluator(configuration, consent),
            profile);

        Assert.True(policy.CanClose(
            41,
            dirtyDocumentCount: 0,
            "xae.close.shutdown"));
        Assert.False(policy.CanClose(
            41,
            dirtyDocumentCount: 1,
            "xae.close.shutdown"));
    }

    [Theory]
    [InlineData(false, false, 41, ErrorCodes.CapabilityDisabled)]
    [InlineData(true, false, 42, ErrorCodes.XaeCloseConsentRequired)]
    [InlineData(true, true, 41, ErrorCodes.OperatorLocked)]
    public void ShutdownPolicyRejectsDisabledReplacedAndLockedPid(
        bool closeConfigured,
        bool lifecycleLocked,
        int processId,
        string expectedCode)
    {
        GatewayConfiguration configuration = CreateConfiguration();
        configuration.Profiles[0].Xae.Capabilities.Close =
            closeConfigured;
        ResolvedProfile profile =
            new ProfileResolver(configuration).Resolve("bench");
        XaeCloseConsentStore consent = new();
        consent.Observe(
            "bench",
            41,
            XaeProcessOwnership.GatewayLaunched);
        OperatorLockStore locks = new();
        locks.SetLocked(
            "bench",
            OperatorLockKey.XaeLifecycle,
            lifecycleLocked);
        XaeShutdownCleanupPolicy policy = new(
            new CapabilityEvaluator(
                configuration,
                consent,
                locks),
            profile);

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => policy.CanClose(
                    processId,
                    dirtyDocumentCount: 0,
                    "xae.close.shutdown"));

        Assert.Equal(expectedCode, exception.Code);
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
}
