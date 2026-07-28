namespace TwinCatGateway.Contracts;

public static class ErrorCodes
{
    public const string RequestInvalid = "REQUEST_INVALID";
    public const string MethodNotFound = "METHOD_NOT_FOUND";
    public const string GatewayNotRunning = "GATEWAY_NOT_RUNNING";
    public const string GatewayNotReady = "GATEWAY_NOT_READY";
    public const string GatewayConfigNotFound =
        "GATEWAY_CONFIG_NOT_FOUND";
    public const string GatewayConfigAmbiguous =
        "GATEWAY_CONFIG_AMBIGUOUS";
    public const string GatewayStartDisabled =
        "GATEWAY_START_DISABLED";
    public const string GatewayStartFailed =
        "GATEWAY_START_FAILED";
    public const string GatewayStartTimeout =
        "GATEWAY_START_TIMEOUT";
    public const string GatewayRunningDifferentProject =
        "GATEWAY_RUNNING_DIFFERENT_PROJECT";
    public const string OperationNotFound = "OPERATION_NOT_FOUND";
    public const string OperationNotCancellable = "OPERATION_NOT_CANCELLABLE";
    public const string OperationTimeout = "OPERATION_TIMEOUT";
    public const string OperationFailed = "OPERATION_FAILED";
    public const string ProfileNotFound = "PROFILE_NOT_FOUND";
    public const string ProfileInvalid = "PROFILE_INVALID";
    public const string ResourceNotFound = "RESOURCE_NOT_FOUND";
    public const string XaeNotFound = "XAE_NOT_FOUND";
    public const string XaeMultipleMatches = "XAE_MULTIPLE_MATCHES";
    public const string XaeProgIdNotRegistered = "XAE_PROGID_NOT_REGISTERED";
    public const string XaeLaunchFailed = "XAE_LAUNCH_FAILED";
    public const string XaeSilentModeFailed = "XAE_SILENT_MODE_FAILED";
    public const string XaeWorkspaceOwnershipFailed =
        "XAE_WORKSPACE_OWNERSHIP_FAILED";
    public const string SolutionNotFound = "SOLUTION_NOT_FOUND";
    public const string SolutionMismatch = "SOLUTION_MISMATCH";
    public const string SysManagerNotAvailable = "SYSMANAGER_NOT_AVAILABLE";
    public const string XaeBusy = "XAE_BUSY";
    public const string ComCallRejected = "COM_CALL_REJECTED";
    public const string ComCallTimeout = "COM_CALL_TIMEOUT";
    public const string BuildFailed = "BUILD_FAILED";
    public const string BuildConfigurationNotFound =
        "BUILD_CONFIGURATION_NOT_FOUND";
    public const string BuildConfigurationAmbiguous =
        "BUILD_CONFIGURATION_AMBIGUOUS";
    public const string BuildConfigurationFailed =
        "BUILD_CONFIGURATION_FAILED";
    public const string BuildResultInconsistent = "BUILD_RESULT_INCONSISTENT";
    public const string ExternalEditConflict = "EXTERNAL_EDIT_CONFLICT";
    public const string ExternalEditUnsupported =
        "EXTERNAL_EDIT_UNSUPPORTED";
    public const string ExternalEditSyncFailed =
        "EXTERNAL_EDIT_SYNC_FAILED";
    public const string PlcObjectInvalid =
        "PLC_OBJECT_INVALID";
    public const string ActivationNotAllowed = "ACTIVATION_NOT_ALLOWED";
    public const string ActivationTargetMismatch =
        "ACTIVATION_TARGET_MISMATCH";
    public const string RecentBuildRequired =
        "RECENT_BUILD_REQUIRED";
    public const string ConfigModeRequired = "CONFIG_MODE_REQUIRED";
    public const string ConfigModeRecoveryFailed =
        "CONFIG_MODE_RECOVERY_FAILED";
    public const string ActivateConfigurationFailed = "ACTIVATE_CONFIGURATION_FAILED";
    public const string TwinCatRestartFailed = "TWINCAT_RESTART_FAILED";
    public const string TwinCatStateUnknown = "TWINCAT_STATE_UNKNOWN";
    public const string TestAdsUnavailable = "TEST_ADS_UNAVAILABLE";
    public const string TestCompletionSymbolUnavailable = "TEST_COMPLETION_SYMBOL_UNAVAILABLE";
    public const string TestCompletionTimeout = "TEST_COMPLETION_TIMEOUT";
    public const string TestReportNotProduced = "TEST_REPORT_NOT_PRODUCED";
    public const string TestReportInvalid = "TEST_REPORT_INVALID";
    public const string TestFailed = "TEST_FAILED";
    public const string IpcVersionMismatch = "IPC_VERSION_MISMATCH";
}
