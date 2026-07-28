using System;
using System.Collections.Generic;

namespace TwinCatGateway.Contracts;

public sealed class HealthResult
{
    public string Version { get; set; } = string.Empty;

    public GatewayState State { get; set; }

    public bool Ready { get; set; }
}

public sealed class GatewayStatus
{
    public GatewayState State { get; set; }

    public string Version { get; set; } = string.Empty;

    public bool Ready { get; set; }

    public string? ConfigurationPath { get; set; }

    public string? ActiveProfile { get; set; }

    public string? SolutionPath { get; set; }

    public GatewayLaunchSource LaunchSource { get; set; } =
        GatewayLaunchSource.Manual;

    public GatewayUiMode UiMode { get; set; } = GatewayUiMode.Auto;
}

public sealed class XaeStatus
{
    public bool Connected { get; set; }

    public string? Version { get; set; }

    public string? Solution { get; set; }

    public bool AgentWorkspaceOwned { get; set; }

    public int DiscardedDocumentCount { get; set; }
}

public sealed class TwinCatStatus
{
    public bool? Started { get; set; }

    public RuntimeMode Mode { get; set; } = RuntimeMode.Unknown;
}

public sealed class GatewayStatusResult
{
    public GatewayStatus Gateway { get; set; } = new();

    public XaeStatus Xae { get; set; } = new();

    public TwinCatStatus TwinCat { get; set; } = new();

    public OperationSummary? CurrentOperation { get; set; }

    public BuildSummary? LastBuild { get; set; }

    public ActivationSummary? LastActivation { get; set; }

    public TestSummary? LastTest { get; set; }

    public string EventStreamId { get; set; } = string.Empty;

    public long LatestEventCursor { get; set; }
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
}

public sealed class AdsRuntimeDiagnostics
{
    public string? AmsNetId { get; set; }

    public int Port { get; set; } = 10000;

    public string? AdsState { get; set; }

    public short? DeviceState { get; set; }

    public string? ErrorCode { get; set; }

    public DateTimeOffset? ReadAtUtc { get; set; }
}

public sealed class ComDiagnostics
{
    public long RejectedCallCount { get; set; }

    public long RetryCount { get; set; }

    public long LastCallLatencyMs { get; set; }

    public int? LastHResult { get; set; }
}

public sealed class GatewayDiagnosticsResult
{
    public GatewayStatusResult Status { get; set; } = new();

    public List<DteInstanceInfo> DteInstances { get; set; } = new();

    public XaeDiagnostics Xae { get; set; } = new();

    public AdsRuntimeDiagnostics Runtime { get; set; } = new();

    public ComDiagnostics Com { get; set; } = new();

    public ComponentHealth Ipc { get; set; } = new();

    public ComponentHealth LogStore { get; set; } = new();

    public List<ResourceReference> Resources { get; set; } = new();

    public List<GatewayEvent> Events { get; set; } = new();

    public string EventStreamId { get; set; } = string.Empty;

    public long NextScanCursor { get; set; }

    public long LatestEventCursor { get; set; }

    public bool MoreMatchingEventsAvailable { get; set; }

    public bool EventHistoryTruncated { get; set; }
}
