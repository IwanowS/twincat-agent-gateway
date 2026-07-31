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
    public const string ConfigVersionUnsupported =
        "CONFIG_VERSION_UNSUPPORTED";
    public const string GatewayStartDisabled =
        "GATEWAY_START_DISABLED";
    public const string GatewayStartFailed =
        "GATEWAY_START_FAILED";
    public const string GatewayInteractiveLaunchUnavailable =
        "GATEWAY_INTERACTIVE_LAUNCH_UNAVAILABLE";
    public const string GatewayStartTimeout =
        "GATEWAY_START_TIMEOUT";
    public const string GatewayShutdownDisabled =
        "GATEWAY_SHUTDOWN_DISABLED";
    public const string GatewayRunningDifferentProject =
        "GATEWAY_RUNNING_DIFFERENT_PROJECT";
    public const string OperationNotFound = "OPERATION_NOT_FOUND";
    public const string OperationNotCancellable = "OPERATION_NOT_CANCELLABLE";
    public const string OperationTimeout = "OPERATION_TIMEOUT";
    public const string OperationFailed = "OPERATION_FAILED";
    public const string UiFailure = "UI_FAILURE";
    public const string ProfileNotFound = "PROFILE_NOT_FOUND";
    public const string ProfileInvalid = "PROFILE_INVALID";
    public const string TargetNotConfigured = "TARGET_NOT_CONFIGURED";
    public const string CapabilityDisabled = "CAPABILITY_DISABLED";
    public const string OperatorLocked = "OPERATOR_LOCKED";
    public const string XaeCloseConsentRequired =
        "XAE_CLOSE_CONSENT_REQUIRED";
    public const string ResourceNotFound = "RESOURCE_NOT_FOUND";
    public const string XaeNotFound = "XAE_NOT_FOUND";
    public const string XaeMultipleMatches = "XAE_MULTIPLE_MATCHES";
    public const string XaeProgIdNotRegistered = "XAE_PROGID_NOT_REGISTERED";
    public const string XaeLaunchFailed = "XAE_LAUNCH_FAILED";
    public const string XaeCloseNotAllowed =
        "XAE_CLOSE_NOT_ALLOWED";
    public const string XaeCloseDiscardNotAllowed =
        "XAE_CLOSE_DISCARD_NOT_ALLOWED";
    public const string XaeCloseFailed = "XAE_CLOSE_FAILED";
    public const string XaeSilentModeFailed = "XAE_SILENT_MODE_FAILED";
    public const string XaeWorkspaceOwnershipFailed =
        "XAE_WORKSPACE_OWNERSHIP_FAILED";
    public const string SolutionNotFound = "SOLUTION_NOT_FOUND";
    public const string SolutionMismatch = "SOLUTION_MISMATCH";
    public const string XaeSolutionMismatch = "XAE_SOLUTION_MISMATCH";
    public const string XaeTargetMismatch = "XAE_TARGET_MISMATCH";
    public const string SysManagerNotAvailable = "SYSMANAGER_NOT_AVAILABLE";
    public const string XaeBusy = "XAE_BUSY";
    public const string ComCallRejected = "COM_CALL_REJECTED";
    public const string ComCallTimeout = "COM_CALL_TIMEOUT";
    public const string BuildFailed = "BUILD_FAILED";
    public const string BuildProjectNotFound =
        "BUILD_PROJECT_NOT_FOUND";
    public const string BuildProjectAmbiguous =
        "BUILD_PROJECT_AMBIGUOUS";
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
    public const string XaeFileChangeGuardFailed =
        "XAE_FILE_CHANGE_GUARD_FAILED";
    public const string ExternalChangeDetected =
        "EXTERNAL_CHANGE_DETECTED";
    public const string XaeSyncRequired =
        "XAE_SYNC_REQUIRED";
    public const string DirtyXaeDocument =
        "DIRTY_XAE_DOCUMENT";
    public const string ForceSynchronizationNotAllowed =
        "FORCE_SYNCHRONIZATION_NOT_ALLOWED";
    public const string ProjectGraphInvalid =
        "PROJECT_GRAPH_INVALID";
    public const string PlcObjectInvalid =
        "PLC_OBJECT_INVALID";
    public const string ActivationNotAllowed = "ACTIVATION_NOT_ALLOWED";
    public const string ActivationTargetMismatch =
        "ACTIVATION_TARGET_MISMATCH";
    public const string ConfigModeRequired = "CONFIG_MODE_REQUIRED";
    public const string ConfigModeRecoveryFailed =
        "CONFIG_MODE_RECOVERY_FAILED";
    public const string RuntimeRecoveryRequired =
        "RUNTIME_RECOVERY_REQUIRED";
    public const string ActivateConfigurationFailed = "ACTIVATE_CONFIGURATION_FAILED";
    public const string ActivationDialogDetected =
        "ACTIVATION_DIALOG_DETECTED";

    public const string XaeUnknownModalDialog =
        "XAE_UNKNOWN_MODAL_DIALOG";

    public const string XaeUnexpectedModalDialog =
        "XAE_UNEXPECTED_MODAL_DIALOG";

    public const string XaeDialogButtonNotFound =
        "XAE_DIALOG_BUTTON_NOT_FOUND";

    public const string XaeDialogActionFailed =
        "XAE_DIALOG_ACTION_FAILED";

    public const string XaeDialogReportedFailure =
        "XAE_DIALOG_REPORTED_FAILURE";

    public const string XaeBlockedByModalDialog =
        "XAE_BLOCKED_BY_MODAL_DIALOG";

    public const string XaeDialogMonitorUnavailable =
        "XAE_DIALOG_MONITOR_UNAVAILABLE";
    public const string TwinCatRestartFailed = "TWINCAT_RESTART_FAILED";
    public const string TwinCatStateUnknown = "TWINCAT_STATE_UNKNOWN";
    public const string XaeSystemStateUnavailable =
        "XAE_SYSTEM_STATE_UNAVAILABLE";
    public const string AdsStateReadFailed = "ADS_STATE_READ_FAILED";
    public const string PlcStateNotObserved =
        "PLC_STATE_NOT_OBSERVED";
    public const string StateObservationsDiverged =
        "STATE_OBSERVATIONS_DIVERGED";
    public const string TestAdsUnavailable = "TEST_ADS_UNAVAILABLE";
    public const string TestCompletionSymbolUnavailable = "TEST_COMPLETION_SYMBOL_UNAVAILABLE";
    public const string TestCompletionTimeout = "TEST_COMPLETION_TIMEOUT";
    public const string TestReportNotProduced = "TEST_REPORT_NOT_PRODUCED";
    public const string TestReportInvalid = "TEST_REPORT_INVALID";
    public const string TestFailed = "TEST_FAILED";
    public const string IpcVersionMismatch = "IPC_VERSION_MISMATCH";
}
