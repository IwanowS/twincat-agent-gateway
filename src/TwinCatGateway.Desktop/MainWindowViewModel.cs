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
    private bool _canConfigTarget;
    private bool _canSynchronize;
    private bool _canReconnect;
    private bool _isVerboseEvents;
    private string _selectedOperationKind = AllFilter;
    private string _selectedOperationState = AllFilter;
    private TargetSystemState _targetState = TargetSystemState.Unknown;

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

    public bool CanConfigTarget
    {
        get => _canConfigTarget;
        private set =>
            SetField(ref _canConfigTarget, value);
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
            ResolvedProfile? profile = _host.ActiveProfile;
            string target = FormatTarget(
                profile?.Target);
            return "Activate the TwinCAT configuration and "
                + "restart the configured remote runtime?\n\n"
                + $"Profile: {profile?.Name ?? "unknown"}\n"
                + $"Target: {target}\n\n"
                + "This is an explicit state-changing operation.";
        }
    }

    public string TargetConfigConfirmation
    {
        get
        {
            ResolvedProfile? profile = _host.ActiveProfile;
            string target = FormatTarget(
                profile?.Target);
            return "Transition the configured remote TwinCAT Target "
                + "to Config?\n\n"
                + $"Profile: {profile?.Name ?? "unknown"}\n"
                + $"Target: {target}\n"
                + $"Current observed state: {_targetState}\n\n"
                + "This is an explicit Target state change. If the "
                + "Target is already in Config, no command is sent.";
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
            CanConfigTarget = false;
            CanReconnect = _host.CanReconnectXae;
        }
    }

    public OperationHandle StartBuild()
    {
        ResolvedProfile profile =
            RequireActiveProfile();
        EnsureNoActiveOperation();
        OperationHandle accepted =
            _host.ApplicationService.StartXaeBuild(
                new XaeBuildParameters
                {
                    Profile = profile.Name,
                    Action = SelectedBuildAction,
                    Detail = DetailLevel.Compact,
                });
        Refresh();
        return accepted;
    }

    public OperationHandle StartActivation()
    {
        ResolvedProfile profile =
            RequireActiveProfile();
        _host.Capabilities.EnsureAllowed(
            profile,
            CapabilityKey.XaeActivate,
            "ui.activation.admission");

        EnsureNoActiveOperation();
        OperationHandle accepted =
            _host.ApplicationService.StartActivation(
                new ActivateParameters
                {
                    Profile = profile.Name,
                    FinalTargetMode = ActivationFinalTargetMode.Run,
                    Verification = VerificationMode.None,
                    TimeoutSeconds = 120,
                });
        Refresh();
        return accepted;
    }

    public OperationHandle StartSynchronization()
    {
        ResolvedProfile profile = RequireActiveProfile();
        EnsureNoActiveOperation();
        OperationHandle accepted =
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

    public OperationHandle StartTargetConfig()
    {
        ResolvedProfile profile =
            RequireActiveProfile();
        _host.Capabilities.EnsureAllowed(
            profile,
            CapabilityKey.TargetConfig,
            "ui.target.config.admission");

        EnsureNoActiveOperation();
        OperationHandle accepted =
            _host.ApplicationService.StartTargetConfig(
                new TargetConfigParameters
                {
                    Profile = profile.Name,
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
        GatewayStateSnapshot gateway =
            _host.ApplicationService.GetGatewayState();
        XaeSessionSnapshot xae = _host.ReadXaeState();
        ProfileObservationSnapshot? observations =
            _host.ReadProfileObservations();
        OperationEventPage eventPage =
            _host.ApplicationService.GetOperationEvents(
                new GetDiagnosticsParameters
                {
                    EventStreamId = gateway.JournalId,
                    AfterEventCursor = Math.Max(
                        0,
                        gateway.LatestEventCursor - 200),
                    MaximumEvents = 200,
                });
        IReadOnlyList<StoredOperation> recent =
            _host.ApplicationService.GetRecentOperations(20);
        OperationRecord? current = recent
            .Select(item => item.Summary)
            .FirstOrDefault(item => string.Equals(
                item.OperationId,
                gateway.CurrentOperationId,
                StringComparison.Ordinal));
        GatewayState = gateway.State.ToString();
        XaeStatus = xae.DteAvailable && xae.SolutionLoaded
            ? $"Connected · {xae.Version ?? "unknown version"}"
            : "Disconnected";
        Solution = xae.Solution ?? "No XAE solution attached";
        WorkspaceOwnership =
            $"Disk synchronization: "
            + xae.SynchronizationState
            + ". Unsaved XAE documents: "
            + xae.DirtyDocuments.Count
                .ToString(CultureInfo.CurrentCulture)
            + ". Automatic saving is disabled.";
        ResolvedProfile? profile = _host.ActiveProfile;
        Profile = profile?.Name ?? "No valid profile";
        Target = FormatTarget(profile?.Target);
        bool activationAllowed = profile is not null
            && _host.Capabilities.Evaluate(
                profile,
                CapabilityKey.XaeActivate).Effective;
        bool targetConfigAllowed = profile is not null
            && _host.Capabilities.Evaluate(
                profile,
                CapabilityKey.TargetConfig).Effective;
        ActivationPolicy = activationAllowed
            ? "Activation allowed for the exact configured target"
            : "Activation disabled";
        CurrentOperation = current is null
            ? "Idle"
            : $"{current.Kind} · {current.State}";
        CurrentStage = FormatCurrentStage(
            current,
            eventPage.Events);
        TargetSystemObservation? targetObservation = observations?.Target;
        RuntimeState = FormatTargetState(targetObservation);
        RuntimeAlert = FormatObservationError(targetObservation?.Error);
        _targetState = targetObservation?.State ?? TargetSystemState.Unknown;
        UnsynchronizedFilesSummary =
            FormatUnsynchronizedFilesSummary(
                xae.SynchronizationState,
                0);
        XaeBuildResult? lastBuild = recent
            .Select(item => item.Result)
            .OfType<XaeBuildResult>()
            .FirstOrDefault();
        ActivationResult? lastActivation = recent
            .Select(item => item.Result)
            .OfType<ActivationResult>()
            .FirstOrDefault();
        LastBuild = FormatBuild(lastBuild);
        LastActivation = FormatActivation(lastActivation);
        LastTest = FormatTest(lastActivation?.Verification.Result);
        LastIssue = FormatLastIssue(eventPage.Events);
        bool operationActive =
            current is not null
            && (current.State
                    == OperationState.Queued
                || current.State
                    == OperationState.Running);
        CanStartOperation =
            !operationActive
            && xae.DteAvailable
            && xae.SynchronizationState
                == SynchronizationState.Confirmed
            && profile is not null;
        CanSynchronize =
            !operationActive
            && xae.DteAvailable
            && profile is not null;
        CanActivate =
            CanStartOperation
            && activationAllowed;
        CanConfigTarget =
            IsTargetConfigAvailable(
                operationActive,
                xae.DteAvailable,
                targetConfigAllowed);
        CanReconnect =
            !operationActive
            && _host.CanReconnectXae;

        SynchronizeRecentOperations(
            RecentOperations,
            recent);
        SynchronizeEvents(
            Events,
            eventPage.JournalId,
            eventPage.Events);
        SynchronizeUnsynchronizedFiles(
            UnsynchronizedFiles,
            Array.Empty<UnsynchronizedFileInfo>());
    }

    internal static void SynchronizeRecentOperations(
        ObservableCollection<OperationRow> rows,
        IReadOnlyList<StoredOperation> operations)
    {
        StoredOperation[] chronologicalOperations =
            operations.Reverse().ToArray();
        HashSet<string> currentOperationIds =
            new(StringComparer.Ordinal);
        for (int index = 0;
             index < chronologicalOperations.Length;
             index++)
        {
            StoredOperation operation =
                chronologicalOperations[index];
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

    public ResourceContent? GetPrimaryResource(string operationId)
    {
        OperationSnapshot<object> operation =
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

    private static string FormatTarget(ResolvedTargetProfile? target)
    {
        if (target is null)
        {
            return "No activation target";
        }

        return string.IsNullOrWhiteSpace(target.Name)
            ? target.AmsNetId
            : $"{target.AmsNetId} · {target.Name}";
    }

    private static string FormatCurrentStage(
        OperationRecord? operation,
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

    private static string FormatBuild(XaeBuildResult? build)
    {
        if (build is null)
        {
            return "Not run";
        }

        return $"{(build.Ok ? "Succeeded" : "Failed")} · "
            + $"{build.Action} · {build.Counts.Errors} errors · "
            + $"{build.Counts.Warnings} warnings";
    }

    private static string FormatActivation(
        ActivationResult? activation)
    {
        if (activation is null)
        {
            return "Not run";
        }

        return $"{(activation.Ok ? "Succeeded" : "Failed")} · "
            + $"{activation.Profile} · "
            + FormatTarget(activation.Target);
    }

    private static string FormatTest(TestResult? test)
    {
        if (test is null)
        {
            return "Not run";
        }

        return $"{(test.Ok ? "Passed" : "Failed")} · "
            + $"{test.Counts.Tests} tests · {test.Counts.Failed} failed";
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

    private static string FormatTargetState(
        TargetSystemObservation? status)
    {
        if (status is null)
        {
            return "Unknown · direct Target observation unavailable";
        }

        string observed = status.ObservedAtUtc != default
            ? status.ObservedAtUtc.LocalDateTime.ToString(
                "G",
                CultureInfo.CurrentCulture)
            : "not observed";
        return $"{status.State} · {status.Freshness} · "
            + $"port {status.Port} · {observed}";
    }

    private static string FormatObservationError(
        ObservationError? alert)
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

    internal static bool IsTargetConfigAvailable(
        bool operationActive,
        bool xaeConnected,
        bool targetConfigAllowed)
    {
        return !operationActive
            && xaeConnected
            && targetConfigAllowed;
    }

    private ResolvedProfile RequireActiveProfile()
    {
        return _host.ActiveProfile
            ?? throw new InvalidOperationException(
                "No valid project profile is configured.");
    }

    private void EnsureNoActiveOperation()
    {
        string? operationId =
            _host.ApplicationService.GetGatewayState()
                .CurrentOperationId;
        if (!string.IsNullOrWhiteSpace(operationId))
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' "
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
