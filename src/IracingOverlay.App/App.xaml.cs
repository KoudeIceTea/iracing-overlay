using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using IracingOverlay.App.Interop;
using IracingOverlay.App.Overlays;
using IracingOverlay.App.Settings;
using IracingOverlay.App.Updates;
using IracingOverlay.App.ViewModels;
using IracingOverlay.Core;
using IracingOverlay.Core.Race;

namespace IracingOverlay.App;

internal enum OverlayMode { Off, Edit, Live }

public partial class App : Application
{
    private readonly OverlayViewModel _viewModel = new();
    private readonly OverlayViewModel _relativeView = new();
    private AppSettings _settings = new();
    private TelemetryService? _telemetry;
    private TrayIcon? _tray;
    internal UpdateService Updates { get; private set; } = null!;
    internal bool IsExiting { get; private set; }
    private StandingsOverlay? _standings;
    private RelativeOverlay? _relative;
    private FuelOverlay? _fuel;
    private RaceSnapshot? _pendingSnapshot;
    private int _applyQueued;
    internal OverlayMode Mode { get; private set; } = OverlayMode.Live;
    private readonly SimulatorPresence _simulator = new();
    private readonly DispatcherTimer _visibilityTimer = new();

    public bool IsConnected { get; private set; }
    public bool IsSimulatorRunning => _simulator.IsRunning;
    public event Action? ConnectionStateChanged;

    public OverlayViewModel ViewModel => _viewModel;
    public AppSettings Settings => _settings;
    private IEnumerable<OverlayWindow> Overlays => [_standings!, _relative!, _fuel!];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _settings = AppSettings.Load();

        var area = SystemParameters.WorkArea;
        _standings = new StandingsOverlay
        {
            DataContext = _viewModel,
            DefaultPosition = _ => new Point(area.Left + 20, area.Top + 20),
        };
        _standings.Attach(_settings.Standings);

        _relative = new RelativeOverlay
        {
            DataContext = _relativeView,
            DefaultPosition = w => new Point(area.Right - w.ActualWidth - 30, area.Bottom - 300),
        };
        _relative.Attach(_settings.Relative);
        _fuel = new FuelOverlay { DefaultPosition = w => new Point(area.Right - w.ActualWidth - 30, area.Top + 20) };
        _fuel.Attach(_settings.Fuel);
        _viewModel.StandingsNames = _settings.Standings.NameFormat;
        _relativeView.RelativeNames = _settings.Relative.NameFormat;
        _viewModel.StandingsTeams = _settings.Standings.TeamNames(defaultOn: true);
        _relativeView.RelativeTeams = _settings.Relative.TeamNames(defaultOn: false);
        _relativeView.RelativeAhead = Math.Max(0, _settings.RelativeCarsAhead);
        _relativeView.RelativeBehind = Math.Max(0, _settings.RelativeCarsBehind);

        foreach (var o in Overlays) o.BlurEnabled = _settings.GlassBlur;

        _telemetry = new TelemetryService
        {
            UpdateHz = _settings.UpdateHz,
            Options = RaceOptionsFromSettings(),
        };
        _telemetry.SnapshotUpdated += OnSnapshot;
        _telemetry.ConnectionChanged += connected => Dispatcher.BeginInvoke(() => SetConnected(connected));

        Updates = new UpdateService();
        MainWindow = new MainWindow(this);
        // F6 is registered on this HWND even before settings are opened for the first time.
        new WindowInteropHelper(MainWindow).EnsureHandle();
        _tray = new TrayIcon(OpenSettings, ExitFromTray);
        // Update check: on start and every hour; a tray notification once per new version.
        Updates.NewVersionFound += release => _tray?.Notify("iRacing Overlay update",
            $"Version {release.Version} is available (you have {UpdateService.Current}). Click to open Settings.", OpenSettings);
        Updates.Start();
        if (UpdateService.TakeInstallResult() is { } installed)
            _tray.Notify("iRacing Overlay update", installed.Succeeded
                ? $"Updated to version {UpdateService.Current}."
                : $"The update could not be installed ({installed.Detail}). You're still on version {UpdateService.Current}.",
                OpenSettings);

