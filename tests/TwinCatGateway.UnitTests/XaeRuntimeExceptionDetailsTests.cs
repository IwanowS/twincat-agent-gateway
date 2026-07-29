using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class XaeRuntimeExceptionDetailsTests
{
    [Fact]
    public void SelectsExceptionMessageForRuntimeAndPort()
    {
        string? details =
            XaeRuntimeExceptionDetails.Select(
                new[]
                {
                    Diagnostic(
                        DiagnosticSeverity.Error,
                        "Exception Code 0xc0000005 in PlcProject1 on port 851."),
                    Diagnostic(
                        DiagnosticSeverity.Error,
                        "Page Fault in PlcProject2 on ADS port 852."),
                },
                runtimeName: "PlcProject2",
                adsPort: 852);

        Assert.Equal(
            "Page Fault in PlcProject2 on ADS port 852.",
            details);
    }

    [Fact]
    public void IgnoresWarningsAndUnrelatedErrors()
    {
        string? details =
            XaeRuntimeExceptionDetails.Select(
                new[]
                {
                    Diagnostic(
                        DiagnosticSeverity.Warning,
                        "Exception Code 0xc0000005."),
                    Diagnostic(
                        DiagnosticSeverity.Error,
                        "PLC compilation failed."),
                });

        Assert.Null(details);
    }

    private static BuildDiagnostic Diagnostic(
        DiagnosticSeverity severity,
        string message)
    {
        return new BuildDiagnostic
        {
            Severity = severity,
            Source = "xae-error-list",
            Message = message,
        };
    }
}
