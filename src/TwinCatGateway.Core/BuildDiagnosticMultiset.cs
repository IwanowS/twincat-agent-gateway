using System;
using System.Collections.Generic;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

internal static class BuildDiagnosticMultiset
{
    public static IReadOnlyList<BuildDiagnostic> Except(
        IEnumerable<BuildDiagnostic> current,
        IEnumerable<BuildDiagnostic> baseline)
    {
        if (current is null)
        {
            throw new ArgumentNullException(nameof(current));
        }

        if (baseline is null)
        {
            throw new ArgumentNullException(nameof(baseline));
        }

        Dictionary<Key, int> remainingBaseline = new();
        foreach (BuildDiagnostic diagnostic in baseline)
        {
            Key key = new(diagnostic);
            remainingBaseline.TryGetValue(
                key,
                out int count);
            remainingBaseline[key] = count + 1;
        }

        List<BuildDiagnostic> difference = new();
        foreach (BuildDiagnostic diagnostic in current)
        {
            Key key = new(diagnostic);
            if (remainingBaseline.TryGetValue(
                    key,
                    out int count)
                && count > 0)
            {
                if (count == 1)
                {
                    remainingBaseline.Remove(key);
                }
                else
                {
                    remainingBaseline[key] = count - 1;
                }

                continue;
            }

            difference.Add(diagnostic);
        }

        return difference;
    }

    private readonly struct Key : IEquatable<Key>
    {
        private readonly DiagnosticSeverity _severity;
        private readonly string _source;
        private readonly string? _code;
        private readonly string _message;
        private readonly string? _file;
        private readonly int? _line;
        private readonly int? _column;

        public Key(BuildDiagnostic diagnostic)
        {
            if (diagnostic is null)
            {
                throw new ArgumentNullException(
                    nameof(diagnostic));
            }

            _severity = diagnostic.Severity;
            _source = diagnostic.Source ?? string.Empty;
            _code = diagnostic.Code;
            _message = diagnostic.Message ?? string.Empty;
            _file = diagnostic.File;
            _line = diagnostic.Line;
            _column = diagnostic.Column;
        }

        public bool Equals(Key other)
        {
            return _severity == other._severity
                && string.Equals(
                    _source,
                    other._source,
                    StringComparison.Ordinal)
                && string.Equals(
                    _code,
                    other._code,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    _message,
                    other._message,
                    StringComparison.Ordinal)
                && string.Equals(
                    _file,
                    other._file,
                    StringComparison.OrdinalIgnoreCase)
                && _line == other._line
                && _column == other._column;
        }

        public override bool Equals(object? obj)
        {
            return obj is Key other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)_severity;
                hash = (hash * 397)
                    ^ StringComparer.Ordinal.GetHashCode(
                        _source);
                hash = (hash * 397)
                    ^ (_code is null
                        ? 0
                        : StringComparer.OrdinalIgnoreCase
                            .GetHashCode(_code));
                hash = (hash * 397)
                    ^ StringComparer.Ordinal.GetHashCode(
                        _message);
                hash = (hash * 397)
                    ^ (_file is null
                        ? 0
                        : StringComparer.OrdinalIgnoreCase
                            .GetHashCode(_file));
                hash = (hash * 397)
                    ^ _line.GetHashCode();
                hash = (hash * 397)
                    ^ _column.GetHashCode();
                return hash;
            }
        }
    }
}
