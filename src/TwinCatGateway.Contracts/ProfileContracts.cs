using System.Collections.Generic;

namespace TwinCatGateway.Contracts;

public enum ZeroTestsPolicy
{
    Fail,
    Warn,
    Allow,
}

public enum GatewayUiMode
{
    Auto,
    Window,
    Tray,
}

public enum GatewayLaunchSource
{
    Manual,
    Agent,
}

public enum GatewayLogLevel
{
    Verbose,
    Debug,
    Information,
    Warning,
    Error,
    Fatal,
}

public enum ExternalChangePolicy
{
    ReloadAll,
    ReloadModified,
    Error,
}

public sealed class GatewayUiConfiguration
{
    public GatewayUiMode Mode { get; set; } = GatewayUiMode.Auto;
}

public sealed class AgentProcessControlConfiguration
{
    public bool AllowStart { get; set; } = true;

    public bool AllowShutdown { get; set; }
}

public sealed class RuntimeMonitoringConfiguration
{
    public int PollIntervalMilliseconds { get; set; } = 1000;

    public int ReadTimeoutMilliseconds { get; set; } = 500;
}

public sealed class GatewayConfiguration
{
    public int SchemaVersion { get; set; } = 1;

    public string PipeName { get; set; } = "TwinCatAgentGateway";

    public string? DefaultProfile { get; set; }

    public string? LogDirectory { get; set; }

    public GatewayLogLevel LogMinimumLevel { get; set; } =
        GatewayLogLevel.Information;

    public long LogFileSizeLimitBytes { get; set; } = 1024 * 1024;

    public int LogRetainedFileCountLimit { get; set; } = 10;

    public int LogRetentionDays { get; set; } = 14;

    public GatewayUiConfiguration Ui { get; set; } = new();

    public AgentProcessControlConfiguration AgentProcessControl { get; set; } =
        new();

    public RuntimeMonitoringConfiguration RuntimeMonitoring { get; set; } =
        new();

    public List<ProjectProfile> Profiles { get; set; } = new();
}

public sealed class ProjectProfile
{
    public string Name { get; set; } = string.Empty;

    public string Solution { get; set; } = string.Empty;

    public bool AllowXaeLaunch { get; set; } = true;

    public string? XaeProgId { get; set; }

    public bool AllowActivation { get; set; }

    public TargetIdentity? ExpectedTarget { get; set; }

    public string? Configuration { get; set; }

    public string? Platform { get; set; }

    public bool AssumeAttachedXaeSynchronized { get; set; } = true;

    public ExternalChangePolicy ExternalChangePolicy { get; set; } =
        ExternalChangePolicy.ReloadModified;

    public bool AllowAgentForceSynchronization { get; set; }

    public bool AllowDirtyDocumentDiscard { get; set; }

    public bool AutoSynchronizeBeforeOperation { get; set; } = true;

    public bool RequireRecentSuccessfulBuild { get; set; } = true;

    public int RecentBuildMaxAgeSeconds { get; set; } = 600;

    public bool AutoWaitForTcUnit { get; set; }

    public TcUnitProfile? TcUnit { get; set; }
}

public sealed class TcUnitProfile
{
    public int AdsPort { get; set; } = 851;

    public string FinishedSymbol { get; set; } =
        "GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished";

    public string SuiteCountSymbol { get; set; } =
        "GVL_TcUnit.NumberOfInitializedTestSuites";

    public string ReportPath { get; set; } = string.Empty;

    public bool AllowDeleteExistingReport { get; set; }

    public int CompletionTimeoutSeconds { get; set; } = 120;

    public ZeroTestsPolicy ZeroTests { get; set; } = ZeroTestsPolicy.Fail;
}
