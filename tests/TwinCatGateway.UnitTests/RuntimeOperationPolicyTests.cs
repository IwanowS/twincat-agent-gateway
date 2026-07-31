using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class RuntimeOperationPolicyTests
{
    [Fact]
    public void ActivationRequiresExplicitRecoveryFromException()
    {
        GatewayOperationException exception = Assert.Throws<
            GatewayOperationException>(
            () => RuntimeOperationPolicy.EnsureActivationAllowed(
                RuntimeMode.Exception));

        Assert.Equal(
            ErrorCodes.RuntimeRecoveryRequired,
            exception.Code);
        Assert.True(exception.Retryable);
        Assert.Equal(
            "activation.runtimePreflight",
            exception.Stage);
    }

    [Fact]
    public void ActivationWaitPreservesVerificationStage()
    {
        GatewayOperationException exception = Assert.Throws<
            GatewayOperationException>(
            () => RuntimeOperationPolicy.EnsureActivationAllowed(
                RuntimeMode.Exception,
                "activation.verify",
                "Runtime entered Exception."));

        Assert.Equal(
            ErrorCodes.RuntimeRecoveryRequired,
            exception.Code);
        Assert.Equal(
            "activation.verify",
            exception.Stage);
        Assert.Equal(
            "Runtime entered Exception.",
            exception.Message);
    }
}
