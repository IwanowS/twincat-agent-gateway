using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Desktop;

internal sealed class XaeErrorListSnapshotStore
{
    private BuildDiagnostic[] _snapshot =
        Array.Empty<BuildDiagnostic>();

    public IReadOnlyList<BuildDiagnostic> Read()
    {
        return Volatile.Read(ref _snapshot)
            .Select(Clone)
            .ToArray();
    }

    public void Replace(
        IEnumerable<BuildDiagnostic> diagnostics)
    {
        if (diagnostics is null)
        {
            throw new ArgumentNullException(
                nameof(diagnostics));
        }

        Interlocked.Exchange(
            ref _snapshot,
            diagnostics.Select(Clone).ToArray());
    }

    private static BuildDiagnostic Clone(
        BuildDiagnostic source)
    {
        return new BuildDiagnostic
        {
            Severity = source.Severity,
            Source = source.Source,
            Code = source.Code,
            Message = source.Message,
            File = source.File,
            Line = source.Line,
            Column = source.Column,
        };
    }
}
