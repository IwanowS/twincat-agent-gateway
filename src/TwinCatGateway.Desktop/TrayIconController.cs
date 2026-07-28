using System;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace TwinCatGateway.Desktop;

public sealed class TrayIconController : IDisposable
{
    private readonly Icon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.NotifyIcon _notifyIcon;
    private int _disposed;

    public TrayIconController()
    {
        _icon = Icon.ExtractAssociatedIcon(
                Forms.Application.ExecutablePath)
            ?? (Icon)SystemIcons.Application.Clone();
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add(
            "Open",
            image: null,
            (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty));
        _menu.Items.Add(
            "Exit",
            image: null,
            (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = _icon,
            Text = "TwinCAT Agent Gateway",
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) =>
            ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ShowRequested;

    public event EventHandler? ExitRequested;

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(
                ref _disposed,
                1) != 0)
        {
            return;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}
