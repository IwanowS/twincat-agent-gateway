using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public static class RuntimeOperationPolicy
{
    public static void EnsureActivationAllowed(
        RuntimeMode runtimeMode,
        string stage = "activation.runtimePreflight",
        string? message = null,
        string? details = null)
    {
        if (runtimeMode != RuntimeMode.Exception)
        {
            return;
        }

        throw new GatewayOperationException(
            ErrorCodes.RuntimeRecoveryRequired,
            message
                ?? "Activation was not started because the TwinCAT runtime "
                    + "is in Exception. Explicitly recover the runtime to "
                    + "Config before activating a new configuration.",
            details: details,
            retryable: true,
            stage: stage);
    }
}
