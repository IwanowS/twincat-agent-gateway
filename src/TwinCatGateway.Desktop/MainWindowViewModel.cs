using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;

namespace TwinCatGateway.Desktop;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private static readonly IReadOnlyList<BuildAction>
        AvailableBuildActions =
            new[]
            {
                BuildAction.Rebuild,
                BuildAction.Build,
                BuildAction.Clean,
            };

    private readonly GatewayDesktopHost _host;
    private string _gatewayState = string.Empty;
    private string _xaeStatus = string.Empty;
    private string _solution = string.Empty;
    private string _profile = string.Empty;
    private string _target = string.Empty;
    private string _activationPolicy = string.Empty;
    private string _currentOperation = string.Empty;
    private string _currentStage = string.Empty;
    private string _workspaceOwnership = string.Empty;
    private string _lastBuild = string.Empty;
    private string _lastActivation = string.Empty;
    private string _lastTest = string.Empty;
    private string _lastIssue = string.Empty;
    private BuildAction _selectedBuildAction =
        BuildAction.Rebuild;
    private bool _canStartOperation;
    private bool _canActivate;
    private bool _canReconnect;

    public MainWindowViewModel(GatewayDesktopHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        StartupError = host.StartupError;
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<OperationRow> RecentOperations { get; } = new();

    public IReadOnlyList<BuildAction> BuildActions { get; } =
        AvailableBuildActions;

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

    public string? StartupError { get; }

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

    public bool CanReconnect
    {
        get => _canReconnect;
        private set => SetField(ref _canReconnect, value);
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
            LastIssue =
                "UI_STATUS_REFRESH_FAILED: "
                + exception.Message;
            CanStartOperation = false;
            CanActivate = false;
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
        WorkspaceOwnership = status.Xae.AgentWorkspaceOwned
            ? "Agent owns project files. "
                + "Unsaved XAE editor changes are discarded. "
                + $"Discarded documents: "
                + status.Xae.DiscardedDocumentCount
                    .ToString(CultureInfo.CurrentCulture)
                + "."
            : "Inactive until XAE ownership is acquired.";
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
            && status.Xae.AgentWorkspaceOwned
            && profile is not null;
        CanActivate =
            CanStartOperation
            && profile?.AllowActivation == true;
        CanReconnect =
            !operationActive
            && _host.CanReconnectXae;

        RecentOperations.Clear();
        foreach (StoredOperation operation in
            _host.ApplicationService.GetRecentOperations(20))
        {
            RecentOperations.Add(
                new OperationRow(
                    operation.Summary.OperationId,
                    operation.Summary.Kind.ToString(),
                    operation.Summary.State.ToString(),
                    operation.Summary.QueuedAtUtc.LocalDateTime.ToString(
                        "G",
                        CultureInfo.CurrentCulture)));
        }
    }

    private GatewayDiagnosticsResult ReadRecentDiagnostics(
        GatewayStatusResult status)
    {
        long afterCursor = Math.Max(
            0,
            status.LatestEventCursor - 100);
        return _host.ApplicationService.GetDiagnostics(
            new GetDiagnosticsParameters
            {
                EventStreamId = status.EventStreamId,
                AfterEventCursor = afterCursor,
                MaximumEvents = 100,
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

    private void SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class OperationRow
{
    public OperationRow(
        string operationId,
        string kind,
        string state,
        string queuedAt)
    {
        OperationId = operationId;
        Kind = kind;
        State = state;
        QueuedAt = queuedAt;
    }

    public string OperationId { get; }

    public string Kind { get; }

    public string State { get; }

    public string QueuedAt { get; }
}
