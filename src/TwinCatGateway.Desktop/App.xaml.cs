using System;
using System.Diagnostics.CodeAnalysis;
using System.Windows;

namespace TwinCatGateway.Desktop;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF Application releases owned resources in OnExit.")]
public partial class App : Application
{
    private SingleInstanceGuard? _singleInstance;
    private GatewayDesktopHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!SingleInstanceGuard.TryAcquire(
            "TwinCatAgentGateway",
            out _singleInstance))
        {
            MessageBox.Show(
                "TwinCAT Agent Gateway is already running for this user.",
                "TwinCAT Agent Gateway",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            GatewayHostOptions options = GatewayHostOptions.FromArguments(e.Args);
            _host = new GatewayDesktopHost(options);
            _host.Start();
            MainWindow window = new(_host);
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "TwinCAT Agent Gateway could not start.\n\n" + exception.Message,
                "TwinCAT Agent Gateway",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
