using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class ProfileObservationSnapshot
{
    internal ProfileObservationSnapshot(
        XaeTwinCatSystemObservation? xae,
        TargetSystemObservation target,
        IReadOnlyList<PlcRuntimeObservation> plcRuntimes,
        StateObservationDivergence? divergence)
    {
        Xae = xae;
        Target = target;
        PlcRuntimes = plcRuntimes;
        Divergence = divergence;
    }

    public XaeTwinCatSystemObservation? Xae { get; }

    public TargetSystemObservation Target { get; }

    public IReadOnlyList<PlcRuntimeObservation> PlcRuntimes { get; }

    public StateObservationDivergence? Divergence { get; }
}

public sealed class ProfileObservationStore
{
    private readonly object _writeSync = new();
    private readonly string _profile;
    private readonly string _amsNetId;
    private ProfileObservationSnapshot _snapshot;

    public ProfileObservationStore(
        string profile,
        string amsNetId)
    {
        _profile = string.IsNullOrWhiteSpace(profile)
            ? throw new ArgumentException(
                "Profile is required.",
                nameof(profile))
            : profile;
        _amsNetId = string.IsNullOrWhiteSpace(amsNetId)
            ? throw new ArgumentException(
                "AMS NetId is required.",
                nameof(amsNetId))
            : amsNetId;
        _snapshot = CreateSnapshot(
            xae: null,
            CreateUnknownTarget(),
            Array.Empty<PlcRuntimeObservation>());
    }

    public ProfileObservationSnapshot Read()
    {
        return Clone(Volatile.Read(ref _snapshot));
    }

    public ProfileObservationSnapshot ConfigureRuntimes(
        IEnumerable<PlcRuntimeTarget> runtimes)
    {
        if (runtimes is null)
        {
            throw new ArgumentNullException(nameof(runtimes));
        }

        PlcRuntimeTarget[] configured = runtimes
            .OrderBy(runtime => runtime.AdsPort)
            .ToArray();
        lock (_writeSync)
        {
            Dictionary<string, PlcRuntimeObservation> existing =
                _snapshot.PlcRuntimes.ToDictionary(
                    runtime => runtime.RuntimeId,
                    StringComparer.OrdinalIgnoreCase);
            List<PlcRuntimeObservation> next = new();
            HashSet<string> runtimeIds =
                new(StringComparer.OrdinalIgnoreCase);
            HashSet<int> ports = new();
            foreach (PlcRuntimeTarget runtime in configured)
            {
                if (!runtimeIds.Add(runtime.RuntimeId))
                {
                    throw new ArgumentException(
                        $"Duplicate runtime id '{runtime.RuntimeId}'.",
                        nameof(runtimes));
                }

                if (!ports.Add(runtime.AdsPort))
                {
                    throw new ArgumentException(
                        $"Duplicate PLC ADS port '{runtime.AdsPort}'.",
                        nameof(runtimes));
                }

                if (existing.TryGetValue(
                        runtime.RuntimeId,
                        out PlcRuntimeObservation? previous)
                    && previous.Port == runtime.AdsPort)
                {
                    PlcRuntimeObservation preserved =
                        Clone(previous);
                    preserved.Project = runtime.Project;
                    preserved.Instance = runtime.Instance;
                    next.Add(preserved);
                }
                else
                {
                    next.Add(new PlcRuntimeObservation
                    {
                        Profile = _profile,
                        RuntimeId = runtime.RuntimeId,
                        Project = runtime.Project,
                        Instance = runtime.Instance,
                        AmsNetId = _amsNetId,
                        Port = runtime.AdsPort,
                        State = PlcRuntimeState.Unknown,
                        Freshness = ObservationFreshness.Unknown,
                    });
                }
            }

            return Replace(
                _snapshot.Xae,
                _snapshot.Target,
                next);
        }
    }

    public ProfileObservationSnapshot PublishXae(
        XaeTwinCatSystemObservation observation)
    {
        if (observation is null)
        {
            throw new ArgumentNullException(nameof(observation));
        }

        lock (_writeSync)
        {
            return Replace(
                observation,
                _snapshot.Target,
                _snapshot.PlcRuntimes);
        }
    }

