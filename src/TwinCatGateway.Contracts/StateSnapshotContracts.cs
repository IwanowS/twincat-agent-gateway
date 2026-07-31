using System;
using System.Collections.Generic;

namespace TwinCatGateway.Contracts;

public enum GatewayProcessState
{
    Starting,
    Ready,
    Busy,
    Faulted,
    Stopping,
    Unavailable,
}

public enum GatewayComponent
{
    Gateway,
    Profile,
    Xae,
    Target,
    Plc,
    Verification,
}

public enum ObservationSource
{
    Xae,
    SystemService,
    Plc,
}

public enum ObservationFreshness
{
    Fresh,
    Stale,
    Unavailable,
    Unknown,
}

public enum TargetSystemState
{
    Config,
    Run,
    Stop,
    Exception,
    Transitioning,
    Unknown,
}

public enum PlcRuntimeState
{
    Run,
    Stop,
    Reset,
    Exception,
    Transitioning,
    Unknown,
}

public enum XaeProcessOwnership
{
    GatewayLaunched,
    Attached,
    Unknown,
}

public enum SourceDiscoveryState
{
    Confirmed,
    Stale,
    Incomplete,
    Unavailable,
    Unknown,
}

public enum SourceEntryKind
{
    Editable,
    Generated,
    Unsupported,
}

public enum CapabilityKey
{
    GatewayStart,
    GatewayShutdown,
    XaeLaunch,
    XaeClose,
    XaeSynchronize,
    XaeDiscardDirtyDocuments,
    XaeBuild,
    XaeActivate,
    TargetConfig,
    TargetStartRestart,
    TargetTcUnitVerification,
}

public enum CapabilityDenialReason
{
    None,
    CapabilityDisabled,
    OperatorLocked,
    XaeCloseConsentRequired,
}

public sealed class ObservationError
{
    public string Code { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public bool Retryable { get; set; }
}

public sealed class GatewayStateSnapshot
{
    public GatewayProcessState State { get; set; } =
        GatewayProcessState.Unavailable;

    public string Version { get; set; } = string.Empty;

    public string? ConfigurationPath { get; set; }

    public string? ActiveProfile { get; set; }

    public string? CurrentOperationId { get; set; }

    public string JournalId { get; set; } = string.Empty;

    public long LatestEventCursor { get; set; }

    public DateTimeOffset ObservedAtUtc { get; set; }

    public ObservationError? Error { get; set; }
}

public sealed class XaeSessionSnapshot
{
    public string Profile { get; set; } = string.Empty;

    public int? ProcessId { get; set; }

    public XaeProcessOwnership Ownership { get; set; } =
        XaeProcessOwnership.Unknown;

    public bool DteAvailable { get; set; }

    public string? ProgId { get; set; }

    public string? Version { get; set; }

    public string? Solution { get; set; }

    public bool SolutionLoaded { get; set; }

    public string? ActiveConfiguration { get; set; }

    public string? ActivePlatform { get; set; }

    public string? ActiveProjectVariant { get; set; }

    public SynchronizationState SynchronizationState { get; set; } =
        SynchronizationState.Uninitialized;

    public List<string> DirtyDocuments { get; set; } = new();

    public string? CurrentOperationId { get; set; }

    public List<string> Dialogs { get; set; } = new();

    public List<string> Errors { get; set; } = new();

    public XaeTwinCatSystemObservation? TwinCatSystem { get; set; }

    public DateTimeOffset ObservedAtUtc { get; set; }
}

public sealed class XaeTwinCatSystemObservation
{
    public ObservationSource Source { get; set; } = ObservationSource.Xae;

    public TargetSystemState State { get; set; } = TargetSystemState.Unknown;

    public string? RawState { get; set; }

    public string? SelectedTarget { get; set; }

    public DateTimeOffset ObservedAtUtc { get; set; }

    public ObservationFreshness Freshness { get; set; } =
        ObservationFreshness.Unknown;

    public ObservationError? Error { get; set; }
}

public sealed class TargetSystemObservation
{
    public ObservationSource Source { get; set; } =
        ObservationSource.SystemService;

    public string Profile { get; set; } = string.Empty;

    public string AmsNetId { get; set; } = string.Empty;

    public int Port { get; set; } = 10000;

    public int? RawAdsState { get; set; }

    public string? RawAdsStateName { get; set; }

    public int? RawDeviceState { get; set; }

    public TargetSystemState State { get; set; } = TargetSystemState.Unknown;

    public DateTimeOffset ObservedAtUtc { get; set; }

    public ObservationFreshness Freshness { get; set; } =
        ObservationFreshness.Unknown;

    public ObservationError? Error { get; set; }

    public List<ResourceReference> PlcRuntimeResources { get; set; } = new();
}

public sealed class PlcRuntimeObservation
{
    public ObservationSource Source { get; set; } = ObservationSource.Plc;

    public string Profile { get; set; } = string.Empty;

    public string RuntimeId { get; set; } = string.Empty;

    public string? Project { get; set; }

    public string? Instance { get; set; }

    public string AmsNetId { get; set; } = string.Empty;

    public int Port { get; set; }

    public int? RawAdsState { get; set; }

    public string? RawAdsStateName { get; set; }

    public int? RawDeviceState { get; set; }

    public PlcRuntimeState State { get; set; } = PlcRuntimeState.Unknown;

    public DateTimeOffset ObservedAtUtc { get; set; }

    public ObservationFreshness Freshness { get; set; } =
        ObservationFreshness.Unknown;

    public ObservationError? Error { get; set; }
}

public sealed class CapabilityState
{
    public CapabilityKey Key { get; set; }

    public bool Configured { get; set; }

    public bool? SessionConsented { get; set; }

    public bool OperatorLocked { get; set; }

    public bool Effective { get; set; }

    public CapabilityDenialReason Reason { get; set; }
}

public sealed class SourceManifest
{
    public string Profile { get; set; } = string.Empty;

    public SourceDiscoveryState DiscoveryState { get; set; } =
        SourceDiscoveryState.Unknown;

    public string SolutionDirectory { get; set; } = string.Empty;

    public List<SourceRootEntry> Roots { get; set; } = new();

    public int FileCount { get; set; }

    public string FilesRef { get; set; } = string.Empty;

    public DateTimeOffset ObservedAtUtc { get; set; }

    public ObservationError? Error { get; set; }
}

public sealed class SourceRootEntry
{
    public string Path { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public string Project { get; set; } = string.Empty;

    public string ProjectFile { get; set; } = string.Empty;

    public bool Exists { get; set; }

    public bool OutsideSolutionDirectory { get; set; }

    public List<string> Extensions { get; set; } = new();
}

public sealed class SourceFileEntry
{
    public string Path { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public string Project { get; set; } = string.Empty;

    public bool Exists { get; set; }

    public bool OutsideSolutionDirectory { get; set; }

    public SourceEntryKind Kind { get; set; }
}
