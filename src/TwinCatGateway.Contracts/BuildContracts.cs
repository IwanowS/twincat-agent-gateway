using System.Collections.Generic;

namespace TwinCatGateway.Contracts;

public enum BuildAction
{
    Build,
    Rebuild,
    Clean,
}

public enum ProjectChangeClassification
{
    ExpectedReorderOnly,
    WhitespaceOnly,
    ContentChanged,
    Unknown,
}

public sealed class BuildParameters
{
    public string Profile { get; set; } = string.Empty;

    public BuildAction Action { get; set; } = BuildAction.Rebuild;

    public string? Configuration { get; set; }

    public string? Platform { get; set; }

    public DetailLevel Detail { get; set; } = DetailLevel.Compact;

    public int? TimeoutSeconds { get; set; }
}

public sealed class BuildDiagnostic
{
    public DiagnosticSeverity Severity { get; set; }

    public string Source { get; set; } = string.Empty;

    public string? Code { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? File { get; set; }

    public int? Line { get; set; }

    public int? Column { get; set; }
}

public sealed class DiagnosticCounts
{
    public int Errors { get; set; }

    public int Warnings { get; set; }
}

public sealed class ProjectChangeSummary
{
    public string File { get; set; } = string.Empty;

    public ProjectChangeClassification Classification { get; set; }

    public int MovedBlocks { get; set; }

    public int ContentChanges { get; set; }

    public bool DoNotInspectFullFile { get; set; }

    public ResourceReference? Details { get; set; }
}

public sealed class BuildResult
{
    public bool Ok { get; set; }

    public string OperationId { get; set; } = string.Empty;

    public BuildAction Action { get; set; }

    public long DurationMs { get; set; }

    public DiagnosticCounts Counts { get; set; } = new();

    public List<BuildDiagnostic> Diagnostics { get; set; } = new();

    public int MoreDiagnostics { get; set; }

    public List<ProjectChangeSummary> ExpectedProjectNoise { get; set; } = new();

    public ResourceReference? Log { get; set; }
}

public sealed class BuildSummary
{
    public bool Ok { get; set; }

    public string OperationId { get; set; } = string.Empty;

    public BuildAction Action { get; set; }

    public int Errors { get; set; }

    public int Warnings { get; set; }
}