    public ProfileObservationSnapshot MarkXaeUnavailable(
        DateTimeOffset attemptedAtUtc,
        ObservationError error)
    {
        if (error is null)
        {
            throw new ArgumentNullException(nameof(error));
        }

        lock (_writeSync)
        {
            XaeTwinCatSystemObservation next;
            if (HasRawObservation(_snapshot.Xae))
            {
                next = Clone(_snapshot.Xae!);
                next.Freshness = ObservationFreshness.Stale;
                next.Error = Clone(error);
            }
            else
            {
                next = new XaeTwinCatSystemObservation
                {
                    State = TargetSystemState.Unknown,
                    SelectedTarget =
                        _snapshot.Xae?.SelectedTarget,
                    ObservedAtUtc = attemptedAtUtc,
                    Freshness =
                        ObservationFreshness.Unavailable,
                    Error = Clone(error),
                };
            }

            return Replace(
                next,
                _snapshot.Target,
                _snapshot.PlcRuntimes);
        }
    }

    public ProfileObservationSnapshot PublishTarget(
        TargetSystemObservation observation)
    {
        if (observation is null)
        {
            throw new ArgumentNullException(nameof(observation));
        }

        VerifyTargetIdentity(
            observation.Profile,
            observation.AmsNetId,
            observation.Port);
        lock (_writeSync)
        {
            return Replace(
                _snapshot.Xae,
                observation,
                _snapshot.PlcRuntimes);
        }
    }

    public ProfileObservationSnapshot MarkTargetReadFailed(
        DateTimeOffset attemptedAtUtc,
        ObservationError error)
    {
        if (error is null)
        {
            throw new ArgumentNullException(nameof(error));
        }

        lock (_writeSync)
        {
            TargetSystemObservation next;
            if (_snapshot.Target.RawAdsState.HasValue)
            {
                next = Clone(_snapshot.Target);
                next.Freshness = ObservationFreshness.Stale;
                next.Error = Clone(error);
            }
            else
            {
                next = CreateUnknownTarget();
                next.ObservedAtUtc = attemptedAtUtc;
                next.Freshness =
                    ObservationFreshness.Unavailable;
                next.Error = Clone(error);
            }

            return Replace(
                _snapshot.Xae,
                next,
                _snapshot.PlcRuntimes);
        }
    }

    public ProfileObservationSnapshot PublishPlc(
        PlcRuntimeObservation observation)
    {
        if (observation is null)
        {
            throw new ArgumentNullException(nameof(observation));
        }

        lock (_writeSync)
        {
            int index = FindRuntimeIndex(
                observation.RuntimeId,
                observation.Port);
            List<PlcRuntimeObservation> next =
                ClonePlcs(_snapshot.PlcRuntimes);
            next[index] = Clone(observation);
            return Replace(
                _snapshot.Xae,
                _snapshot.Target,
                next);
        }
    }

    public ProfileObservationSnapshot MarkPlcReadFailed(
        string runtimeId,
        int port,
        DateTimeOffset attemptedAtUtc,
        ObservationError error)
    {
        return MarkPlcUnavailable(
            runtimeId,
            port,
            attemptedAtUtc,
            error,
            preserveLastSuccessful: true);
    }

    public ProfileObservationSnapshot MarkPlcNotObserved(
        string runtimeId,
        int port,
        DateTimeOffset attemptedAtUtc)
    {
        return MarkPlcUnavailable(
            runtimeId,
            port,
            attemptedAtUtc,
            new ObservationError
            {
                Code = ErrorCodes.PlcStateNotObserved,
                Message =
                    "PLC state was not read because the Target "
                    + "System Service is not in Run.",
                Retryable = true,
            },
            preserveLastSuccessful: false);
    }

