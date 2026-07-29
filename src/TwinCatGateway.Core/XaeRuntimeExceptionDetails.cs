using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public static class XaeRuntimeExceptionDetails
{
    public static string? Select(
        IEnumerable<BuildDiagnostic> diagnostics,
        string? runtimeName = null,
        int? adsPort = null)
    {
        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        string? port = adsPort?.ToString(
            CultureInfo.InvariantCulture);
        return diagnostics
            .Select(
                (diagnostic, index) =>
                    new Candidate(
                        diagnostic,
                        index,
                        Score(
                            diagnostic,
                            runtimeName,
                            port)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Index)
            .Select(candidate => candidate.Diagnostic.Message)
            .FirstOrDefault();
    }

    private static int Score(
        BuildDiagnostic diagnostic,
        string? runtimeName,
        string? adsPort)
    {
        if (diagnostic.Severity != DiagnosticSeverity.Error
            || string.IsNullOrWhiteSpace(diagnostic.Message))
        {
            return 0;
        }

        string message = diagnostic.Message;
        int score = 0;
        if (Contains(message, "exception code"))
        {
            score += 8;
        }
        else if (Contains(message, "exception"))
        {
            score += 4;
        }

        if (Contains(message, "page fault"))
        {
            score += 8;
        }

        if (!string.IsNullOrWhiteSpace(runtimeName)
            && Contains(message, runtimeName!))
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(adsPort)
            && Contains(message, adsPort!))
        {
            score += 1;
        }

        return score;
    }

    private static bool Contains(string value, string expected)
    {
        return value.IndexOf(
                expected,
                StringComparison.OrdinalIgnoreCase)
            >= 0;
    }

    private sealed class Candidate
    {
        public Candidate(
            BuildDiagnostic diagnostic,
            int index,
            int score)
        {
            Diagnostic = diagnostic;
            Index = index;
            Score = score;
        }

        public BuildDiagnostic Diagnostic { get; }

        public int Index { get; }

        public int Score { get; }
    }
}
