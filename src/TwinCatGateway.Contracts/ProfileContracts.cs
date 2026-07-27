using System.Collections.Generic;

namespace TwinCatGateway.Contracts;

public enum ZeroTestsPolicy
{
    Fail,
    Warn,
    Allow,
}

public enum UnsavedDocumentPolicy
{
    SaveAll,
    Reject,
}

public sealed class GatewayConfiguration
{
    public int SchemaVersion { get; set; } = 1;

    public string PipeName { get; set; } = "TwinCatAgentGateway";

    public string? DefaultProfile { get; set; }

    public string? LogDirectory { get; set; }

    public int LogRetentionDays { get; set; } = 14;

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

    public UnsavedDocumentPolicy UnsavedDocuments { get; set; } =
        UnsavedDocumentPolicy.SaveAll;

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
