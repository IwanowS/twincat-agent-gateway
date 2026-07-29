using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public static class RuntimeOperationPolicy
{
    public static void EnsureBuildAllowed(RuntimeMode runtimeMode)
    {
        if (runtimeMode != RuntimeMode.Exception)
        {
            return;
        }

        throw new GatewayOperationException(
            ErrorCodes.BuildBlockedByRuntimeException,
            "The build was not started because the TwinCAT runtime is "
                + "in Exception. Preserve the current runtime and build "
                + "artifacts for diagnostics; explicitly recover the "
                + "runtime to Config before starting a new build.",
            retryable: true,
            stage: "build.runtimePreflight");
    }

    public static void EnsureActivationAllowed(
        RuntimeMode runtimeMode,
        string stage = "activation.runtimePreflight",
        string? message = null)
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
            retryable: true,
            stage: stage);
    }
}
