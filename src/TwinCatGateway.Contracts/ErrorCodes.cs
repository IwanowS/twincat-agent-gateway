namespace TwinCatGateway.Contracts;

public static class ErrorCodes
{
    public const string RequestInvalid = "REQUEST_INVALID";
    public const string MethodNotFound = "METHOD_NOT_FOUND";
    public const string GatewayNotReady = "GATEWAY_NOT_READY";
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
    public const string ComCallRejected = "COM_CALL_REJECTED";
    public const string ComCallTimeout = "COM_CALL_TIMEOUT";
    public const string BuildFailed = "BUILD_FAILED";
    public const string BuildResultInconsistent = "BUILD_RESULT_INCONSISTENT";
    public const string ExternalEditConflict = "EXTERNAL_EDIT_CONFLICT";
    public const string ActivationNotAllowed = "ACTIVATION_NOT_ALLOWED";
    public const string ConfigModeRequired = "CONFIG_MODE_REQUIRED";
    public const string ActivateConfigurationFailed = "ACTIVATE_CONFIGURATION_FAILED";
    public const string TwinCatRestartFailed = "TWINCAT_RESTART_FAILED";
    public const string TwinCatStateUnknown = "TWINCAT_STATE_UNKNOWN";
    public const string TestAdsUnavailable = "TEST_ADS_UNAVAILABLE";
    public const string TestCompletionSymbolUnavailable = "TEST_COMPLETION_SYMBOL_UNAVAILABLE";
    public const string TestCompletionTimeout = "TEST_COMPLETION_TIMEOUT";
    public const string TestReportNotProduced = "TEST_REPORT_NOT_PRODUCED";
    public const string TestReportInvalid = "TEST_REPORT_INVALID";
    public const string IpcVersionMismatch = "IPC_VERSION_MISMATCH";
}
