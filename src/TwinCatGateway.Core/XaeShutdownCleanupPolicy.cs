using System;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class XaeShutdownCleanupPolicy
{
    private readonly CapabilityEvaluator _capabilities;
    private readonly ResolvedProfile _profile;

    public XaeShutdownCleanupPolicy(
        CapabilityEvaluator capabilities,
        ResolvedProfile profile)
    {
        _capabilities = capabilities
            ?? throw new ArgumentNullException(nameof(capabilities));
        _profile = profile
            ?? throw new ArgumentNullException(nameof(profile));
    }

    public bool CanClose(
        int processId,
        int dirtyDocumentCount,
        string stage)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        if (dirtyDocumentCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dirtyDocumentCount));
        }

        _capabilities.EnsureAllowed(
            _profile,
            CapabilityKey.XaeClose,
            stage,
            new CapabilityEvaluationContext(processId));
        return dirtyDocumentCount == 0;
    }
}
