using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public delegate Task<TargetSystemObservation>
    TargetSystemObservationReader(
        TimeSpan timeout,
        CancellationToken cancellationToken);

public delegate Task<XaeMessagesResult?> TargetFaultEvidenceReader(
    TimeSpan timeout,
    CancellationToken cancellationToken);

public delegate Task TargetTransitionCommand(
    TimeSpan timeout,
    CancellationToken cancellationToken);

public sealed class TargetOperationService
{
    private static readonly TimeSpan ObservationReadLimit =
        TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FaultEvidenceReadLimit =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(250);

    private readonly IClock _clock;

    public TargetOperationService(IClock? clock = null)
    {
        _clock = clock ?? SystemClock.Instance;
    }

    public async Task<TargetConfigResult> ExecuteConfigAsync(
        string operationId,
        ResolvedProfile profile,
        OperationCapabilityGuard capabilityGuard,
        TargetSystemObservationReader observationReader,
        TargetFaultEvidenceReader? faultEvidenceReader,
        TargetTransitionCommand transitionCommand,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ValidateCommon(
            operationId,
            profile,
            capabilityGuard,
            observationReader,
            transitionCommand,
            timeout);

        ResolvedTargetProfile target = profile.Target
            ?? throw new GatewayOperationException(
                ErrorCodes.TargetNotConfigured,
                $"Profile '{profile.Name}' has no configured Target System.",
                stage: "target.config.preflight",
                component: GatewayComponent.Target,
                sideEffectsStarted: false);
        capabilityGuard.EnsureAllowed(
            "target.config.preflight",
            sideEffectsStarted: false);
        DateTimeOffset startedAtUtc = _clock.UtcNow;
        DateTimeOffset deadlineUtc = startedAtUtc.Add(timeout);
        TargetSystemObservation before = await observationReader(
            GetReadTimeout(
                deadlineUtc,
                "target.config.preflight",
                ErrorCodes.TargetConfigFailed,
                sideEffectsStarted: false),
            cancellationToken).ConfigureAwait(false);

        if (IsFreshState(before, TargetSystemState.Config))
        {
            return CreateConfigResult(
                operationId,
                profile,
                target,
                startedAtUtc,
                TargetTransitionAction.NoOp,
                before,
                before,
                faultSnapshot: null);
        }

        TargetFaultSnapshot faultSnapshot =
            await CaptureFaultSnapshotAsync(
                before,
                faultEvidenceReader,
                deadlineUtc,
                cancellationToken).ConfigureAwait(false);

        capabilityGuard.EnsureAllowed(
            "target.config.preSideEffect",
            sideEffectsStarted: false);
        DateTimeOffset commandStartedAtUtc = _clock.UtcNow;
        try
        {
            await transitionCommand(
                GetRemaining(
                    deadlineUtc,
                    "target.config.command",
                    ErrorCodes.TargetConfigFailed,
                    sideEffectsStarted: false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw CreateCancellation(
                exception,
                ErrorCodes.TargetConfigFailed,
                "Target Config was cancelled after the command started.",
                "target.config.command");
        }
        catch (GatewayOperationException exception)
        {
            throw NormalizeCommandFailure(
                exception,
                ErrorCodes.TargetConfigFailed,
                "Target Config command failed.",
                "target.config.command");
        }
        catch (Exception exception)
        {
            throw new GatewayOperationException(
                ErrorCodes.TargetConfigFailed,
                "Target Config command failed.",
                retryable: true,
                stage: "target.config.command",
                innerException: exception,
                component: GatewayComponent.Target,
                sideEffectsStarted: true);
        }

        TargetSystemObservation after;
        try
        {
            after = await WaitForStateAsync(
                observationReader,
                TargetSystemState.Config,
                commandStartedAtUtc,
                deadlineUtc,
                ErrorCodes.TargetConfigPostconditionMissing,
                "Target did not reach Config before the operation deadline.",
                "target.config.postcondition",
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw CreateCancellation(
                exception,
                ErrorCodes.TargetConfigPostconditionMissing,
                "Target Config was cancelled while awaiting its postcondition.",
                "target.config.postcondition");
        }
        return CreateConfigResult(
            operationId,
            profile,
            target,
            startedAtUtc,
            TargetTransitionAction.Config,
            before,
            after,
            faultSnapshot);
    }

    public async Task<TargetStartRestartResult>
        ExecuteStartRestartAsync(
            string operationId,
            ResolvedProfile profile,
            OperationCapabilityGuard capabilityGuard,
            TargetSystemObservationReader observationReader,
            TargetTransitionCommand transitionCommand,
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        ValidateCommon(
            operationId,
            profile,
            capabilityGuard,
            observationReader,
            transitionCommand,
            timeout);

        ResolvedTargetProfile target = profile.Target
            ?? throw new GatewayOperationException(
                ErrorCodes.TargetNotConfigured,
                $"Profile '{profile.Name}' has no configured Target System.",
                stage: "target.startRestart.preflight",
                component: GatewayComponent.Target,
                sideEffectsStarted: false);
        capabilityGuard.EnsureAllowed(
            "target.startRestart.preflight",
            sideEffectsStarted: false);
        DateTimeOffset startedAtUtc = _clock.UtcNow;
        DateTimeOffset deadlineUtc = startedAtUtc.Add(timeout);
        TargetSystemObservation before = await observationReader(
            GetReadTimeout(
                deadlineUtc,
                "target.startRestart.preflight",
                ErrorCodes.TargetAdsUnavailable,
                sideEffectsStarted: false),
            cancellationToken).ConfigureAwait(false);
        if (before.Freshness != ObservationFreshness.Fresh
            || before.Error is not null)
        {
            throw new GatewayOperationException(
                ErrorCodes.TargetAdsUnavailable,
                "A fresh direct Target System observation is required "
                    + "before start/restart.",
                before.Error?.Message,
                retryable: true,
                stage: "target.startRestart.preflight",
                component: GatewayComponent.Target,
                sideEffectsStarted: false,
                expected: CreateTargetEvidence(profile, target),
                observed: CreateObservationEvidence(before));
        }

        TargetTransitionAction action;
        switch (before.State)
        {
            case TargetSystemState.Config:
            case TargetSystemState.Stop:
                action = TargetTransitionAction.Start;
                break;
            case TargetSystemState.Run:
                action = TargetTransitionAction.Restart;
                break;
            default:
                throw new GatewayOperationException(
                    ErrorCodes.TargetStartRestartFailed,
                    "Target start/restart requires a fresh Config, Stop, "
                        + "or Run observation.",
                    retryable: false,
                    stage: "target.startRestart.preflight",
                    component: GatewayComponent.Target,
                    sideEffectsStarted: false,
                    expected: CreateTargetEvidence(profile, target),
                    observed: CreateObservationEvidence(before));
        }

        capabilityGuard.EnsureAllowed(
            "target.startRestart.preSideEffect",
            sideEffectsStarted: false);
        DateTimeOffset commandStartedAtUtc = _clock.UtcNow;
        try
        {
            await transitionCommand(
                GetRemaining(
                    deadlineUtc,
                    "target.startRestart.command",
                    ErrorCodes.TargetStartRestartFailed,
                    sideEffectsStarted: false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw CreateCancellation(
                exception,
                ErrorCodes.TargetStartRestartFailed,
                "Target start/restart was cancelled after the command started.",
                "target.startRestart.command");
        }
        catch (GatewayOperationException exception)
        {
            throw NormalizeCommandFailure(
                exception,
                ErrorCodes.TargetStartRestartFailed,
                "Target start/restart command failed.",
                "target.startRestart.command");
        }
        catch (Exception exception)
        {
            throw new GatewayOperationException(
                ErrorCodes.TargetStartRestartFailed,
                "Target start/restart command failed.",
                retryable: true,
                stage: "target.startRestart.command",
                innerException: exception,
                component: GatewayComponent.Target,
                sideEffectsStarted: true);
        }

        TargetSystemObservation after;
        try
        {
            after = await WaitForStateAsync(
                observationReader,
                TargetSystemState.Run,
                commandStartedAtUtc,
                deadlineUtc,
                ErrorCodes.TargetRunPostconditionMissing,
                "Target did not reach Run before the operation deadline.",
                "target.startRestart.postcondition",
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw CreateCancellation(
                exception,
                ErrorCodes.TargetRunPostconditionMissing,
                "Target start/restart was cancelled while awaiting its postcondition.",
                "target.startRestart.postcondition");
        }
        return new TargetStartRestartResult
        {
            Ok = true,
            OperationId = operationId,
            DurationMs = Math.Max(
                0,
                (long)(_clock.UtcNow - startedAtUtc).TotalMilliseconds),
            Profile = profile.Name,
            Target = new TargetIdentity
            {
                Name = target.Name,
                AmsNetId = target.AmsNetId,
            },
            Action = action,
            Before = before,
            After = after,
            Verification = new OperationStageResult<TestResult>
            {
                OperationId = operationId,
                Component = GatewayComponent.Verification,
                Stage = "target.startRestart.verification",
                Completion = OperationCompletion.Skipped,
                SideEffectsStarted = false,
            },
        };
    }

    private async Task<TargetFaultSnapshot> CaptureFaultSnapshotAsync(
        TargetSystemObservation before,
        TargetFaultEvidenceReader? faultEvidenceReader,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        TargetFaultSnapshot snapshot = new()
        {
            Target = before,
        };
        if (faultEvidenceReader is null)
        {
            return snapshot;
        }

        try
        {
            TimeSpan remaining = GetRemaining(
                deadlineUtc,
                "target.config.evidence",
                ErrorCodes.TargetConfigFailed,
                sideEffectsStarted: false);
            TimeSpan timeout = remaining < FaultEvidenceReadLimit
                ? remaining
                : FaultEvidenceReadLimit;
            snapshot.XaeMessages = await faultEvidenceReader(
                timeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            snapshot.Diagnostics.Add(new OperationDiagnostic
            {
                Code = ErrorCodes.XaeSystemStateUnavailable,
                Component = GatewayComponent.Xae,
                Stage = "target.config.evidence",
                Severity = DiagnosticSeverity.Warning,
                Message = exception.Message,
                OccurredAtUtc = _clock.UtcNow,
            });
        }

        return snapshot;
    }

    private async Task<TargetSystemObservation> WaitForStateAsync(
        TargetSystemObservationReader observationReader,
        TargetSystemState expectedState,
        DateTimeOffset notBeforeUtc,
        DateTimeOffset deadlineUtc,
        string errorCode,
        string errorMessage,
        string stage,
        CancellationToken cancellationToken)
    {
        while (_clock.UtcNow < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TargetSystemObservation observation = await observationReader(
                GetReadTimeout(
                    deadlineUtc,
                    stage,
                    errorCode,
                    sideEffectsStarted: true),
                cancellationToken).ConfigureAwait(false);
            if (IsFreshState(observation, expectedState)
                && observation.ObservedAtUtc >= notBeforeUtc)
            {
                return observation;
            }

            TimeSpan remaining = deadlineUtc - _clock.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            TimeSpan delay = remaining < PollInterval
                ? remaining
                : PollInterval;
            await Task.Delay(delay, cancellationToken)
                .ConfigureAwait(false);
        }

        throw new GatewayOperationException(
            errorCode,
            errorMessage,
            retryable: true,
            stage: stage,
            component: GatewayComponent.Target,
            sideEffectsStarted: true);
    }

    private TargetConfigResult CreateConfigResult(
        string operationId,
        ResolvedProfile profile,
        ResolvedTargetProfile target,
        DateTimeOffset startedAtUtc,
        TargetTransitionAction action,
        TargetSystemObservation before,
        TargetSystemObservation after,
        TargetFaultSnapshot? faultSnapshot)
    {
        return new TargetConfigResult
        {
            Ok = true,
            OperationId = operationId,
            DurationMs = Math.Max(
                0,
                (long)(_clock.UtcNow - startedAtUtc).TotalMilliseconds),
            Profile = profile.Name,
            Target = new TargetIdentity
            {
                Name = target.Name,
                AmsNetId = target.AmsNetId,
            },
            Action = action,
            Before = before,
            After = after,
            FaultSnapshot = faultSnapshot,
        };
    }

    private static void ValidateCommon(
        string operationId,
        ResolvedProfile profile,
        OperationCapabilityGuard capabilityGuard,
        TargetSystemObservationReader observationReader,
        TargetTransitionCommand transitionCommand,
        TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new ArgumentException(
                "Operation id is required.",
                nameof(operationId));
        }

        _ = profile ?? throw new ArgumentNullException(nameof(profile));
        _ = capabilityGuard
            ?? throw new ArgumentNullException(nameof(capabilityGuard));
        _ = observationReader
            ?? throw new ArgumentNullException(nameof(observationReader));
        _ = transitionCommand
            ?? throw new ArgumentNullException(nameof(transitionCommand));
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    private static bool IsFreshState(
        TargetSystemObservation observation,
        TargetSystemState state)
    {
        return observation.Freshness == ObservationFreshness.Fresh
            && observation.Error is null
            && observation.State == state;
    }

    private TimeSpan GetReadTimeout(
        DateTimeOffset deadlineUtc,
        string stage,
        string errorCode,
        bool sideEffectsStarted)
    {
        TimeSpan remaining = GetRemaining(
            deadlineUtc,
            stage,
            errorCode,
            sideEffectsStarted);
        return remaining < ObservationReadLimit
            ? remaining
            : ObservationReadLimit;
    }

    private TimeSpan GetRemaining(
        DateTimeOffset deadlineUtc,
        string stage,
        string errorCode,
        bool sideEffectsStarted)
    {
        TimeSpan remaining = deadlineUtc - _clock.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            return remaining;
        }

        throw new GatewayOperationException(
            errorCode,
            "Target operation deadline expired.",
            retryable: true,
            stage: stage,
            component: GatewayComponent.Target,
            sideEffectsStarted: sideEffectsStarted);
    }

    private static GatewayOperationException NormalizeCommandFailure(
        GatewayOperationException exception,
        string fallbackCode,
        string fallbackMessage,
        string fallbackStage)
    {
        return new GatewayOperationException(
            string.IsNullOrWhiteSpace(exception.Code)
                ? fallbackCode
                : exception.Code,
            string.IsNullOrWhiteSpace(exception.Message)
                ? fallbackMessage
                : exception.Message,
            exception.Details,
            exception.Retryable,
            exception.Stage ?? fallbackStage,
            exception.RawLogRef,
            exception,
            exception.Component ?? GatewayComponent.Target,
            exception.SideEffectsStarted ?? true,
            exception.Expected,
            exception.Observed);
    }

    private static GatewayOperationCanceledException CreateCancellation(
        OperationCanceledException exception,
        string code,
        string message,
        string stage)
    {
        return exception as GatewayOperationCanceledException
            ?? new GatewayOperationCanceledException(
                code,
                message,
                stage,
                GatewayComponent.Target,
                sideEffectsStarted: true,
                exception);
    }

    private static IdentityEvidence CreateTargetEvidence(
        ResolvedProfile profile,
        ResolvedTargetProfile target)
    {
        return new IdentityEvidence
        {
            Profile = profile.Name,
            Solution = profile.Xae.Solution,
            AmsNetId = target.AmsNetId,
            Port = 10000,
        };
    }

    private static IdentityEvidence CreateObservationEvidence(
        TargetSystemObservation observation)
    {
        return new IdentityEvidence
        {
            Profile = observation.Profile,
            AmsNetId = observation.AmsNetId,
            Port = observation.Port,
        };
    }
}
