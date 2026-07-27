using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public static class BuildOutputDiagnosticParser
{
    private static readonly Regex DiagnosticLine = new(
        @"^(?<file>.+)\((?<line>\d+)(?:,(?<column>\d+))?\)"
        + @"\s*:\s*(?<severity>error|warning)"
        + @"(?:\s+(?<code>[A-Za-z]+\d+))?\s*:\s*(?<message>.+)$",
        RegexOptions.Compiled
        | RegexOptions.CultureInvariant
        | RegexOptions.IgnoreCase);

    public static IReadOnlyList<BuildDiagnostic> Parse(string output)
    {
        if (output is null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        List<BuildDiagnostic> diagnostics = new();
        using StringReader reader = new(output);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            Match match = DiagnosticLine.Match(line);
            if (!match.Success)
            {
                continue;
            }

            diagnostics.Add(
                new BuildDiagnostic
                {
                    Severity = string.Equals(
                        match.Groups["severity"].Value,
                        "error",
                        StringComparison.OrdinalIgnoreCase)
                            ? DiagnosticSeverity.Error
                            : DiagnosticSeverity.Warning,
                    Source = "plc-compiler",
                    Code = EmptyToNull(
                        match.Groups["code"].Value),
                    Message = match.Groups["message"].Value.Trim(),
                    File = match.Groups["file"].Value.Trim(),
                    Line = ParsePositive(
                        match.Groups["line"].Value),
                    Column = ParsePositive(
                        match.Groups["column"].Value),
                });
        }

        return diagnostics;
    }

    private static int? ParsePositive(string value)
    {
        return int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int result)
            && result > 0
                ? result
                : null;
    }

    private static string? EmptyToNull(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value;
    }
}
