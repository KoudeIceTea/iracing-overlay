using System.Drawing;
using System.Windows.Forms;

namespace IracingOverlay.App;

internal sealed class TrayIcon : IDisposable
{
    private readonly Icon _icon = new(typeof(App), "Assets.overlay.ico");
    private readonly ContextMenuStrip _menu = new();
    private readonly NotifyIcon _tray;

    public TrayIcon(Action openSettings, Action exit)
    {
        _menu.Items.Add("Settings", null, (_, _) => openSettings());
        _menu.Items.Add("Exit", null, (_, _) => exit());
        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "iRacing Overlay",
            ContextMenuStrip = _menu,
        };
        _tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) openSettings();
        };
        _tray.Visible = true;
    }

    public void Dispose()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}
