using System;
using System.Collections.Generic;
using System.Linq;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class OperatorLockState
{
    public string Profile { get; set; } = string.Empty;

    public OperatorLockKey Key { get; set; }

    public bool Locked { get; set; }
}

public sealed class OperatorLockStore : IOperatorLockProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<OperatorLockKey>> _locks =
        new(StringComparer.OrdinalIgnoreCase);

    public void SetLocked(
        string profile,
        OperatorLockKey key,
        bool locked)
    {
        ValidateProfile(profile);
        if (!Enum.IsDefined(typeof(OperatorLockKey), key))
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        lock (_gate)
        {
            if (!_locks.TryGetValue(
                    profile,
                    out HashSet<OperatorLockKey>? profileLocks))
            {
                if (!locked)
                {
                    return;
                }

                profileLocks = new HashSet<OperatorLockKey>();
                _locks.Add(profile, profileLocks);
            }

            if (locked)
            {
                profileLocks.Add(key);
            }
            else
            {
                profileLocks.Remove(key);
                if (profileLocks.Count == 0)
                {
                    _locks.Remove(profile);
                }
            }
        }
    }

    public IReadOnlyList<OperatorLockState> Read(string profile)
    {
        ValidateProfile(profile);
        lock (_gate)
        {
            _locks.TryGetValue(
                profile,
                out HashSet<OperatorLockKey>? profileLocks);
            return Enum.GetValues(typeof(OperatorLockKey))
                .Cast<OperatorLockKey>()
                .OrderBy(key => key)
                .Select(key => new OperatorLockState
                {
                    Profile = profile,
                    Key = key,
                    Locked = profileLocks?.Contains(key) == true,
                })
                .ToArray();
        }
    }

    public bool IsLocked(
        string? profile,
        CapabilityKey capability)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            return false;
        }

        OperatorLockKey group = GroupFor(capability);
        lock (_gate)
        {
            return _locks.TryGetValue(
                    profile!,
                    out HashSet<OperatorLockKey>? profileLocks)
                && (profileLocks.Contains(OperatorLockKey.AllMutating)
                    || profileLocks.Contains(group));
        }
    }

    public void ResetProfile(string profile)
    {
        ValidateProfile(profile);
        lock (_gate)
        {
            _locks.Remove(profile);
        }
    }

    public void ResetAll()
    {
        lock (_gate)
        {
            _locks.Clear();
        }
    }

    private static OperatorLockKey GroupFor(CapabilityKey capability)
    {
        return capability switch
        {
            CapabilityKey.XaeLaunch or CapabilityKey.XaeClose =>
                OperatorLockKey.XaeLifecycle,
            CapabilityKey.XaeSynchronize
                or CapabilityKey.XaeDiscardDirtyDocuments
                or CapabilityKey.XaeBuild =>
                OperatorLockKey.XaeSynchronizationBuild,
            CapabilityKey.XaeActivate =>
                OperatorLockKey.XaeActivation,
            CapabilityKey.TargetConfig
                or CapabilityKey.TargetStartRestart =>
                OperatorLockKey.TargetConfigStartRestart,
            CapabilityKey.TargetTcUnitVerification =>
                OperatorLockKey.TcUnitVerification,
            CapabilityKey.GatewayStart
                or CapabilityKey.GatewayShutdown =>
                throw new ArgumentOutOfRangeException(
                    nameof(capability),
                    capability,
                    "Gateway process capabilities are not profile-scoped."),
            _ => throw new ArgumentOutOfRangeException(
                nameof(capability),
                capability,
                "The capability has no operator-lock group."),
        };
    }

    private static void ValidateProfile(string profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            throw new ArgumentException(
                "Profile name is required.",
                nameof(profile));
        }
    }
}
