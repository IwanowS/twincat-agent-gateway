using System.Collections.Generic;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class BuildDiagnosticMultisetTests
{
    [Fact]
    public void ExcludesDiagnosticsAlreadyPresentBeforeBuild()
    {
        BuildDiagnostic runtimeMessage = Diagnostic(
            "Tests: 1");
        BuildDiagnostic compileError = Diagnostic(
            "Expression expected",
            file: @"C:\Project\MAIN.TcPOU",
            line: 7);

        IReadOnlyList<BuildDiagnostic> result =
            BuildDiagnosticMultiset.Except(
                new[]
                {
                    runtimeMessage,
                    compileError,
                },
                new[]
                {
                    runtimeMessage,
                });

        Assert.Same(
            compileError,
            Assert.Single(result));
    }

    [Fact]
    public void PreservesAdditionalIdenticalOccurrence()
    {
        BuildDiagnostic first = Diagnostic(
            "Duplicate diagnostic");
        BuildDiagnostic second = Diagnostic(
            "Duplicate diagnostic");

        IReadOnlyList<BuildDiagnostic> result =
            BuildDiagnosticMultiset.Except(
                new[]
                {
                    first,
                    second,
                },
                new[]
                {
                    Diagnostic("Duplicate diagnostic"),
                });

        Assert.Same(second, Assert.Single(result));
    }

    [Fact]
    public void TreatsChangedLocationAsCurrentDiagnostic()
    {
        BuildDiagnostic current = Diagnostic(
            "Expression expected",
            file: @"C:\Project\MAIN.TcPOU",
            line: 8);

        IReadOnlyList<BuildDiagnostic> result =
            BuildDiagnosticMultiset.Except(
                new[]
                {
                    current,
                },
                new[]
                {
                    Diagnostic(
                        "Expression expected",
                        file: @"C:\Project\MAIN.TcPOU",
                        line: 7),
                });

        Assert.Same(current, Assert.Single(result));
    }

    private static BuildDiagnostic Diagnostic(
        string message,
        string? file = null,
        int? line = null)
    {
        return new BuildDiagnostic
        {
            Severity = DiagnosticSeverity.Error,
            Source = "xae-error-list",
            Message = message,
            File = file,
            Line = line,
        };
    }
}
