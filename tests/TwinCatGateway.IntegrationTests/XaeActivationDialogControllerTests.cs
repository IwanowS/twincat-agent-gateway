using TwinCatGateway.Xae;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class XaeActivationDialogControllerTests
{
    [Theory]
    [InlineData(
        "TcXaeShell",
        "Active solution platform 'TwinCAT OS (ARMT2)' differs from "
            + "current target platform 'TwinCAT RT (x64)'!",
        "PlatformMismatch")]
    [InlineData(
        "Activate Configuration",
        "Project: Machine Target: Bench Autostart PLC Boot Project(s)",
        "ActivationConfirmation")]
    [InlineData(
        "TcXaeShell",
        "Restart TwinCAT System in Run Mode",
        "RunConfirmation")]
    [InlineData(
        "Target system reports a fatal error",
        "AdsError: 1804",
        "FatalError")]
    [InlineData(
        "Unexpected",
        "Unknown prompt",
        "Unknown")]
    public void ClassifiesActivationDialogs(
        string title,
        string text,
        string expected)
    {
        Assert.Equal(
            expected,
            XaeActivationDialogController.ClassifyDialog(
                title,
                text).ToString());
    }
}
