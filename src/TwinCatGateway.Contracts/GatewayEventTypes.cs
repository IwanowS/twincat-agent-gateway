namespace TwinCatGateway.Contracts;

public static class GatewayEventTypes
{
    public const string XaeDialogObserved = "xae.dialog.observed";

    public const string GatewayStarted = "gateway.started";

    public const string GatewayStopping = "gateway.stopping";

    public const string GatewayStopped = "gateway.stopped";

    public const string GatewayFaulted = "gateway.faulted";

    public const string XaeConnected = "xae.connected";

    public const string XaeDisconnected = "xae.disconnected";

    public const string XaeConnectionFailed = "xae.connectionFailed";

    public const string XaeReconnectRequested =
        "xae.reconnectRequested";

    public const string RuntimeStateChanged = "runtime.stateChanged";

    public const string RuntimeStatusReadFailed =
        "runtime.statusReadFailed";

    public const string SolutionOpenQueued = "solution.openQueued";

    public const string SolutionOpenStarted = "solution.openStarted";

    public const string SolutionOpenSucceeded =
        "solution.openSucceeded";

    public const string SolutionOpenFailed = "solution.openFailed";

    public const string SolutionOpenTimedOut =
        "solution.openTimedOut";

    public const string SolutionOpenCancelled =
        "solution.openCancelled";

    public const string BuildQueued = "build.queued";

    public const string BuildStarted = "build.started";

    public const string BuildSucceeded = "build.succeeded";

    public const string BuildFailed = "build.failed";

    public const string BuildTimedOut = "build.timedOut";

    public const string BuildCancelled = "build.cancelled";

    public const string ActivationQueued = "activation.queued";

    public const string ActivationStarted = "activation.started";

    public const string ActivationSucceeded =
        "activation.succeeded";

    public const string ActivationFailed = "activation.failed";

    public const string ActivationTimedOut =
        "activation.timedOut";

    public const string ActivationCancelled =
        "activation.cancelled";

    public const string ActivationRecoveryStarted =
        "activation.recoveryStarted";

    public const string ActivationRecoverySucceeded =
        "activation.recoverySucceeded";

    public const string ActivationConfigurationStarted =
        "activation.configurationStarted";

    public const string ActivationDialogHandled =
        "activation.dialogHandled";

    public const string ActivationConfigurationActivated =
        "activation.configurationActivated";

    public const string ActivationRestartSkipped =
        "activation.restartSkipped";

    public const string ActivationRestartStarted =
        "activation.restartStarted";

    public const string ActivationRestartRequested =
        "activation.restartRequested";

    public const string ActivationRuntimeReady =
        "activation.runtimeReady";

    public const string RecoveryQueued = "recovery.queued";

    public const string RecoveryStarted = "recovery.started";

    public const string RecoverySucceeded = "recovery.succeeded";

    public const string RecoveryFailed = "recovery.failed";

    public const string RecoveryTimedOut = "recovery.timedOut";

    public const string RecoveryCancelled = "recovery.cancelled";

    public const string TcUnitQueued = "tcunit.queued";

    public const string TcUnitStarted = "tcunit.started";

    public const string TcUnitSucceeded = "tcunit.succeeded";

    public const string TcUnitFailed = "tcunit.failed";

    public const string TcUnitTimedOut = "tcunit.timedOut";

    public const string TcUnitCancelled = "tcunit.cancelled";

    public const string TcUnitCompletionObserved =
        "tcunit.completionObserved";

    public const string TcUnitReportProduced =
        "tcunit.reportProduced";

    public const string TcUnitZeroTests =
        "tcunit.zeroTests";
}