    private ProfileObservationSnapshot MarkPlcUnavailable(
        string runtimeId,
        int port,
        DateTimeOffset attemptedAtUtc,
        ObservationError error,
        bool preserveLastSuccessful)
    {
        if (error is null)
        {
            throw new ArgumentNullException(nameof(error));
        }

        lock (_writeSync)
        {
            int index = FindRuntimeIndex(runtimeId, port);
            List<PlcRuntimeObservation> next =
                ClonePlcs(_snapshot.PlcRuntimes);
            PlcRuntimeObservation previous = next[index];
            if (preserveLastSuccessful
                && previous.RawAdsState.HasValue)
            {
                previous.Freshness =
                    ObservationFreshness.Stale;
                previous.Error = Clone(error);
            }
            else
            {
                next[index] = new PlcRuntimeObservation
                {
                    Profile = _profile,
                    RuntimeId = previous.RuntimeId,
                    Project = previous.Project,
                    Instance = previous.Instance,
                    AmsNetId = _amsNetId,
                    Port = previous.Port,
                    State = PlcRuntimeState.Unknown,
                    ObservedAtUtc = attemptedAtUtc,
                    Freshness =
                        ObservationFreshness.Unavailable,
                    Error = Clone(error),
                };
            }

            return Replace(
                _snapshot.Xae,
                _snapshot.Target,
                next);
        }
    }

    private int FindRuntimeIndex(
        string runtimeId,
        int port)
    {
        for (int index = 0;
             index < _snapshot.PlcRuntimes.Count;
             index++)
        {
            PlcRuntimeObservation candidate =
                _snapshot.PlcRuntimes[index];
            if (string.Equals(
                    candidate.RuntimeId,
                    runtimeId,
                    StringComparison.OrdinalIgnoreCase)
                && candidate.Port == port)
            {
                return index;
            }
        }

        throw new InvalidOperationException(
            $"PLC runtime '{runtimeId}' on port {port} is not configured.");
    }

    private void VerifyTargetIdentity(
        string profile,
        string amsNetId,
        int port)
    {
        if (!string.Equals(
                profile,
                _profile,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                amsNetId,
                _amsNetId,
                StringComparison.OrdinalIgnoreCase)
            || port != 10000)
        {
            throw new InvalidOperationException(
                "Target observation identity does not match "
                + "the profile observation store.");
        }
    }

    private ProfileObservationSnapshot Replace(
        XaeTwinCatSystemObservation? xae,
        TargetSystemObservation target,
        IEnumerable<PlcRuntimeObservation> plcs)
    {
        ProfileObservationSnapshot next =
            CreateSnapshot(xae, target, plcs);
        Interlocked.Exchange(ref _snapshot, next);
        return Clone(next);
    }

    private ProfileObservationSnapshot CreateSnapshot(
        XaeTwinCatSystemObservation? xae,
        TargetSystemObservation target,
        IEnumerable<PlcRuntimeObservation> plcs)
    {
        XaeTwinCatSystemObservation? xaeClone =
            xae is null ? null : Clone(xae);
        TargetSystemObservation targetClone = Clone(target);
        List<PlcRuntimeObservation> plcClones =
            plcs.Select(Clone)
                .OrderBy(runtime => runtime.Port)
                .ToList();
        targetClone.PlcRuntimeResources = plcClones
            .Select(runtime => new ResourceReference
            {
                Uri =
                    "twincat-plc://profile/"
                    + $"{_profile}/{runtime.RuntimeId}/state",
                MimeType = "application/json",
            })
            .ToList();
        return new ProfileObservationSnapshot(
            xaeClone,
            targetClone,
            plcClones,
            CreateDivergence(xaeClone, targetClone));
    }

