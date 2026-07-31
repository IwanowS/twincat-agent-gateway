using System;
using System.Collections.Generic;
using System.Linq;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class CapabilityEvaluationContext
{
    public CapabilityEvaluationContext(int? xaeProcessId = null)
    {
        XaeProcessId = xaeProcessId;
    }

    public int? XaeProcessId { get; }
}

public interface ISessionConsentProvider
{
    bool HasConsent(
        string profile,
        CapabilityKey capability,
        int? xaeProcessId);
}

public interface IOperatorLockProvider
{
    bool IsLocked(
        string? profile,
        CapabilityKey capability);
}

public sealed class NoSessionConsentProvider : ISessionConsentProvider
{
    public static NoSessionConsentProvider Instance { get; } = new();

    private NoSessionConsentProvider()
    {
    }

    public bool HasConsent(
        string profile,
        CapabilityKey capability,
        int? xaeProcessId)
    {
        return false;
    }
}

public sealed class NoOperatorLockProvider : IOperatorLockProvider
{
    public static NoOperatorLockProvider Instance { get; } = new();

    private NoOperatorLockProvider()
    {
    }

    public bool IsLocked(
        string? profile,
        CapabilityKey capability)
    {
        return false;
    }
}

public sealed class CapabilityEvaluator
{
    private readonly bool _allowGatewayShutdown;
    private readonly bool _allowGatewayStart;
    private readonly IOperatorLockProvider _operatorLocks;
    private readonly ISessionConsentProvider _sessionConsent;

    public CapabilityEvaluator(
        GatewayConfiguration configuration,
        ISessionConsentProvider? sessionConsent = null,
        IOperatorLockProvider? operatorLocks = null)
    {
        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        _allowGatewayStart =
            configuration.Gateway.ProcessControl.AllowStart;
        _allowGatewayShutdown =
            configuration.Gateway.ProcessControl.AllowShutdown;
        _sessionConsent =
            sessionConsent ?? NoSessionConsentProvider.Instance;
        _operatorLocks =
            operatorLocks ?? NoOperatorLockProvider.Instance;
    }

    public CapabilityState EvaluateGateway(CapabilityKey key)
    {
        bool configured = key switch
        {
            CapabilityKey.GatewayStart => _allowGatewayStart,
            CapabilityKey.GatewayShutdown => _allowGatewayShutdown,
            _ => throw new ArgumentOutOfRangeException(
                nameof(key),
                key,
                "A Gateway process capability is required."),
        };
        return CreateState(
            key,
            configured,
            sessionConsented: null,
            operatorLocked: false);
    }

    public CapabilityState Evaluate(
        ResolvedProfile profile,
        CapabilityKey key,
        CapabilityEvaluationContext? context = null)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        if (IsGatewayCapability(key))
        {
            throw new ArgumentOutOfRangeException(
                nameof(key),
                key,
                "A profile capability is required.");
        }

        bool configured = profile.IsConfigured(key);
        bool? sessionConsented = key == CapabilityKey.XaeClose
            ? _sessionConsent.HasConsent(
                profile.Name,
                key,
                context?.XaeProcessId)
            : null;
        bool operatorLocked =
            _operatorLocks.IsLocked(profile.Name, key);
        return CreateState(
            key,
            configured,
            sessionConsented,
            operatorLocked);
    }

    public void EnsureGatewayAllowed(
        CapabilityKey key,
        string stage)
    {
        EnsureAllowed(
            EvaluateGateway(key),
            profile: null,
            stage);
    }

    public void EnsureAllowed(
        ResolvedProfile profile,
        CapabilityKey key,
        string stage,
        CapabilityEvaluationContext? context = null,
        bool sideEffectsStarted = false)
    {
        EnsureAllowed(
            Evaluate(profile, key, context),
            profile.Name,
            stage,
            sideEffectsStarted);
    }

    private static CapabilityState CreateState(
        CapabilityKey key,
        bool configured,
        bool? sessionConsented,
        bool operatorLocked)
    {
        CapabilityDenialReason reason =
            !configured
                ? CapabilityDenialReason.CapabilityDisabled
                : sessionConsented == false
                    ? CapabilityDenialReason.XaeCloseConsentRequired
                    : operatorLocked
                        ? CapabilityDenialReason.OperatorLocked
                        : CapabilityDenialReason.None;
        return new CapabilityState
        {
            Key = key,
            Configured = configured,
            SessionConsented = sessionConsented,
            OperatorLocked = operatorLocked,
            Effective = reason == CapabilityDenialReason.None,
            Reason = reason,
        };
    }

    private static void EnsureAllowed(
        CapabilityState state,
        string? profile,
        string stage,
        bool sideEffectsStarted = false)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            throw new ArgumentException(
                "Capability-check stage is required.",
                nameof(stage));
        }

        if (state.Effective)
        {
            return;
        }

        string code = state.Reason switch
        {
            CapabilityDenialReason.CapabilityDisabled =>
                ErrorCodes.CapabilityDisabled,
            CapabilityDenialReason.OperatorLocked =>
                ErrorCodes.OperatorLocked,
            CapabilityDenialReason.XaeCloseConsentRequired =>
                ErrorCodes.XaeCloseConsentRequired,
            _ => throw new InvalidOperationException(
                "A denied capability has no denial reason."),
        };
        throw new GatewayOperationException(
            code,
            profile is null
                ? $"Gateway capability '{state.Key}' is unavailable."
                : $"Capability '{state.Key}' is unavailable for profile "
                    + $"'{profile}'.",
            stage: stage,
            component: ComponentFor(state.Key),
            sideEffectsStarted: sideEffectsStarted,
            expected: profile is null
                ? null
                : new IdentityEvidence
                {
                    Profile = profile,
                });
    }

    private static GatewayComponent ComponentFor(CapabilityKey key)
    {
        return key switch
        {
            CapabilityKey.GatewayStart
                or CapabilityKey.GatewayShutdown =>
                GatewayComponent.Gateway,
            CapabilityKey.XaeLaunch
                or CapabilityKey.XaeClose
                or CapabilityKey.XaeSynchronize
                or CapabilityKey.XaeDiscardDirtyDocuments
                or CapabilityKey.XaeBuild
                or CapabilityKey.XaeActivate =>
                GatewayComponent.Xae,
            CapabilityKey.TargetConfig
                or CapabilityKey.TargetStartRestart =>
                GatewayComponent.Target,
            CapabilityKey.TargetTcUnitVerification =>
                GatewayComponent.Verification,
            _ => GatewayComponent.Profile,
        };
    }

    private static bool IsGatewayCapability(CapabilityKey key)
    {
        return key == CapabilityKey.GatewayStart
            || key == CapabilityKey.GatewayShutdown;
    }
}

