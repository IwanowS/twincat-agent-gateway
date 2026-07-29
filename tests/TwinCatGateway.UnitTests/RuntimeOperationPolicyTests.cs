using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class RuntimeOperationPolicyTests
{
    [Theory]
    [InlineData(RuntimeMode.Unknown)]
    [InlineData(RuntimeMode.Config)]
    [InlineData(RuntimeMode.Run)]
    [InlineData(RuntimeMode.Stop)]
    public void BuildAllowsNonExceptionRuntime(RuntimeMode runtimeMode)
    {
        RuntimeOperationPolicy.EnsureBuildAllowed(runtimeMode);
    }

    [Fact]
    public void BuildRequiresExplicitRecoveryFromException()
    {
        GatewayOperationException exception = Assert.Throws<
            GatewayOperationException>(
            () => RuntimeOperationPolicy.EnsureBuildAllowed(
                RuntimeMode.Exception,
                "Exception Code 0xc0000005."));

        Assert.Equal(
            ErrorCodes.BuildBlockedByRuntimeException,
            exception.Code);
        Assert.True(exception.Retryable);
        Assert.Equal(
            "build.runtimePreflight",
            exception.Stage);
        Assert.Equal(
            "Exception Code 0xc0000005.",
            exception.Details);
    }

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
