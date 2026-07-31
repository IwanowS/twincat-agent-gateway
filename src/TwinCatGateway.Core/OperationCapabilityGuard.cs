using System;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class OperationCapabilityGuard
{
    private readonly CapabilityKey _capability;
    private readonly CapabilityEvaluator _evaluator;
    private readonly Func<CapabilityEvaluationContext?>? _contextProvider;
    private readonly ResolvedProfile _profile;

    public OperationCapabilityGuard(
        CapabilityEvaluator evaluator,
        ResolvedProfile profile,
        CapabilityKey capability,
        Func<CapabilityEvaluationContext?>? contextProvider = null)
    {
        _evaluator = evaluator
            ?? throw new ArgumentNullException(nameof(evaluator));
        _profile = profile
            ?? throw new ArgumentNullException(nameof(profile));
        _capability = capability;
        _contextProvider = contextProvider;
    }

    public void EnsureAllowed(
        string stage,
        bool sideEffectsStarted = false)
    {
        _evaluator.EnsureAllowed(
            _profile,
            _capability,
            stage,
            _contextProvider?.Invoke(),
            sideEffectsStarted);
    }
}
