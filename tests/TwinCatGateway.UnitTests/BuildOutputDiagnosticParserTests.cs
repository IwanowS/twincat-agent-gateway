using System.Linq;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class BuildOutputDiagnosticParserTests
{
    [Fact]
    public void ParsesObservedTwinCatCompilerError()
    {
        const string path =
            @"C:\work\TC3_SimpleProject\PlcProject1\POUs\MAIN.TcPOU";
        string output =
            "typify code ...\r\n"
            + path
            + "(7) : error: Expression expected instead of ';'\r\n"
            + "Compile complete -- 1 errors, 0 warnings\r\n";

        BuildDiagnostic diagnostic = Assert.Single(
            BuildOutputDiagnosticParser.Parse(output));

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("plc-compiler", diagnostic.Source);
        Assert.Null(diagnostic.Code);
        Assert.Equal(
            "Expression expected instead of ';'",
            diagnostic.Message);
        Assert.Equal(path, diagnostic.File);
        Assert.Equal(7, diagnostic.Line);
        Assert.Null(diagnostic.Column);
    }

    [Fact]
    public void ParsesOptionalColumnAndCompilerCode()
    {
        const string output =
            @"C:\work\MAIN.TcPOU(12,4): warning C0123: Check value";

        BuildDiagnostic diagnostic = Assert.Single(
            BuildOutputDiagnosticParser.Parse(output));

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("C0123", diagnostic.Code);
        Assert.Equal(12, diagnostic.Line);
        Assert.Equal(4, diagnostic.Column);
    }

    [Fact]
    public void IgnoresBuildSummaryAndInfrastructureText()
    {
        const string output =
            "Build complete -- 1 errors, 0 warnings\r\n"
            + "Error: The operation could not be completed.\r\n";

        Assert.Empty(
            BuildOutputDiagnosticParser.Parse(output));
    }
}
