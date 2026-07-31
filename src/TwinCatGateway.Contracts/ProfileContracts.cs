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

public sealed class GatewayProcessControlConfiguration
{
    public bool AllowStart { get; set; } = true;

    public bool AllowShutdown { get; set; }
}

public sealed class GatewayLoggingConfiguration
{
    public string? Directory { get; set; }

    public GatewayLogLevel MinimumLevel { get; set; } =
        GatewayLogLevel.Information;

    public long FileSizeLimitBytes { get; set; } = 1024 * 1024;

    public int RetainedFileCountLimit { get; set; } = 10;

    public int RetentionDays { get; set; } = 14;
}

public sealed class GatewaySettingsConfiguration
{
    public string PipeName { get; set; } = "TwinCatAgentGateway";

    public GatewayProcessControlConfiguration ProcessControl { get; set; } =
        new();

    public GatewayLoggingConfiguration Logging { get; set; } = new();
}

public sealed class XaeWorkspaceConfiguration
{
    public bool AssumeAttachedSynchronized { get; set; } = true;

    public ExternalChangePolicy ExternalChangePolicy { get; set; } =
        ExternalChangePolicy.ReloadModified;

    public bool AutoSynchronizeBeforeOperation { get; set; } = true;
}

public sealed class XaeCapabilitiesConfiguration
{
    public bool Launch { get; set; } = true;

    public bool Close { get; set; }

    public bool Synchronize { get; set; } = true;

    public bool DiscardDirtyDocuments { get; set; }

    public bool Build { get; set; } = true;

    public bool Activate { get; set; }
}

public sealed class XaeProfileConfiguration
{
    public string Solution { get; set; } = string.Empty;

    public string? ProgId { get; set; }

    public string? Configuration { get; set; }

    public string? Platform { get; set; }

    public XaeWorkspaceConfiguration Workspace { get; set; } = new();

    public XaeCapabilitiesConfiguration Capabilities { get; set; } = new();
}

public sealed class TargetMonitoringConfiguration
{
    public int PollIntervalMilliseconds { get; set; } = 1000;

    public int ReadTimeoutMilliseconds { get; set; } = 500;
}

public sealed class TargetCapabilitiesConfiguration
{
    public bool Config { get; set; }

    public bool StartRestart { get; set; }

    public bool TcUnitVerification { get; set; }
}

public sealed class TargetProfileConfiguration
{
    public string? Name { get; set; }

    public string AmsNetId { get; set; } = string.Empty;

    public TargetMonitoringConfiguration Monitoring { get; set; } = new();

    public TargetCapabilitiesConfiguration Capabilities { get; set; } =
        new();

    public TcUnitProfile? TcUnit { get; set; }
}

public sealed class GatewayConfiguration
{
    public int SchemaVersion { get; set; } = 2;

    public string? DefaultProfile { get; set; }

    public GatewaySettingsConfiguration Gateway { get; set; } = new();

    public GatewayUiConfiguration Ui { get; set; } = new();

    public List<ProjectProfile> Profiles { get; set; } = new();
}

public sealed class ProjectProfile
{
    public string Name { get; set; } = string.Empty;

    public XaeProfileConfiguration Xae { get; set; } = new();

    public TargetProfileConfiguration? Target { get; set; }
}

public sealed class TcUnitProfile
{
    public string RuntimeId { get; set; } = string.Empty;

    public int AdsPort { get; set; }

    public string FinishedSymbol { get; set; } =
        "GVL_TcUnit.TcUnitRunner.AllTestSuitesFinished";

    public string SuiteCountSymbol { get; set; } =
        "GVL_TcUnit.NumberOfInitializedTestSuites";

    public string ReportPath { get; set; } = string.Empty;

    public bool AllowDeleteExistingReport { get; set; }

    public int CompletionTimeoutSeconds { get; set; } = 120;

    public ZeroTestsPolicy ZeroTests { get; set; } = ZeroTestsPolicy.Fail;
}
