using System;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Xae;

internal static class XaeTwinCatSystemStateMapper
{
    public static XaeTwinCatSystemObservation FromStartedFlag(
        bool started,
        string? selectedTarget,
        DateTimeOffset observedAtUtc)
    {
        return new XaeTwinCatSystemObservation
        {
            State = started
                ? TargetSystemState.Run
                : TargetSystemState.Unknown,
            RawState = started
                ? "IsTwinCATStarted=true"
                : "IsTwinCATStarted=false",
            SelectedTarget = selectedTarget,
            ObservedAtUtc = observedAtUtc,
            Freshness = ObservationFreshness.Fresh,
        };
    }

    public static XaeTwinCatSystemObservation Unavailable(
        string? selectedTarget,
        DateTimeOffset observedAtUtc,
        string message)
    {
        return new XaeTwinCatSystemObservation
        {
            State = TargetSystemState.Unknown,
            SelectedTarget = selectedTarget,
            ObservedAtUtc = observedAtUtc,
            Freshness = ObservationFreshness.Unavailable,
            Error = new ObservationError
            {
                Code = ErrorCodes.XaeSystemStateUnavailable,
                Message = message,
                Retryable = true,
            },
        };
    }
}
