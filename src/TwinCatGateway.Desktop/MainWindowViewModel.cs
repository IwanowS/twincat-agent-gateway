using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;

namespace TwinCatGateway.Desktop;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private const string AllFilter = "All";
    private static readonly IReadOnlyList<BuildAction>
        AvailableBuildActions =
            new[]
            {
                BuildAction.Rebuild,
                BuildAction.Build,
                BuildAction.Clean,
            };
    private static readonly IReadOnlyList<string>
        AvailableOperationKinds =
            new[] { AllFilter }
                .Concat(
                    Enum.GetNames(typeof(OperationKind)))
                .ToArray();
    private static readonly IReadOnlyList<string>
        AvailableOperationStates =
            new[] { AllFilter }
                .Concat(
                    Enum.GetNames(typeof(OperationState)))
                .ToArray();

    private readonly GatewayDesktopHost _host;
    private string _gatewayState = string.Empty;
    private string _xaeStatus = string.Empty;
    private string _solution = string.Empty;
    private string _profile = string.Empty;
    private string _target = string.Empty;
    private string _activationPolicy = string.Empty;
    private string _currentOperation = string.Empty;
    private string _currentStage = string.Empty;
    private string _runtimeState = string.Empty;
    private string _runtimeAlert = string.Empty;
    private string _unsynchronizedFilesSummary = string.Empty;
    private string _workspaceOwnership = string.Empty;
    private string _lastBuild = string.Empty;
    private string _lastActivation = string.Empty;
    private string _lastTest = string.Empty;
    private string _lastIssue = string.Empty;
    private BuildAction _selectedBuildAction =
        BuildAction.Rebuild;
    private bool _canStartOperation;
    private bool _canActivate;
    private bool _canRecoverToConfig;
    private bool _canSynchronize;
    private bool _canReconnect;
    private bool _isVerboseEvents;
    private string _selectedOperationKind = AllFilter;
    private string _selectedOperationState = AllFilter;
    private RuntimeMode _runtimeMode = RuntimeMode.Unknown;

    public MainWindowViewModel(GatewayDesktopHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Version = GatewayProductVersion.DisplayText;
        StartupError = host.StartupError;
        RecentOperationsView =
            CollectionViewSource.GetDefaultView(RecentOperations);
        RecentOperationsView.Filter = IncludeOperation;
        if (RecentOperationsView is ICollectionViewLiveShaping liveView
            && liveView.CanChangeLiveFiltering == true)
        {
            liveView.LiveFilteringProperties.Add(
                nameof(OperationRow.Kind));
            liveView.LiveFilteringProperties.Add(
                nameof(OperationRow.State));
            liveView.IsLiveFiltering = true;
        }

        EventsView = CollectionViewSource.GetDefaultView(Events);
        EventsView.Filter = IncludeEvent;
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<OperationRow> RecentOperations { get; } = new();

    public ObservableCollection<EventRow> Events { get; } = new();

    public ObservableCollection<UnsynchronizedFileRow>
        UnsynchronizedFiles { get; } = new();

    public ICollectionView RecentOperationsView { get; }

    public ICollectionView EventsView { get; }

    public IReadOnlyList<BuildAction> BuildActions { get; } =
        AvailableBuildActions;

    public IReadOnlyList<string> OperationKindFilters { get; } =
        AvailableOperationKinds;

    public IReadOnlyList<string> OperationStateFilters { get; } =
        AvailableOperationStates;

    public string GatewayState
    {
        get => _gatewayState;
        private set => SetField(ref _gatewayState, value);
    }

    public string XaeStatus
    {
        get => _xaeStatus;
        private set => SetField(ref _xaeStatus, value);
    }

    public string Solution
    {
        get => _solution;
        private set => SetField(ref _solution, value);
    }

    public string Profile
    {
        get => _profile;
        private set => SetField(ref _profile, value);
    }

    public string Target
    {
        get => _target;
        private set => SetField(ref _target, value);
    }

    public string ActivationPolicy
    {
        get => _activationPolicy;
        private set => SetField(ref _activationPolicy, value);
    }

    public string CurrentOperation
    {
        get => _currentOperation;
        private set => SetField(ref _currentOperation, value);
    }

    public string CurrentStage
    {
        get => _currentStage;
        private set => SetField(ref _currentStage, value);
    }

    public string RuntimeState
    {
        get => _runtimeState;
        private set => SetField(ref _runtimeState, value);
    }

    public string RuntimeAlert
    {
        get => _runtimeAlert;
        private set => SetField(ref _runtimeAlert, value);
    }

    public string UnsynchronizedFilesSummary
    {
        get => _unsynchronizedFilesSummary;
        private set =>
            SetField(ref _unsynchronizedFilesSummary, value);
    }

    public string? StartupError { get; }

    public string Version { get; }

    public string LogDirectory => _host.LogDirectory;

    public string WorkspaceOwnership
    {
        get => _workspaceOwnership;
        private set => SetField(
            ref _workspaceOwnership,
            value);
    }

    public string LastBuild
    {
        get => _lastBuild;
        private set => SetField(ref _lastBuild, value);
    }

    public string LastActivation
    {
        get => _lastActivation;
        private set => SetField(ref _lastActivation, value);
    }

    public string LastTest
    {
        get => _lastTest;
        private set => SetField(ref _lastTest, value);
    }

    public string LastIssue
    {
        get => _lastIssue;
        private set => SetField(ref _lastIssue, value);
    }

    public BuildAction SelectedBuildAction
    {
        get => _selectedBuildAction;
        set => SetField(ref _selectedBuildAction, value);
    }

    public bool CanStartOperation
    {
        get => _canStartOperation;
        private set => SetField(ref _canStartOperation, value);
    }

    public bool CanActivate
    {
        get => _canActivate;
        private set => SetField(ref _canActivate, value);
    }

    public bool CanRecoverToConfig
    {
        get => _canRecoverToConfig;
        private set =>
            SetField(ref _canRecoverToConfig, value);
    }

    public bool CanSynchronize
    {
        get => _canSynchronize;
        private set => SetField(ref _canSynchronize, value);
    }

    public bool CanReconnect
    {
        get => _canReconnect;
        private set => SetField(ref _canReconnect, value);
    }

    public bool IsVerboseEvents
    {
        get => _isVerboseEvents;
        set
        {
            if (SetField(ref _isVerboseEvents, value))
            {
                EventsView.Refresh();
            }
        }
    }

    public string SelectedOperationKind
    {
        get => _selectedOperationKind;
        set
        {
            if (SetField(ref _selectedOperationKind, value))
            {
                RecentOperationsView.Refresh();
            }
        }
    }

    public string SelectedOperationState
    {
        get => _selectedOperationState;
        set
        {
            if (SetField(ref _selectedOperationState, value))
            {
                RecentOperationsView.Refresh();
            }
        }
    }

    public string ActivationConfirmation
    {
        get
        {
            ProjectProfile? profile = _host.ActiveProfile;
            string target = FormatTarget(
                profile?.ExpectedTarget);
            return "Activate the TwinCAT configuration and "
                + "restart the configured remote runtime?\n\n"
                + $"Profile: {profile?.Name ?? "unknown"}\n"
                + $"Target: {target}\n\n"
                + "This is an explicit state-changing operation.";
        }
    }

    public string RecoveryConfirmation
    {
        get
        {
            ProjectProfile? profile = _host.ActiveProfile;
            string target = FormatTarget(
                profile?.ExpectedTarget);
            return "Restart the configured remote TwinCAT runtime "
                + "in Config Mode?\n\n"
                + $"Profile: {profile?.Name ?? "unknown"}\n"
                + $"Target: {target}\n"
                + $"Current runtime state: {_runtimeMode}\n\n"
                + "This is an explicit state-changing recovery "
                + "operation. It does not activate a configuration.";
        }
    }

    public static string SynchronizationConfirmation =>
        "Reload the selected TwinCAT project from disk?\n\n"
        + "The current disk state will become the confirmed XAE "
        + "baseline. Unsaved XAE documents are not discarded by "
        + "this UI action.";

    public void Refresh()
    {
        try
        {
            RefreshCore();
        }
        catch (Exception exception)
        {
            _host.RecordUiFailure(
                "status.refresh",
                exception);
            GatewayState = "Faulted";
            CurrentOperation = "Status unavailable";
            CurrentStage = "ui.refresh";
            RuntimeState = "Unknown";
            RuntimeAlert = "Runtime status unavailable";
            UnsynchronizedFilesSummary =
                "Synchronization status unavailable";
            LastIssue =
                "UI_STATUS_REFRESH_FAILED: "
                + exception.Message;
            CanStartOperation = false;
            CanSynchronize = false;
            CanActivate = false;
            CanRecoverToConfig = false;
            CanReconnect = _host.CanReconnectXae;
        }
    }

    public OperationAccepted StartBuild()
    {
        ProjectProfile profile =
            RequireActiveProfile();
        EnsureNoActiveOperation();
        OperationAccepted accepted =
            _host.ApplicationService.StartBuild(
                new BuildParameters
                {
                    Profile = profile.Name,
                    Action = SelectedBuildAction,
                    Detail = DetailLevel.Compact,
                    TimeoutSeconds = 120,
                });
        Refresh();
        return accepted;
    }

    public OperationAccepted StartActivation()
    {
        ProjectProfile profile =
            RequireActiveProfile();
        if (!profile.AllowActivation)
        {
            throw new InvalidOperationException(
                "Activation is disabled for the active profile.");
        }

        EnsureNoActiveOperation();
        OperationAccepted accepted =
            _host.ApplicationService.StartActivation(
                new ActivateParameters
                {
                    Profile = profile.Name,
                    WaitForTcUnit = null,
                    TimeoutSeconds = 120,
                });
        Refresh();
        return accepted;
    }

    public OperationAccepted StartSynchronization()
    {
        ProjectProfile profile = RequireActiveProfile();
        EnsureNoActiveOperation();
        OperationAccepted accepted =
            _host.ApplicationService.StartSynchronization(
                new SynchronizeParameters
                {
                    Profile = profile.Name,
                    DiscardDirtyDocuments = false,
                    TimeoutSeconds = 120,
                },
                agentRequest: false);
        Refresh();
        return accepted;
    }

    public OperationAccepted StartRecoverToConfig()
    {
        ProjectProfile profile =
            RequireActiveProfile();
        if (!profile.AllowActivation)
        {
            throw new InvalidOperationException(
                "Runtime recovery is disabled for the active profile.");
        }

        EnsureNoActiveOperation();
        OperationAccepted accepted =
            _host.ApplicationService.StartRecoverToConfig(
                new RecoverToConfigParameters
                {
                    Profile = profile.Name,
                    TimeoutSeconds = 60,
                });
        Refresh();
        return accepted;
    }

    public void RequestReconnect()
    {
        EnsureNoActiveOperation();
        _host.RequestXaeReconnect();
        Refresh();
    }

    internal void RecordUiFailure(
        string stage,
        Exception exception)
    {
        _host.RecordUiFailure(stage, exception);
    }

    private void RefreshCore()
    {
        GatewayStatusResult status =
            _host.ApplicationService.GetStatus();
        GatewayDiagnosticsResult diagnostics =
            ReadRecentDiagnostics(status);
        GatewayState = status.Gateway.State.ToString();
        XaeStatus = status.Xae.Connected
            ? $"Connected · {status.Xae.Version ?? "unknown version"}"
            : "Disconnected";
        Solution = status.Xae.Solution ?? "No XAE solution attached";
        WorkspaceOwnership =
            $"Disk synchronization: "
            + status.Xae.SynchronizationState
            + ". Unsaved XAE documents: "
            + status.Xae.DirtyDocumentCount
                .ToString(CultureInfo.CurrentCulture)
            + ". Automatic saving is disabled.";
        ProjectProfile? profile = _host.ActiveProfile;
        Profile = profile?.Name ?? "No valid profile";
        Target = FormatTarget(profile?.ExpectedTarget);
        ActivationPolicy = profile?.AllowActivation == true
            ? "Activation allowed for the exact configured target"
            : "Activation disabled";
        CurrentOperation = status.CurrentOperation is null
            ? "Idle"
            : $"{status.CurrentOperation.Kind} · {status.CurrentOperation.State}";
        CurrentStage = FormatCurrentStage(
            status.CurrentOperation,
            diagnostics.Events);
        RuntimeState = FormatRuntimeState(status.TwinCat);
        RuntimeAlert = FormatRuntimeAlert(status.TwinCat.Alert);
        _runtimeMode = status.TwinCat.Mode;
        UnsynchronizedFilesSummary =
            FormatUnsynchronizedFilesSummary(
                status.Xae.SynchronizationState,
                diagnostics.Xae.UnsynchronizedFiles.Count);
        LastBuild = FormatBuild(status.LastBuild);
        LastActivation =
            FormatActivation(status.LastActivation);
        LastTest = FormatTest(status.LastTest);
        LastIssue = FormatLastIssue(diagnostics.Events);
        bool operationActive =
            status.CurrentOperation is not null
            && (status.CurrentOperation.State
                    == OperationState.Queued
                || status.CurrentOperation.State
                    == OperationState.Running);
        CanStartOperation =
            !operationActive
            && status.Xae.Connected
            && status.Xae.SynchronizationState
                == SynchronizationState.Confirmed
            && profile is not null;
        CanSynchronize =
            !operationActive
            && status.Xae.Connected
            && profile is not null;
        CanActivate =
            CanStartOperation
            && profile?.AllowActivation == true;
        CanRecoverToConfig =
            IsRecoveryAvailable(
                operationActive,
                status.Xae.Connected,
                profile?.AllowActivation == true,
                status.TwinCat.Mode);
        CanReconnect =
            !operationActive
            && _host.CanReconnectXae;

        SynchronizeRecentOperations(
            RecentOperations,
            _host.ApplicationService.GetRecentOperations(20));
        SynchronizeEvents(
            Events,
            diagnostics.EventStreamId,
            diagnostics.Events);
        SynchronizeUnsynchronizedFiles(
            UnsynchronizedFiles,
            diagnostics.Xae.UnsynchronizedFiles);
    }

    internal static void SynchronizeRecentOperations(
        ObservableCollection<OperationRow> rows,
        IReadOnlyList<StoredOperation> operations)
    {
        HashSet<string> currentOperationIds =
            new(StringComparer.Ordinal);
        for (int index = 0; index < operations.Count; index++)
        {
            StoredOperation operation = operations[index];
            string operationId =
                operation.Summary.OperationId;
            currentOperationIds.Add(operationId);

            OperationRow? row = rows.FirstOrDefault(
                candidate =>
                    string.Equals(
                        candidate.OperationId,
                        operationId,
                        StringComparison.Ordinal));
            if (row is null)
            {
                row = new OperationRow(
                    operationId,
                    operation.Summary.Kind.ToString(),
                    operation.Summary.State.ToString(),
                    operation.Summary.QueuedAtUtc.LocalDateTime.ToString(
                        "G",
                        CultureInfo.CurrentCulture));
                rows.Insert(index, row);
                continue;
            }

            row.Update(
                operation.Summary.Kind.ToString(),
                operation.Summary.State.ToString(),
                operation.Summary.QueuedAtUtc.LocalDateTime.ToString(
                    "G",
                    CultureInfo.CurrentCulture));
            int currentIndex = rows.IndexOf(row);
            if (currentIndex != index)
            {
                rows.Move(currentIndex, index);
            }
        }

        for (int index = rows.Count - 1; index >= 0; index--)
        {
            if (!currentOperationIds.Contains(
                    rows[index].OperationId))
            {
                rows.RemoveAt(index);
            }
        }
    }

    internal static void SynchronizeEvents(
        ObservableCollection<EventRow> rows,
        string eventStreamId,
        IReadOnlyList<GatewayEvent> events)
    {
        if (rows.Count != 0
            && !string.Equals(
                rows[0].EventStreamId,
                eventStreamId,
                StringComparison.Ordinal))
        {
            rows.Clear();
        }

        HashSet<long> retainedCursors =
            new(events.Select(gatewayEvent => gatewayEvent.Cursor));
        for (int index = rows.Count - 1; index >= 0; index--)
        {
            if (!retainedCursors.Contains(rows[index].Cursor))
            {
                rows.RemoveAt(index);
            }
        }

        foreach (GatewayEvent gatewayEvent in events
                     .OrderBy(item => item.Cursor))
        {
            EventRow? row = rows.FirstOrDefault(
                candidate => candidate.Cursor == gatewayEvent.Cursor);
            if (row is null)
            {
                rows.Add(
                    new EventRow(
                        eventStreamId,
                        gatewayEvent));
                continue;
            }

            row.Update(gatewayEvent);
        }
    }

    internal static void SynchronizeUnsynchronizedFiles(
        ObservableCollection<UnsynchronizedFileRow> rows,
        IReadOnlyList<UnsynchronizedFileInfo> files)
    {
        HashSet<string> retainedPaths =
            new(
                files.Select(file => file.Path),
                StringComparer.OrdinalIgnoreCase);
        for (int index = rows.Count - 1; index >= 0; index--)
        {
            if (!retainedPaths.Contains(rows[index].Path))
            {
                rows.RemoveAt(index);
            }
        }

        for (int index = 0; index < files.Count; index++)
        {
            UnsynchronizedFileInfo file = files[index];
            UnsynchronizedFileRow? row = rows.FirstOrDefault(
                candidate => string.Equals(
                    candidate.Path,
                    file.Path,
                    StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                rows.Insert(
                    index,
                    new UnsynchronizedFileRow(file));
                continue;
            }

            row.Update(file);
            int currentIndex = rows.IndexOf(row);
            if (currentIndex != index)
            {
                rows.Move(currentIndex, index);
            }
        }
    }

    private GatewayDiagnosticsResult ReadRecentDiagnostics(
        GatewayStatusResult status)
    {
        long afterCursor = Math.Max(
            0,
            status.LatestEventCursor - 200);
        return _host.ApplicationService.GetDiagnostics(
            new GetDiagnosticsParameters
            {
                EventStreamId = status.EventStreamId,
                AfterEventCursor = afterCursor,
                MaximumEvents = 200,
            });
    }

    public ResourceContent? GetPrimaryResource(string operationId)
    {
        OperationDetails<object> operation =
            _host.ApplicationService.GetOperation(operationId);
        ResourceReference? resource =
            operation.Operation.Resources.FirstOrDefault();
        return resource is null
            ? null
            : _host.ApplicationService.GetResource(
                resource.Uri,
                maximumCharacters: 1024 * 1024,
                offset: 0);
    }

    private static string FormatTarget(TargetIdentity? target)
    {
        if (target is null)
        {
            return "No activation target";
        }

        string amsNetId =
            target.AmsNetId ?? "unknown AMS NetId";
        return string.IsNullOrWhiteSpace(target.Name)
            ? amsNetId
            : $"{amsNetId} · {target.Name}";
    }

    private static string FormatCurrentStage(
        OperationSummary? operation,
        IReadOnlyList<GatewayEvent> events)
    {
        if (operation is null)
        {
            return "No active stage";
        }

        GatewayEvent? latest =
            events.LastOrDefault(
                gatewayEvent =>
                    string.Equals(
                        gatewayEvent.OperationId,
                        operation.OperationId,
                        StringComparison.Ordinal));
        return latest?.Stage
            ?? operation.Error?.Stage
            ?? "Waiting for progress event";
    }

    private static string FormatBuild(BuildSummary? build)
    {
        if (build is null)
        {
            return "Not run";
        }

        return $"{(build.Ok ? "Succeeded" : "Failed")} · "
            + $"{build.Action} · {build.Errors} errors · "
            + $"{build.Warnings} warnings";
    }

    private static string FormatActivation(
        ActivationSummary? activation)
    {
        if (activation is null)
        {
            return "Not run";
        }

        return $"{(activation.Ok ? "Succeeded" : "Failed")} · "
            + $"{activation.Profile} · "
            + FormatTarget(activation.Target);
    }

    private static string FormatTest(TestSummary? test)
    {
        if (test is null)
        {
            return "Not run";
        }

        return $"{(test.Ok ? "Passed" : "Failed")} · "
            + $"{test.Tests} tests · {test.Failed} failed";
    }

    private static string FormatLastIssue(
        IReadOnlyList<GatewayEvent> events)
    {
        GatewayEvent? issue =
            events.LastOrDefault(
                gatewayEvent =>
                    gatewayEvent.Severity
                        == DiagnosticSeverity.Error);
        if (issue is null)
        {
            return "No recent errors";
        }

        string code = issue.Error?.Code ?? issue.Type;
        return $"{code} · {issue.Message}";
    }

    private static string FormatRuntimeState(
        TwinCatStatus status)
    {
        string observed = status.ObservedAtUtc.HasValue
            ? status.ObservedAtUtc.Value.LocalDateTime.ToString(
                "G",
                CultureInfo.CurrentCulture)
            : "not observed";
        return $"{status.Mode} · system {status.SystemMode} · {observed}";
    }

    private static string FormatRuntimeAlert(
        RuntimeAlert? alert)
    {
        return alert is null
            ? "No active runtime alert"
            : $"{alert.Code} · {alert.Message}";
    }

    private static string FormatUnsynchronizedFilesSummary(
        SynchronizationState state,
        int fileCount)
    {
        if (fileCount != 0)
        {
            return fileCount == 1
                ? "1 file differs from the confirmed XAE baseline."
                : $"{fileCount} files differ from the confirmed XAE baseline.";
        }

        return state == SynchronizationState.Confirmed
            ? "No files differ from the confirmed XAE baseline."
            : "Synchronization is required, but no confirmed baseline "
                + "comparison is available.";
    }

    private bool IncludeEvent(object item)
    {
        return item is EventRow row
            && IsEventVisible(
                row.SeverityValue,
                IsVerboseEvents);
    }

    internal static bool IsEventVisible(
        DiagnosticSeverity severity,
        bool verbose)
    {
        return verbose
            || severity >= DiagnosticSeverity.Warning;
    }

    private bool IncludeOperation(object item)
    {
        return item is OperationRow row
            && MatchesOperationFilters(
                row,
                SelectedOperationKind,
                SelectedOperationState);
    }

    internal static bool MatchesOperationFilters(
        OperationRow row,
        string kind,
        string state)
    {
        return (string.Equals(
                    kind,
                    AllFilter,
                    StringComparison.Ordinal)
                || string.Equals(
                    row.Kind,
                    kind,
                    StringComparison.Ordinal))
            && (string.Equals(
                    state,
                    AllFilter,
                    StringComparison.Ordinal)
                || string.Equals(
                    row.State,
                    state,
                    StringComparison.Ordinal));
    }

    internal static bool IsRecoveryAvailable(
        bool operationActive,
        bool xaeConnected,
        bool recoveryAllowed,
        RuntimeMode runtimeMode)
    {
        return !operationActive
            && xaeConnected
            && recoveryAllowed
            && runtimeMode != RuntimeMode.Unknown
            && runtimeMode != RuntimeMode.Config;
    }

    private ProjectProfile RequireActiveProfile()
    {
        return _host.ActiveProfile
            ?? throw new InvalidOperationException(
                "No valid project profile is configured.");
    }

    private void EnsureNoActiveOperation()
    {
        OperationSummary? operation =
            _host.ApplicationService.GetStatus()
                .CurrentOperation;
        if (operation is not null
            && (operation.State == OperationState.Queued
                || operation.State == OperationState.Running))
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' "
                + "is already active.");
        }
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class UnsynchronizedFileRow : INotifyPropertyChanged
{
    private string _changeKind = string.Empty;
    private string _role = string.Empty;

    public UnsynchronizedFileRow(UnsynchronizedFileInfo source)
    {
        Path = source.Path;
        Update(source);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    public string ChangeKind => _changeKind;

    public string Role => _role;

    internal void Update(UnsynchronizedFileInfo source)
    {
        SetField(
            ref _changeKind,
            source.ChangeKind.ToString(),
            nameof(ChangeKind));
        SetField(
            ref _role,
            source.Role.ToString(),
            nameof(Role));
    }

    private void SetField(
        ref string field,
        string value,
        string propertyName)
    {
        if (string.Equals(
                field,
                value,
                StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class EventRow : INotifyPropertyChanged
{
    private string _occurredAt = string.Empty;
    private string _severity = string.Empty;
    private DiagnosticSeverity _severityValue;
    private string _type = string.Empty;
    private string _code = string.Empty;
    private string _description = string.Empty;
    private string _exception = string.Empty;

    public EventRow(
        string eventStreamId,
        GatewayEvent gatewayEvent)
    {
        EventStreamId = eventStreamId;
        Cursor = gatewayEvent.Cursor;
        Update(gatewayEvent);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string EventStreamId { get; }

    public long Cursor { get; }

    public string OccurredAt => _occurredAt;

    public string Severity => _severity;

    public DiagnosticSeverity SeverityValue => _severityValue;

    public string Type => _type;

    public string Code => _code;

    public string Description => _description;

    public string Exception => _exception;

    internal void Update(GatewayEvent gatewayEvent)
    {
        SetField(
            ref _occurredAt,
            gatewayEvent.OccurredAtUtc.LocalDateTime.ToString(
                "G",
                CultureInfo.CurrentCulture),
            nameof(OccurredAt));
        SetField(
            ref _severity,
            gatewayEvent.Severity.ToString(),
            nameof(Severity));
        if (_severityValue != gatewayEvent.Severity)
        {
            _severityValue = gatewayEvent.Severity;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SeverityValue)));
        }

        SetField(ref _type, gatewayEvent.Type, nameof(Type));
        SetField(
            ref _code,
            gatewayEvent.Error?.Code ?? string.Empty,
            nameof(Code));
        SetField(
            ref _description,
            gatewayEvent.Error?.Message ?? gatewayEvent.Message,
            nameof(Description));
        SetField(
            ref _exception,
            FormatException(gatewayEvent),
            nameof(Exception));
    }

    private static string FormatException(GatewayEvent gatewayEvent)
    {
        if (gatewayEvent.Properties.TryGetValue(
                "exceptionType",
                out string? exceptionType))
        {
            string? exceptionDetails =
                gatewayEvent.Error?.Details;
            return !string.IsNullOrWhiteSpace(exceptionDetails)
                && exceptionDetails!.StartsWith(
                    exceptionType + ":",
                    StringComparison.Ordinal)
                    ? exceptionDetails
                    : exceptionType;
        }

        string? details = gatewayEvent.Error?.Details;
        if (string.IsNullOrWhiteSpace(details))
        {
            return string.Empty;
        }

        int separator = details!.IndexOf(':');
        if (separator <= 0)
        {
            return string.Empty;
        }

        string candidate = details.Substring(0, separator);
        return candidate.EndsWith(
            "Exception",
            StringComparison.Ordinal)
                ? details
                : string.Empty;
    }

    private void SetField(
        ref string field,
        string value,
        string propertyName)
    {
        if (string.Equals(
                field,
                value,
                StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class OperationRow : INotifyPropertyChanged
{
    private string _kind;
    private string _state;
    private string _queuedAt;

    public OperationRow(
        string operationId,
        string kind,
        string state,
        string queuedAt)
    {
        OperationId = operationId;
        _kind = kind;
        _state = state;
        _queuedAt = queuedAt;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string OperationId { get; }

    public string Kind => _kind;

    public string State => _state;

    public string QueuedAt => _queuedAt;

    internal void Update(
        string kind,
        string state,
        string queuedAt)
    {
        SetField(ref _kind, kind, nameof(Kind));
        SetField(ref _state, state, nameof(State));
        SetField(ref _queuedAt, queuedAt, nameof(QueuedAt));
    }

    private void SetField(
        ref string field,
        string value,
        string propertyName)
    {
        if (string.Equals(
                field,
                value,
                StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}
