using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Desktop;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;

    public MainWindow(GatewayDesktopHost host)
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel(
            host ?? throw new ArgumentNullException(nameof(host)));
        DataContext = _viewModel;
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();
        Closed += MainWindow_Closed;
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        _viewModel.Refresh();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Refresh();
    }

    private void ReconnectButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        ExecuteUiAction(
            _viewModel.RequestReconnect,
            "XAE reconnect could not be requested.");
    }

    private void BuildButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        ExecuteUiAction(
            () => _viewModel.StartBuild(),
            "The build operation could not be queued.");
    }

    private void SynchronizeButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        MessageBoxResult confirmation =
            MessageBox.Show(
                MainWindowViewModel.SynchronizationConfirmation,
                "Confirm disk synchronization",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        ExecuteUiAction(
            () => _viewModel.StartSynchronization(),
            "Disk synchronization could not be queued.");
    }

    private void ActivateButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        MessageBoxResult confirmation =
            MessageBox.Show(
                _viewModel.ActivationConfirmation,
                "Confirm TwinCAT activation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        ExecuteUiAction(
            () => _viewModel.StartActivation(),
            "The activation operation could not be queued.");
    }

    private void ConfigModeButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        MessageBoxResult confirmation =
            MessageBox.Show(
                _viewModel.RecoveryConfirmation,
                "Confirm TwinCAT Config Mode",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        ExecuteUiAction(
            () => _viewModel.StartRecoverToConfig(),
            "The Config Mode recovery operation could not be queued.");
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = _viewModel.LogDirectory,
                    UseShellExecute = true,
                });
        }
        catch (Exception exception)
        {
            _viewModel.RecordUiFailure(
                "command",
                exception);
            MessageBox.Show(
                "The log folder could not be opened.\n\n" + exception.Message,
                "TwinCAT Agent Gateway",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void SetupInstructionsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            TextBox content = new()
            {
                FontFamily =
                    new System.Windows.Media.FontFamily(
                        "Consolas"),
                FontSize = 13,
                IsReadOnly = true,
                Text = SetupInstructionsProvider.Read(),
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility =
                    ScrollBarVisibility.Auto,
            };
            Window viewer = new()
            {
                Owner = this,
                Title = "TwinCAT Agent Gateway setup",
                Width = 760,
                Height = 520,
                Content = content,
                WindowStartupLocation =
                    WindowStartupLocation.CenterOwner,
            };
            viewer.ShowDialog();
        }
        catch (Exception exception)
        {
            _viewModel.RecordUiFailure(
                "setup.instructions",
                exception);
            MessageBox.Show(
                "Setup instructions could not be opened.\n\n"
                + exception.Message,
                "TwinCAT Agent Gateway",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OpenOperationLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (!(RecentOperationsList.SelectedItem is OperationRow operation))
        {
            MessageBox.Show(
                "Select an operation first.",
                "TwinCAT Agent Gateway",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            ResourceContent? resource =
                _viewModel.GetPrimaryResource(operation.OperationId);
            if (resource is null)
            {
                MessageBox.Show(
                    "This operation has no stored artifact.",
                    "TwinCAT Agent Gateway",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            TextBox content = new()
            {
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 13,
                IsReadOnly = true,
                Text = resource.Content
                    + (resource.Truncated
                        ? "\n\n[First 1 MiB shown; use the resource API for more.]"
                        : string.Empty),
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            Window viewer = new()
            {
                Owner = this,
                Title = resource.Uri,
                Width = 900,
                Height = 620,
                Content = content,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            viewer.Show();
        }
        catch (Exception exception)
        {
            _viewModel.RecordUiFailure(
                "operation.resource",
                exception);
            MessageBox.Show(
                "The operation artifact could not be opened.\n\n" + exception.Message,
                "TwinCAT Agent Gateway",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
    }

    private void ExecuteUiAction(
        Action action,
        string failureMessage)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            _viewModel.RecordUiFailure(
                "command",
                exception);
            MessageBox.Show(
                failureMessage + "\n\n" + exception.Message,
                "TwinCAT Agent Gateway",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            _viewModel.Refresh();
        }
    }
}
