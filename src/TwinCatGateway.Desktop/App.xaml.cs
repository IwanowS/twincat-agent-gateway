using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;

namespace TwinCatGateway.Desktop;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF Application releases owned resources in OnExit.")]
public partial class App : Application
{
    private SingleInstanceGuard? _singleInstance;
    private GatewayDesktopHost? _host;
    private MainWindow? _window;
    private SetupWindow? _setupWindow;
    private TrayIconController? _trayIcon;
    private GatewayInstanceRegistration? _instanceRegistration;
    private bool _exitRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        GatewayHostOptions? options = null;
        try
        {
            options = GatewayHostOptions.FromArguments(e.Args);
            if (string.IsNullOrWhiteSpace(
                    options.ConfigurationPath))
            {
                StartSetupMode();
                return;
            }

            if (!SingleInstanceGuard.TryAcquire(
                    "TwinCatAgentGateway",
                    out _singleInstance))
            {
                if (options.LaunchSource
                    == GatewayLaunchSource.Manual)
                {
                    MessageBox.Show(
                        "TwinCAT Agent Gateway is already running "
                        + "for this user.",
                        "TwinCAT Agent Gateway",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                Shutdown();
                return;
            }

            _host = new GatewayDesktopHost(options);
            _host.Start();
            using (Process process = Process.GetCurrentProcess())
            {
                _instanceRegistration =
                    new GatewayInstanceRegistry().Register(
                        new GatewayInstanceRecord
                        {
                            ProcessId = process.Id,
                            ProcessStartedAtUtc =
                                process.StartTime.ToUniversalTime(),
                            PipeName = _host.PipeName,
                            ConfigurationPath =
                                _host.ConfigurationPath
                                ?? options.ConfigurationPath
                                ?? throw new InvalidOperationException(
                                    "Gateway configuration path is unavailable."),
                            ActiveProfile =
                                _host.ActiveProfile?.Name,
                            SolutionPath =
                                _host.ActiveProfile?.Solution,
                            LaunchSource = _host.LaunchSource,
                            UiMode = _host.EffectiveUiMode,
                        });
            }

            _window = new MainWindow(_host);
            _window.Closing += MainWindow_Closing;
            _window.Closed += MainWindow_Closed;
            MainWindow = _window;

            if (_host.EffectiveUiMode == GatewayUiMode.Tray)
            {
                _trayIcon = new TrayIconController();
                _trayIcon.ShowRequested += TrayIcon_ShowRequested;
                _trayIcon.ExitRequested += TrayIcon_ExitRequested;
            }
            else
            {
                ShowMainWindow();
            }
        }
        catch (Exception exception)
        {
            if (options?.LaunchSource
                != GatewayLaunchSource.Agent)
            {
                string message =
                    "TwinCAT Agent Gateway could not start.\n\n"
                    + exception.Message;
                if (exception
                        is GatewayOperationException
                        configurationError
                    && configurationError.Code
                        == ErrorCodes
                            .GatewayConfigNotFound)
                {
                    if (SetupInstructionsProvider.TryRead(
                            out string instructions,
                            out string? setupError))
                    {
                        message += "\n\n" + instructions;
                    }
                    else
                    {
                        message +=
                            "\n\nSetup instructions are "
                            + "unavailable: "
                            + setupError;
                    }
                }

                MessageBox.Show(
                    message,
                    "TwinCAT Agent Gateway",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _instanceRegistration?.Dispose();
        _host?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void MainWindow_Closing(
        object? sender,
        CancelEventArgs e)
    {
        if (!_exitRequested
            && _host?.EffectiveUiMode == GatewayUiMode.Tray)
        {
            e.Cancel = true;
            _window?.Hide();
        }
    }

    private void MainWindow_Closed(
        object? sender,
        EventArgs e)
    {
        if (_host?.EffectiveUiMode == GatewayUiMode.Window)
        {
            Shutdown();
        }
    }

    private void TrayIcon_ShowRequested(
        object? sender,
        EventArgs e)
    {
        ShowMainWindow();
    }

    private void TrayIcon_ExitRequested(
        object? sender,
        EventArgs e)
    {
        _exitRequested = true;
        _window?.Close();
        Shutdown();
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private void StartSetupMode()
    {
        if (!SingleInstanceGuard.TryAcquire(
                "TwinCatAgentGateway.Setup",
                out _singleInstance))
        {
            MessageBox.Show(
                "TwinCAT Agent Gateway setup is already open "
                + "for this user.",
                "TwinCAT Agent Gateway",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _setupWindow = new SetupWindow();
        _setupWindow.Closed +=
            (_, _) => Shutdown();
        MainWindow = _setupWindow;
        _setupWindow.Show();
        _setupWindow.Activate();
    }
}