public sealed class CapabilitySnapshotStore
{
    private static readonly CapabilityKey[] GatewayKeys =
    {
        CapabilityKey.GatewayStart,
        CapabilityKey.GatewayShutdown,
    };

    private static readonly CapabilityKey[] ProfileKeys =
        Enum.GetValues(typeof(CapabilityKey))
            .Cast<CapabilityKey>()
            .Where(key => !GatewayKeys.Contains(key))
            .OrderBy(key => key)
            .ToArray();

    private readonly CapabilityEvaluator _evaluator;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<CapabilityState>>
        _profileSnapshots =
            new(StringComparer.OrdinalIgnoreCase);
    private List<CapabilityState> _gatewaySnapshot = new();

    public CapabilitySnapshotStore(CapabilityEvaluator evaluator)
    {
        _evaluator = evaluator
            ?? throw new ArgumentNullException(nameof(evaluator));
    }

    public IReadOnlyList<CapabilityState> RefreshGateway()
    {
        List<CapabilityState> snapshot = GatewayKeys
            .Select(_evaluator.EvaluateGateway)
            .Select(Clone)
            .ToList();
        lock (_gate)
        {
            _gatewaySnapshot = snapshot;
            return Clone(snapshot);
        }
    }

    public IReadOnlyList<CapabilityState> RefreshProfile(
        ResolvedProfile profile,
        CapabilityEvaluationContext? context = null)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        List<CapabilityState> snapshot = ProfileKeys
            .Select(key => _evaluator.Evaluate(profile, key, context))
            .Select(Clone)
            .ToList();
        lock (_gate)
        {
            _profileSnapshots[profile.Name] = snapshot;
            return Clone(snapshot);
        }
    }

    public IReadOnlyList<CapabilityState> ReadGateway()
    {
        lock (_gate)
        {
            return Clone(_gatewaySnapshot);
        }
    }

    public IReadOnlyList<CapabilityState> ReadProfile(string profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            throw new ArgumentException(
                "Profile name is required.",
                nameof(profile));
        }

        lock (_gate)
        {
            if (!_profileSnapshots.TryGetValue(
                profile,
                out List<CapabilityState>? snapshot))
            {
                return Array.Empty<CapabilityState>();
            }

            return Clone(snapshot);
        }
    }

    private static CapabilityState[] Clone(
        IEnumerable<CapabilityState> source)
    {
        return source.Select(Clone).ToArray();
    }

    private static CapabilityState Clone(CapabilityState source)
    {
        return new CapabilityState
        {
            Key = source.Key,
            Configured = source.Configured,
            SessionConsented = source.SessionConsented,
            OperatorLocked = source.OperatorLocked,
            Effective = source.Effective,
            Reason = source.Reason,
        };
    }
}

public sealed class OperationCapabilityPreflight
{
    private readonly ResolvedProfile? _activeProfile;
    private readonly CapabilityEvaluator _capabilities;
    private readonly ProfileResolver _profiles;

    public OperationCapabilityPreflight(
        ProfileResolver profiles,
        CapabilityEvaluator capabilities,
        ResolvedProfile? activeProfile)
    {
        _profiles = profiles
            ?? throw new ArgumentNullException(nameof(profiles));
        _capabilities = capabilities
            ?? throw new ArgumentNullException(nameof(capabilities));
        _activeProfile = activeProfile;
    }

    public ResolvedProfile EnsureAllowed(
        string? requestedProfile,
        CapabilityKey capability,
        string stage,
        bool requireTarget = false,
        CapabilityEvaluationContext? context = null)
    {
        ResolvedProfile requested =
            _profiles.Resolve(requestedProfile);
        ResolvedProfile active = _activeProfile
            ?? throw new GatewayOperationException(
                ErrorCodes.GatewayNotReady,
                "No active profile context is available.",
                retryable: true,
                stage: stage,
                component: GatewayComponent.Profile,
                sideEffectsStarted: false,
                expected: new IdentityEvidence
                {
                    Profile = requested.Name,
                    Solution = requested.Xae.Solution,
                });
        if (!string.Equals(
            requested.Name,
            active.Name,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayOperationException(
                ErrorCodes.XaeSolutionMismatch,
                $"Profile '{requested.Name}' is not the active XAE context.",
                stage: stage,
                component: GatewayComponent.Xae,
                sideEffectsStarted: false,
                expected: new IdentityEvidence
                {
                    Profile = requested.Name,
                    Solution = requested.Xae.Solution,
                },
                observed: new IdentityEvidence
                {
                    Profile = active.Name,
                    Solution = active.Xae.Solution,
                });
        }

        if (requireTarget)
        {
            ProfileResolver.RequireTarget(requested);
        }

        _capabilities.EnsureAllowed(
            requested,
            capability,
            stage,
            context);
        return requested;
    }
}
