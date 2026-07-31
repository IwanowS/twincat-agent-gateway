using System;
using System.Collections.Generic;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class XaeCloseConsentState
{
    public string Profile { get; set; } = string.Empty;

    public int ProcessId { get; set; }

    public XaeProcessOwnership Ownership { get; set; } =
        XaeProcessOwnership.Unknown;

    public bool Consented { get; set; }
}

public sealed class XaeCloseConsentStore : ISessionConsentProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, XaeCloseConsentState> _states =
        new(StringComparer.OrdinalIgnoreCase);

    public XaeCloseConsentState Observe(
        string profile,
        int processId,
        XaeProcessOwnership ownership)
    {
        Validate(profile, processId, ownership);
        lock (_gate)
        {
            if (_states.TryGetValue(
                    profile,
                    out XaeCloseConsentState? current)
                && current.ProcessId == processId
                && current.Ownership == ownership)
            {
                return Clone(current);
            }

            XaeCloseConsentState next = new()
            {
                Profile = profile,
                ProcessId = processId,
                Ownership = ownership,
                Consented = ownership
                    == XaeProcessOwnership.GatewayLaunched,
            };
            _states[profile] = next;
            return Clone(next);
        }
    }

    public XaeCloseConsentState SetConsent(
        string profile,
        int processId,
        bool consented)
    {
        ValidateProfile(profile);
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        lock (_gate)
        {
            if (!_states.TryGetValue(
                    profile,
                    out XaeCloseConsentState? current)
                || current.ProcessId != processId)
            {
                throw new GatewayOperationException(
                    ErrorCodes.XaeCloseConsentRequired,
                    "XAE close consent can only be changed for the current exact process ID.",
                    stage: "xae.close.consent",
                    component: GatewayComponent.Xae,
                    sideEffectsStarted: false,
                    expected: new IdentityEvidence
                    {
                        Profile = profile,
                    });
            }

            current.Consented = consented;
            return Clone(current);
        }
    }

    public XaeCloseConsentState? Read(string profile)
    {
        ValidateProfile(profile);
        lock (_gate)
        {
            return _states.TryGetValue(
                    profile,
                    out XaeCloseConsentState? current)
                ? Clone(current)
                : null;
        }
    }

    public int? ReadProcessId(string profile)
    {
        return Read(profile)?.ProcessId;
    }

    public void Clear(string profile, int? expectedProcessId = null)
    {
        ValidateProfile(profile);
        lock (_gate)
        {
            if (!_states.TryGetValue(
                    profile,
                    out XaeCloseConsentState? current)
                || (expectedProcessId.HasValue
                    && current.ProcessId != expectedProcessId.Value))
            {
                return;
            }

            _states.Remove(profile);
        }
    }

    public bool HasConsent(
        string profile,
        CapabilityKey capability,
        int? xaeProcessId)
    {
        if (capability != CapabilityKey.XaeClose
            || !xaeProcessId.HasValue)
        {
            return false;
        }

        lock (_gate)
        {
            return _states.TryGetValue(
                    profile,
                    out XaeCloseConsentState? current)
                && current.ProcessId == xaeProcessId.Value
                && current.Consented;
        }
    }

    private static XaeCloseConsentState Clone(
        XaeCloseConsentState source)
    {
        return new XaeCloseConsentState
        {
            Profile = source.Profile,
            ProcessId = source.ProcessId,
            Ownership = source.Ownership,
            Consented = source.Consented,
        };
    }

    private static void Validate(
        string profile,
        int processId,
        XaeProcessOwnership ownership)
    {
        ValidateProfile(profile);
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        if (!Enum.IsDefined(typeof(XaeProcessOwnership), ownership))
        {
            throw new ArgumentOutOfRangeException(nameof(ownership));
        }
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
