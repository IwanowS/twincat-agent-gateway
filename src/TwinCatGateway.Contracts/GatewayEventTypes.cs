namespace TwinCatGateway.Contracts;

public static class GatewayEventTypes
{
    public const string XaeDialogObserved = "xae.dialog.observed";

    public const string XaeProjectChangesAccepted =
        "xae.projectChangesAccepted";

    public const string GatewayStarted = "gateway.started";

    public const string GatewayStopping = "gateway.stopping";

    public const string GatewayStopped = "gateway.stopped";

    public const string GatewayFaulted = "gateway.faulted";

    public const string XaeConnected = "xae.connected";

    public const string XaeDisconnected = "xae.disconnected";

    public const string XaeConnectionFailed = "xae.connectionFailed";

    public const string XaeReconnectRequested =
        "xae.reconnectRequested";

    public const string XaeSystemStateChanged =
        "xae.systemStateChanged";

    public const string XaeSystemStateReadFailed =
        "xae.systemStateReadFailed";

    public const string TargetSystemStateChanged =
        "target.systemStateChanged";

    public const string TargetSystemStateReadFailed =
        "target.systemStateReadFailed";

    public const string PlcRuntimeStateChanged =
        "plc.runtimeStateChanged";

    public const string PlcRuntimeStateReadFailed =
        "plc.runtimeStateReadFailed";

    public const string StateObservationsDiverged =
        "target.stateObservationsDiverged";

    public const string StateObservationsConverged =
        "target.stateObservationsConverged";

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

    public const string XaeCloseQueued = "xae.closeQueued";

    public const string XaeCloseStarted = "xae.closeStarted";

    public const string XaeCloseSucceeded = "xae.closeSucceeded";

    public const string XaeCloseFailed = "xae.closeFailed";

    public const string XaeCloseTimedOut = "xae.closeTimedOut";

    public const string XaeCloseCancelled = "xae.closeCancelled";

    public const string ActivationQueued = "activation.queued";

    public const string ActivationStarted = "activation.started";

    public const string ActivationSucceeded =
        "activation.succeeded";

    public const string ActivationFailed = "activation.failed";

    public const string ActivationTimedOut =
        "activation.timedOut";

    public const string ActivationCancelled =
        "activation.cancelled";

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

    public const string TargetConfigQueued = "target.config.queued";

    public const string TargetConfigStarted = "target.config.started";

    public const string TargetConfigSucceeded = "target.config.succeeded";

    public const string TargetConfigFailed = "target.config.failed";

    public const string TargetConfigTimedOut = "target.config.timedOut";

    public const string TargetConfigCancelled = "target.config.cancelled";

    public const string TargetStartRestartQueued =
        "target.startRestart.queued";

    public const string TargetStartRestartStarted =
        "target.startRestart.started";

    public const string TargetStartRestartSucceeded =
        "target.startRestart.succeeded";

    public const string TargetStartRestartFailed =
        "target.startRestart.failed";

    public const string TargetStartRestartTimedOut =
        "target.startRestart.timedOut";

    public const string TargetStartRestartCancelled =
        "target.startRestart.cancelled";

    public const string TcUnitCompletionObserved =
        "tcunit.completionObserved";

    public const string TcUnitReportProduced =
        "tcunit.reportProduced";

    public const string TcUnitZeroTests =
        "tcunit.zeroTests";

    public const string UiFailure = "ui.failure";
}
