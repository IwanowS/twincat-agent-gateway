using System.Collections.Generic;

namespace TwinCatGateway.Contracts;

public enum SynchronizationState
{
    Uninitialized,
    SyncRequired,
    Synchronizing,
    Confirmed,
}

public sealed class GetDiagnosticsParameters
{
    public string? EventStreamId { get; set; }

    public long AfterEventCursor { get; set; }

    public int MaximumEvents { get; set; } = 100;

    public DiagnosticSeverity? MinimumSeverity { get; set; }
}

public sealed class DteInstanceInfo
{
    public string Moniker { get; set; } = string.Empty;

    public string? ProgId { get; set; }

    public int? ProcessId { get; set; }

    public string? Version { get; set; }

    public string? Solution { get; set; }

    public bool SolutionLoaded { get; set; }

    public bool Selected { get; set; }

    public string? SelectionReason { get; set; }

    public string? InspectionError { get; set; }

    public int? InspectionHResult { get; set; }
}

public sealed class XaeDiagnostics
{
    public bool SysManagerAvailable { get; set; }

    public string? ActiveConfiguration { get; set; }

    public string? ActivePlatform { get; set; }

    public TargetIdentity? Target { get; set; }

    public List<string> LastErrorMessages { get; set; } = new();

    public List<string> InspectionIssues { get; set; } = new();

    public int? LastHResult { get; set; }

    public List<UnsynchronizedFileInfo> UnsynchronizedFiles { get; set; } =
        new();
}

public sealed class UnsynchronizedFileInfo
{
    public string Path { get; set; } = string.Empty;

    public SynchronizationChangeKind ChangeKind { get; set; }

    public SynchronizationFileRole Role { get; set; }
}

public enum SynchronizationChangeKind
{
    Added,
    Modified,
    Deleted,
}

public enum SynchronizationFileRole
{
    TwinCatProject,
    PlcProject,
    PlcSource,
}

public sealed class ComDiagnostics
{
    public long RejectedCallCount { get; set; }

    public long RetryCount { get; set; }

    public long LastCallLatencyMs { get; set; }

    public int? LastHResult { get; set; }
}