        _visibilityTimer.Tick += (_, _) => UpdateVisibility();
        UpdatePollingInterval();
        UpdateVisibility();
        _visibilityTimer.Start();
        _telemetry.Start();
    }

    private void OpenSettings()
    {
        if (IsExiting) return;
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Show();
        MainWindow.Activate();
    }

    private void ExitFromTray()
    {
        IsExiting = true;
        Shutdown();
    }

    /// <summary>Quits so the update installer, which waits for this process, can replace the app's files.</summary>
    internal void ExitForUpdate() => ExitFromTray();

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        IsExiting = !e.Cancel;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _visibilityTimer.Stop();
        Updates?.Dispose();
        _tray?.Dispose();
        _telemetry?.Dispose();
        _settings.Save();
        base.OnExit(e);
    }

    /// <summary>Changes how driver names are written in one overlay and resizes its name column to match.</summary>
    internal void SetNameFormat(OverlayWindow overlay, NameFormat format)
    {
        overlay.OverlaySettings.NameFormat = format;
        if (overlay == _standings) _viewModel.StandingsNames = format;
        else _relativeView.RelativeNames = format;
        overlay.ApplyFieldSettings();
        _settings.Save();
    }

    internal bool ShowsTeamNames(OverlayWindow overlay) =>
        overlay == _standings ? _viewModel.StandingsTeams : _relativeView.RelativeTeams;

    /// <summary>Team races: show the team name above the driver in this overlay (next tick).</summary>
    internal void SetShowTeamNames(OverlayWindow overlay, bool show)
    {
        overlay.OverlaySettings.ShowTeamNames = show;
        if (overlay == _standings) _viewModel.StandingsTeams = show;
        else _relativeView.RelativeTeams = show;
        _settings.Save();
    }

    public void SetOverlayEnabled(OverlayWindow overlay, bool enabled)
    {
        overlay.OverlaySettings.Enabled = enabled;
        UpdateVisibility();
        _settings.Save();
    }

    public OverlayWindow StandingsWindow => _standings!;
    public OverlayWindow RelativeWindow => _relative!;
    public OverlayWindow FuelWindow => _fuel!;

    private RaceOptions RaceOptionsFromSettings() => new(
        FocusClassRows: _settings.StandingsFocusClassRows,
        OtherClassRows: _settings.StandingsOtherClassRows,
        RelativeAhead: _settings.RelativeCarsAhead, RelativeBehind: _settings.RelativeCarsBehind,
        StandingsFollowCamera: _settings.Standings.FollowSpectatedCar,
        RelativeFollowCamera: _settings.Relative.FollowSpectatedCar);

    internal void ApplyRaceOptions()
    {
        if (_telemetry != null) _telemetry.Options = RaceOptionsFromSettings();
        _settings.Save();
    }

    public void SetHideWhenSimulatorUnfocused(bool enabled)
    {
        _settings.HideWhenSimulatorUnfocused = enabled;
        UpdatePollingInterval();
        UpdateVisibility();
        _settings.Save();
    }

    internal void CycleOverlayMode()
    {
        bool wasEditing = Mode == OverlayMode.Edit;
        Mode = Mode switch
        {
            OverlayMode.Live => OverlayMode.Off,
            OverlayMode.Off => OverlayMode.Edit,
            OverlayMode.Edit => OverlayMode.Live,
            _ => throw new InvalidOperationException(),
        };
        foreach (var o in Overlays) o.EditMode = Mode == OverlayMode.Edit;
        UpdatePollingInterval();
        UpdateVisibility();
        if (wasEditing) _settings.Save();
    }

    public void ResetPositions()
    {
        foreach (var o in Overlays) o.ResetPosition();
        _settings.Save();
    }

    /// <summary>
    /// Called on the telemetry thread. Keeps only the latest snapshot and queues at most one
    /// UI update at a time, so a busy UI thread never builds up a backlog.
    /// </summary>
    private void OnSnapshot(RaceSnapshot snapshot)
    {
        Interlocked.Exchange(ref _pendingSnapshot, snapshot);
        if (Interlocked.Exchange(ref _applyQueued, 1) == 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, ApplyPending);
    }

    private void ApplyPending()
    {
        Interlocked.Exchange(ref _applyQueued, 0);
        var snapshot = Interlocked.Exchange(ref _pendingSnapshot, null);
        if (snapshot == null) return;
        var type = snapshot.Header.SessionType;
        var session = type.Contains("qual", StringComparison.OrdinalIgnoreCase) ? LayoutSession.Qualifying
            : snapshot.Header.IsRace ? LayoutSession.Race : LayoutSession.Practice;
        _standings!.SetSession(session);
        _relative!.SetSession(session);
        _fuel!.SetSession(session);
        _fuel.Update(snapshot.Fuel);
        _viewModel.ApplyStandings(snapshot, _settings.AnimateStandingsChanges && _standings!.IsVisible);
        _relativeView.ApplyRelative(snapshot);

        // Name columns only need room for team names in team sessions, and only where they're switched on.
        bool teamSession = snapshot.Classes.Any(c => c.Rows.Any(r => r.TeamName.Length > 0));
        _standings!.TeamRace = teamSession && _viewModel.StandingsTeams;
        _relative!.TeamRace = snapshot.Relative.Any(r => r.TeamName.Length > 0) && _relativeView.RelativeTeams;
    }

    private void SetConnected(bool connected)
    {
        IsConnected = connected;
        if (!connected)
        {
            _viewModel.Clear();
            _relativeView.Clear();
            _fuel!.Update(new());
        }
        ConnectionStateChanged?.Invoke();
    }

    private void UpdatePollingInterval() => _visibilityTimer.Interval =
        TimeSpan.FromMilliseconds(_settings.HideWhenSimulatorUnfocused || Mode == OverlayMode.Edit ? 150 : 1000);

    private void UpdateVisibility()
    {
        bool previouslyRunning = _simulator.IsRunning;
        _simulator.Refresh();
        if (previouslyRunning != _simulator.IsRunning) ConnectionStateChanged?.Invoke();

        int foregroundPid = 0;
        if (_settings.HideWhenSimulatorUnfocused || Mode == OverlayMode.Edit)
            NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out foregroundPid);

        bool show = _simulator.IsRunning && (!_settings.HideWhenSimulatorUnfocused || _simulator.OwnsWindow(foregroundPid));
        // Explicit layout editing also allows a preview while using the overlay control panel.
        if (Mode == OverlayMode.Edit && foregroundPid == Environment.ProcessId) show = true;

        foreach (var o in Overlays)
        {
            if (Mode != OverlayMode.Off && o.OverlaySettings.Enabled && show) o.ShowOverlay();
            else if (o.IsVisible) o.Hide();
        }
    }
}
