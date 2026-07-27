using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

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
            MessageBox.Show(
                "The log folder could not be opened.\n\n" + exception.Message,
                "TwinCAT Agent Gateway",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
    }
}
