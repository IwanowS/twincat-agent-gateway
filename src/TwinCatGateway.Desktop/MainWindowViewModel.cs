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
    private readonly GatewayDesktopHost _host;
    private string _gatewayState = string.Empty;
    private string _xaeStatus = string.Empty;
    private string _solution = string.Empty;
    private string _profile = string.Empty;
    private string _target = string.Empty;
    private string _activationPolicy = string.Empty;
    private string _currentOperation = string.Empty;
    private UnsavedDocumentPolicy _unsavedDocuments;
    private string _unsavedDocumentsStatus = string.Empty;

    public MainWindowViewModel(GatewayDesktopHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        StartupError = host.StartupError;
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<OperationRow> RecentOperations { get; } = new();

    public IReadOnlyList<UnsavedDocumentPolicy> UnsavedDocumentPolicies { get; } =
        new[]
        {
            UnsavedDocumentPolicy.SaveAll,
            UnsavedDocumentPolicy.Reject,
        };

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

    public UnsavedDocumentPolicy UnsavedDocuments
    {
        get => _unsavedDocuments;
        set
        {
            if (_unsavedDocuments == value)
            {
                return;
            }

            try
            {
                _host.UpdateUnsavedDocumentPolicy(value);
                SetField(ref _unsavedDocuments, value);
                UnsavedDocumentsStatus =
                    "Saved to the active profile.";
            }
            catch (Exception exception)
            {
                UnsavedDocumentsStatus =
                    "Could not save the policy: "
                    + exception.Message;
                OnPropertyChanged();
            }
        }
    }

    public string UnsavedDocumentsStatus
    {
        get => _unsavedDocumentsStatus;
        private set => SetField(
            ref _unsavedDocumentsStatus,
            value);
    }

    public bool CanEditUnsavedDocuments =>
        _host.ActiveProfile is not null
        && !string.IsNullOrWhiteSpace(_host.ConfigurationPath);

    public string? StartupError { get; }

    public string LogDirectory => _host.LogDirectory;

    public void Refresh()
    {
        GatewayStatusResult status = _host.ApplicationService.GetStatus();
        GatewayState = status.Gateway.State.ToString();
        XaeStatus = status.Xae.Connected
            ? $"Connected · {status.Xae.Version ?? "unknown version"}"
            : "Disconnected";
        Solution = status.Xae.Solution ?? "No XAE solution attached";
        ProjectProfile? profile = _host.ActiveProfile;
        Profile = profile?.Name ?? "No valid profile";
        Target = FormatTarget(profile?.ExpectedTarget);
        ActivationPolicy = profile?.AllowActivation == true
            ? "Activation allowed for the exact configured target"
            : "Activation disabled";
        SetField(
            ref _unsavedDocuments,
            profile?.UnsavedDocuments
                ?? UnsavedDocumentPolicy.SaveAll,
            nameof(UnsavedDocuments));
        CurrentOperation = status.CurrentOperation is null
            ? "Idle"
            : $"{status.CurrentOperation.Kind} · {status.CurrentOperation.State}";

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

        return $"{target.Name ?? "unknown"} · {target.AmsNetId ?? "unknown AMS NetId"}";
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
