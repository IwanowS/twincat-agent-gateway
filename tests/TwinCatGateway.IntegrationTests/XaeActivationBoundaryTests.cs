using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Xae;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class XaeActivationBoundaryTests
{
    [Fact]
    public async Task ActivationCommandRequiresAttachedSession()
    {
        using XaeSession session = new();

        GatewayOperationException exception =
            await Assert.ThrowsAsync<GatewayOperationException>(
                () => session.ActivateConfigurationAsync(
                    @"C:\missing\Machine.sln",
                    "192.168.3.31.1.1",
                    TimeSpan.FromSeconds(2),
                    CancellationToken.None));

        Assert.Equal(ErrorCodes.XaeNotFound, exception.Code);
    }

    [XaeFact]
    public async Task StateChangingCommandsRejectMismatchedTarget()
    {
        string solution = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                "TWINCAT_GATEWAY_XAE_SOLUTION")!);
        using XaeSession session = new();
        await session.AttachAsync(
            solution,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);
        const string wrongTarget = "127.0.0.1.1.1";

        GatewayOperationException activate =
            await Assert.ThrowsAsync<GatewayOperationException>(
                () => session.ActivateConfigurationAsync(
                    solution,
                    wrongTarget,
                    TimeSpan.FromSeconds(10),
                    CancellationToken.None));
        GatewayOperationException restart =
            await Assert.ThrowsAsync<GatewayOperationException>(
                () => session.StartRestartTwinCatAsync(
                    solution,
                    wrongTarget,
                    TimeSpan.FromSeconds(10),
                    CancellationToken.None));
        GatewayOperationException recover =
            await Assert.ThrowsAsync<GatewayOperationException>(
                () => session.RestartTwinCatConfigModeAsync(
                    solution,
                    wrongTarget,
                    TimeSpan.FromSeconds(10),
                    CancellationToken.None));

        Assert.Equal(
            ErrorCodes.ActivationTargetMismatch,
            activate.Code);
        Assert.Equal(
            ErrorCodes.ActivationTargetMismatch,
            restart.Code);
        Assert.Equal(
            ErrorCodes.ActivationTargetMismatch,
            recover.Code);
    }
}
