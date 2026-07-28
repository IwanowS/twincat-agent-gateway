using System.Collections.Generic;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Xae;

public sealed class XaeSessionSnapshot
{
    public bool Connected { get; set; }

    public DteInstanceInfo? SelectedInstance { get; set; }

    public bool SysManagerAvailable { get; set; }

    public bool LaunchedByGateway { get; set; }

    public bool AgentWorkspaceOwned { get; set; }

    public int ClosedDocumentCount { get; set; }

    public int DiscardedDocumentCount { get; set; }

    public SynchronizationState SynchronizationState { get; set; } =
        SynchronizationState.Uninitialized;

    public int DirtyDocumentCount { get; set; }

    public string? ActiveConfiguration { get; set; }

    public string? ActivePlatform { get; set; }

    public string? TargetAmsNetId { get; set; }

    public IReadOnlyList<string> LastErrorMessages { get; set; } =
        new List<string>();

    public IReadOnlyList<string> DiagnosticIssues { get; set; } =
        new List<string>();

    public IReadOnlyList<DteInstanceInfo> DiscoveredInstances { get; set; } =
        new List<DteInstanceInfo>();
}