    private StateObservationDivergence? CreateDivergence(
        XaeTwinCatSystemObservation? xae,
        TargetSystemObservation target)
    {
        if (xae is null
            || xae.Freshness != ObservationFreshness.Fresh
            || target.Freshness != ObservationFreshness.Fresh
            || xae.State == TargetSystemState.Unknown
            || target.State == TargetSystemState.Unknown
            || xae.State == target.State
            || !string.Equals(
                xae.SelectedTarget,
                _amsNetId,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new StateObservationDivergence
        {
            Profile = _profile,
            AmsNetId = _amsNetId,
            XaeObserved = xae.State,
            SystemServiceObserved = target.State,
            XaeObservedAtUtc = xae.ObservedAtUtc,
            SystemServiceObservedAtUtc =
                target.ObservedAtUtc,
        };
    }

    private TargetSystemObservation CreateUnknownTarget()
    {
        return new TargetSystemObservation
        {
            Profile = _profile,
            AmsNetId = _amsNetId,
            Port = 10000,
            State = TargetSystemState.Unknown,
            Freshness = ObservationFreshness.Unknown,
        };
    }

    private static bool HasRawObservation(
        XaeTwinCatSystemObservation? observation)
    {
        return observation is not null
            && !string.IsNullOrWhiteSpace(
                observation.RawState);
    }

    private static ProfileObservationSnapshot Clone(
        ProfileObservationSnapshot source)
    {
        return new ProfileObservationSnapshot(
            source.Xae is null
                ? null
                : Clone(source.Xae),
            Clone(source.Target),
            source.PlcRuntimes
                .Select(Clone)
                .ToArray(),
            source.Divergence is null
                ? null
                : Clone(source.Divergence));
    }

    private static XaeTwinCatSystemObservation Clone(
        XaeTwinCatSystemObservation source)
    {
        return new XaeTwinCatSystemObservation
        {
            Source = source.Source,
            State = source.State,
            RawState = source.RawState,
            SelectedTarget = source.SelectedTarget,
            ObservedAtUtc = source.ObservedAtUtc,
            Freshness = source.Freshness,
            Error = source.Error is null
                ? null
                : Clone(source.Error),
        };
    }

    private static TargetSystemObservation Clone(
        TargetSystemObservation source)
    {
        return new TargetSystemObservation
        {
            Source = source.Source,
            Profile = source.Profile,
            AmsNetId = source.AmsNetId,
            Port = source.Port,
            RawAdsState = source.RawAdsState,
            RawAdsStateName = source.RawAdsStateName,
            RawDeviceState = source.RawDeviceState,
            State = source.State,
            ObservedAtUtc = source.ObservedAtUtc,
            Freshness = source.Freshness,
            Error = source.Error is null
                ? null
                : Clone(source.Error),
            PlcRuntimeResources = source.PlcRuntimeResources
                .Select(Clone)
                .ToList(),
        };
    }

    private static PlcRuntimeObservation Clone(
        PlcRuntimeObservation source)
    {
        return new PlcRuntimeObservation
        {
            Source = source.Source,
            Profile = source.Profile,
            RuntimeId = source.RuntimeId,
            Project = source.Project,
            Instance = source.Instance,
            AmsNetId = source.AmsNetId,
            Port = source.Port,
            RawAdsState = source.RawAdsState,
            RawAdsStateName = source.RawAdsStateName,
            RawDeviceState = source.RawDeviceState,
            State = source.State,
            ObservedAtUtc = source.ObservedAtUtc,
            Freshness = source.Freshness,
            Error = source.Error is null
                ? null
                : Clone(source.Error),
        };
    }

    private static List<PlcRuntimeObservation> ClonePlcs(
        IEnumerable<PlcRuntimeObservation> source)
    {
        return source.Select(Clone).ToList();
    }

    private static StateObservationDivergence Clone(
        StateObservationDivergence source)
    {
        return new StateObservationDivergence
        {
            Code = source.Code,
            Component = source.Component,
            Profile = source.Profile,
            AmsNetId = source.AmsNetId,
            XaeObserved = source.XaeObserved,
            SystemServiceObserved =
                source.SystemServiceObserved,
            XaeObservedAtUtc = source.XaeObservedAtUtc,
            SystemServiceObservedAtUtc =
                source.SystemServiceObservedAtUtc,
        };
    }

    private static ObservationError Clone(
        ObservationError source)
    {
        return new ObservationError
        {
            Code = source.Code,
            Message = source.Message,
            Retryable = source.Retryable,
        };
    }

    private static ResourceReference Clone(
        ResourceReference source)
    {
        return new ResourceReference
        {
            Uri = source.Uri,
            MimeType = source.MimeType,
        };
    }
}
