using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using IracingOverlay.App.Interop;
using IracingOverlay.App.Updates;
using UpdateState = IracingOverlay.App.Updates.UpdateStatus; // the window already has an UpdateStatus() method

namespace IracingOverlay.App;

/// <summary>Small control panel: overlay toggles, layout edit mode, connection status.</summary>
public partial class MainWindow : Window
{
    private static readonly Brush Connected = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
    private static readonly Brush Disconnected = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));

    private readonly App _app;
    private const int ModeHotkeyId = 1;
    private readonly DispatcherTimer _retryHotkey = new() { Interval = TimeSpan.FromSeconds(2) };
    private HwndSource? _source;
    private bool _hotkeyRegistered;

    public MainWindow(App app)
    {
        _app = app;
        _retryHotkey.Tick += (_, _) => RegisterModeHotkey();
        InitializeComponent();
        ModeText.Text = _app.Mode.ToString();
        DataContext = app.ViewModel;

        StandingsOptions.Attach(app, app.StandingsWindow);
        RelativeOptions.Attach(app, app.RelativeWindow);
        FuelOptions.Attach(app, app.FuelWindow);
        FocusCheck.IsChecked = app.Settings.HideWhenSimulatorUnfocused;
        FocusCheck.Checked += OnFocusToggled;
        FocusCheck.Unchecked += OnFocusToggled;

        app.ConnectionStateChanged += UpdateStatus;
        app.Updates.Changed += ShowUpdateState;
        ShowUpdateState();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        StatusDot.Fill = _app.IsConnected ? Connected : Disconnected;
        StatusText.Text = _app.IsConnected ? "Connected to iRacing"
            : _app.IsSimulatorRunning ? "Waiting for session telemetry…" : "Waiting for iRacing…";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        NativeMethods.UseDarkTitleBar(_source.Handle, 0x11161D); // matches the window background
        _source.AddHook(OnWindowMessage);
        RegisterModeHotkey();
    }

    private void RegisterModeHotkey()
    {
        _hotkeyRegistered = NativeMethods.RegisterHotKey(_source!.Handle, ModeHotkeyId,
            NativeMethods.MOD_NOREPEAT, NativeMethods.VK_F6);
        if (_hotkeyRegistered)
        {
            _retryHotkey.Stop();
            ShortcutHint.Text = "F6 cycles Off → Edit → Live. In Edit, drag to move and scroll to resize.";
        }
        else
        {
            ShortcutHint.Text = "F6 is unavailable. If another app uses it, release F6 there. Retrying…";
            _retryHotkey.Start();
        }
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WM_HOTKEY && wParam == ModeHotkeyId && _hotkeyRegistered)
        {
            _app.CycleOverlayMode();
            ModeText.Text = _app.Mode.ToString();
            handled = true;
        }
        return IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_app.IsExiting) return;
        e.Cancel = true;
        Hide();
        _app.Settings.Save();
    }

    protected override void OnClosed(EventArgs e)
    {
        _retryHotkey.Stop();
        if (_source is { IsDisposed: false })
        {
            if (_hotkeyRegistered) NativeMethods.UnregisterHotKey(_source.Handle, ModeHotkeyId);
            _source.RemoveHook(OnWindowMessage);
        }
        base.OnClosed(e);
    }

    private void OnFocusToggled(object sender, RoutedEventArgs e)
    {
        _app.SetHideWhenSimulatorUnfocused(FocusCheck.IsChecked == true);
    }

    private void OnResetPositions(object sender, RoutedEventArgs e) => _app.ResetPositions();

    // ----- Updates -----

    private void ShowUpdateState()
    {
        var updates = _app.Updates;
        string checkedAt = updates.LastChecked is DateTime at ? $" · checked {at:HH:mm}" : "";
        VersionText.Text = $"Version {UpdateService.Current}";
        UpdateStatusText.Text = updates.Status switch
        {
            UpdateState.Checking => "Checking for updates…",
            UpdateState.UpToDate => "Up to date" + checkedAt,
            UpdateState.Available => $"Version {updates.Latest!.Version} is available{checkedAt}",
            UpdateState.NoReleases => "No releases published yet" + checkedAt,
            _ => $"Couldn't check for updates: {updates.Error}{checkedAt}",
        };
        bool canDownload = updates.Status == UpdateState.Available && updates.Latest?.AssetId != null;
        DownloadUpdate.Visibility = canDownload ? Visibility.Visible : Visibility.Collapsed;
        if (canDownload && DownloadUpdate.IsEnabled) DownloadUpdate.Content = $"Download {updates.Latest!.Tag}";
        CheckUpdates.IsEnabled = updates.Status != UpdateState.Checking;
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e) => await _app.Updates.CheckAsync();

    private async void OnDownloadUpdate(object sender, RoutedEventArgs e)
    {
        DownloadUpdate.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(done => DownloadUpdate.Content = $"Downloading… {done:P0}");
            string path = await _app.Updates.DownloadAsync(progress);
            DownloadUpdate.Content = "Downloaded";
            UpdateStatusText.Text = $"Saved {Path.GetFileName(path)} in Downloads. Exit the app from the tray, extract the ZIP " +
                "(over the old version or anywhere) and start it again. Your settings are kept.";
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            DownloadUpdate.IsEnabled = true;
            UpdateStatusText.Text = "Download failed: " + ex.Message;
            ShowUpdateState();
        }
    }

    private void OnOpenLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
