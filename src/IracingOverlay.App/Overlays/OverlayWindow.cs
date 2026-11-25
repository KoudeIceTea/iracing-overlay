using System.Windows;
using System.Windows.Controls;
using System.Numerics;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using IracingOverlay.App.Interop;
using IracingOverlay.App.Settings;
using IracingOverlay.App.ViewModels;

namespace IracingOverlay.App.Overlays;

/// <summary>
/// Base class for overlay windows: borderless, transparent, always on top, never steals focus,
/// click-through unless edit mode is on (then drag to move, mouse wheel to scale), and an optional
/// real blurred "glass" backdrop clipped to the elements marked <see cref="Glass.IsSurfaceProperty"/>.
/// </summary>
public class OverlayWindow : Window
{
    internal const double MinScale = 0.5, MaxScale = 2.5;
    private const double BottomAnchorDistance = 48;
    internal event Action? ScaleChanged;
    private static readonly Brush EditBorderBrush = CreateEditBrush();

    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly HashSet<FrameworkElement> _surfaces = [];
    private IntPtr _hwnd;
    private bool _editMode;
    private bool _blur = true;
    private bool _regionDirty = true;
    private List<PanelShape> _appliedPanels = [], _pendingPanels = [];
    private PanelBackdrop? _backdrop;
    private bool _backdropTried;
    private bool _regionUpdateQueued;
    private bool _placementQueued;
    private bool _dragging;
    private OverlaySettings _settings = new();

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        BorderBrush = EditBorderBrush;
        BorderThickness = new Thickness(0);
        UseLayoutRounding = true;
        SetResourceReference(FontFamilyProperty, "OverlayFont");
        SetResourceReference(ForegroundProperty, "TextBrush");
        Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular); // digits don't jitter

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyWindowStyles();
            HwndSource.FromHwnd(_hwnd)?.AddHook(OnWindowPositionChanged);
        };

        IsVisibleChanged += (_, _) => _backdrop?.Sync(IsVisible);
        Closed += (_, _) =>
        {
            _topmostTimer.Stop();
            _backdrop?.Dispose();
            _backdrop = null;
            _hwnd = IntPtr.Zero;
        };

        Loaded += (_, _) =>
        {
            if (Content is FrameworkElement fe) fe.LayoutTransform = _scale;
            UpdateRegion();
        };

        // Rows come and go, so the glass shapes change size; only touch the region when they do.
        LayoutUpdated += (_, _) => UpdateRegion();

        // SizeChanged may fire before SizeToContent has resized the native window.
        // Measure its final pixel bounds after WPF finishes the layout pass.
        SizeChanged += (_, e) =>
        {
            if (_placementQueued) return;
            _placementQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                _placementQueued = false;
                if (!_dragging) ClampToMonitor(previousSize: e.PreviousSize);
            });
        };

        MouseLeftButtonDown += (_, e) =>
        {
            if (!_editMode || e.ButtonState != MouseButtonState.Pressed) return;
            _dragging = true;
            try { DragMove(); }
            finally { _dragging = false; }
            ClampToMonitor(snap: true);
            SavePosition();
        };

        MouseWheel += (_, e) =>
        {
            if (!_editMode) return;
            AdjustScale(Math.Sign(e.Delta));
        };

        // Some games (and alt-tabbing) can push us behind; re-assert topmost periodically.
        _topmostTimer.Tick += (_, _) =>
        {
            if (_hwnd != IntPtr.Zero && IsVisible)
                NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        };
        _topmostTimer.Start();
    }

    /// <summary>Computes a default position (in DIPs) the first time an overlay is shown.</summary>
    public Func<OverlayWindow, Point>? DefaultPosition { get; set; }

    public OverlaySettings OverlaySettings => _settings;
    internal virtual OverlayFields Fields => this is StandingsOverlay ? OverlayFields.Standings : OverlayFields.Relative;

    /// <summary>Real blur of whatever is behind the glass panels (the game).</summary>
    public bool BlurEnabled
    {
        get => _blur;
        set
        {
            if (_blur == value) return;
            _blur = value;
            if (!value)
            {
                _backdrop?.Dispose();
                _backdrop = null;
            }
            _backdropTried = false;
            _regionDirty = true;
            UpdateRegion();
        }
    }

    public void Attach(OverlaySettings settings)
    {
        _settings = settings;
        ApplyFieldSettings();
        SetScale(settings.Scale);
        if (!double.IsNaN(settings.Left) && !double.IsNaN(settings.Top))
        {
            Left = settings.Left;
            Top = settings.Top;
        }
    }

    /// <summary>
    /// Team races show the team name (large) above the driver; the column then needs room for team
    /// names even when driver names use a short format.
    /// </summary>
    public bool TeamRace
    {
        get => _teamRace;
        set
        {
            if (_teamRace == value) return;
            _teamRace = value;
            ApplyFieldSettings();
        }
    }
    private bool _teamRace;

    private int NameColumnWidth()
    {
        bool standings = this is StandingsOverlay;
        int width = DriverNames.ColumnWidth(_settings.NameFormat, standings);
        return _teamRace ? Math.Max(width, DriverNames.ColumnWidth(NameFormat.Full, standings)) : width;
    }

    private LayoutSession _session;

    internal void SetSession(LayoutSession session)
    {
        if (_session == session) return;
        _session = session;
        if (_settings.SessionLayouts != null) ApplyFieldSettings();
    }

    internal void ApplyFieldSettings() => ApplyLayout(_settings.LayoutFor(_session));

    internal virtual void ApplyLayout(TableLayout layout)
    {
        var ordered = layout.Ordered("Content", Fields.Content);
        var widths = new GridLength[ordered.Count + 1];
        widths[0] = new GridLength(4); // focus marker
        int panelWidth = 4, classSlot = 0, classSpan = 1, classSpace = 0;
        bool rows = false, extendSummary = false, labels = false;
        for (int i = 0; i < ordered.Count; i++)
        {
            var field = ordered[i];
            int slot = i + 1;
            // "Positions gained since start" only means something in races; don't spend width on it elsewhere.
            bool show = layout.Shows("Content", field) && (field.Id != "Change" || _session == LayoutSession.Race);
            Resources["Content." + field.Id] = show ? Visibility.Visible : Visibility.Collapsed;
            Resources["Index." + field.Id] = slot;
            if (field.Id == "Class")
            {
                if (show) { classSlot = slot; extendSummary = true; }
                widths[slot] = new GridLength(0);
                continue;
            }
            // The name column only takes the room its chosen format (full name, "M. Verstappen", …) needs.
            int width = !show ? 0
                : field.Id == "Name" ? NameColumnWidth()
                : field.ColumnWidth ?? 150;
            widths[slot] = field.Id == "Name" && show ? new GridLength(1, GridUnitType.Star) : new GridLength(width);
            panelWidth += width;
            rows |= show;
            if (show && layout.ShowColumnNames && field.Id is ("License" or "Rating" or "Gap" or "Interval" or "Best" or "Last" or "Average"))
            {
                extendSummary = false;
                labels = true;
            }
            if (extendSummary) { classSpan++; classSpace += width; }
        }
        if (classSlot != 0)
        {
            int extra = Math.Max(0, 200 - classSpace);
            widths[classSlot] = new GridLength(extra);
            panelWidth += extra;
        }
        Resources["Content.Widths"] = widths;
        // Elements that cover the whole row (column titles, divider) span every column, however many there are.
        Resources["Content.ColumnCount"] = widths.Length;
        Resources["Content.ClassSpan"] = classSpan;
        Resources["Content.Rows"] = rows ? Visibility.Visible : Visibility.Collapsed;
        Resources["Content.ClassRow"] = classSlot != 0 ? Visibility.Visible : Visibility.Collapsed;
        if (this is StandingsOverlay)
        {
            Resources["Penalty.Visible"] = rows && layout.ShowPenalties ? Visibility.Visible : Visibility.Collapsed;
            Resources["Content.ColumnNames"] = labels ? Visibility.Visible : Visibility.Collapsed;
            Resources["Content.FirstClassRow"] = classSlot != 0 || labels ? Visibility.Visible : Visibility.Collapsed;
        }
        // Minimum width only: the header/footer strips never wrap, so a wider strip widens the overlay
        // (the name column then takes the extra room).
        Resources["Panel.Width"] = (double)Math.Max(300, panelWidth);
        // Overlays size to their content, so without a cap the (star) name column would grow to fit the
        // longest name or team name. Capping the cell keeps the column at its set width; long names get "…".
        Resources["Name.MaxWidth"] = (double)Math.Max(20, NameColumnWidth() - 4);
        bool header = ApplyBand(layout, "Header", Fields.Header);
        bool footer = ApplyBand(layout, "Footer", Fields.Footer);
        bool body = rows || classSlot != 0 || footer;
        Resources["Body.Visible"] = body ? Visibility.Visible : Visibility.Collapsed;
        Resources["Header.Margin"] = new Thickness(0, 0, 0, header && body ? 3 : 0);
    }

    private bool ApplyBand(TableLayout layout, string section, IReadOnlyList<OverlayField> fields)
    {
        var strip = (SessionStrip)FindName(section + "Strip");
        var visible = layout.Ordered(section, fields).Where(f => layout.Shows(section, f)).ToArray();
        strip.ArrangeFields(visible, section == "Footer");
        bool any = visible.Length != 0;
        Resources[section + ".Visible"] = any ? Visibility.Visible : Visibility.Collapsed;
        return any;
    }

    public void ShowOverlay()
    {
        if (IsVisible) return;
        bool useDefault = double.IsNaN(_settings.Left) || double.IsNaN(_settings.Top);
        Show();
        if (useDefault)
        {
            UpdateLayout();
            var p = _settings.Placement is { } placement
                ? new Point(placement.X * Math.Max(0, SystemParameters.PrimaryScreenWidth - ActualWidth),
                    placement.Y * Math.Max(0, SystemParameters.PrimaryScreenHeight - ActualHeight))
                : DefaultPosition?.Invoke(this) ?? new Point(40, 40);
            _settings.Placement = null; // from now on the saved position is used
            Left = p.X;
            Top = p.Y;
        }
        ClampToMonitor();
        SavePosition();
    }

    /// <summary>Place game overlays against physical monitor edges, including the taskbar area.</summary>
    private void ClampToMonitor(bool snap = false, Size previousSize = default)
    {
        if (_hwnd == IntPtr.Zero || !IsVisible) return;
        var monitor = NativeMethods.MonitorFromWindow(_hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info) || !NativeMethods.GetWindowRect(_hwnd, out var rect)) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var bounds = info.rcMonitor;
        int Edge(int start, int size, int min, int max, double previous, double scale)
        {
            int end = Math.Max(min, max - size);
            double margin = snap ? 8 * scale : 0;
            if (start <= min + margin) return min;
            if (start >= end - margin || previous > 0 && Math.Abs(start + previous * scale - max) <= 1) return end;
            return start;
        }
        int x = Edge(rect.Left, rect.Right - rect.Left, bounds.Left, bounds.Right, previousSize.Width, dpi.DpiScaleX);
        int height = rect.Bottom - rect.Top;
        int y;
        if (!snap && _settings.BottomGap is >= 0 and <= BottomAnchorDistance)
        {
            int gap = (int)Math.Round(_settings.BottomGap.Value * dpi.DpiScaleY);
            y = Math.Max(bounds.Top, bounds.Bottom - height - gap);
        }
        else
        {
            y = Edge(rect.Top, height, bounds.Top, bounds.Bottom, 0, dpi.DpiScaleY);
            double gap = (bounds.Bottom - y - height) / dpi.DpiScaleY;
            _settings.BottomGap = y > bounds.Top && gap is >= 0 and <= BottomAnchorDistance ? gap : null;
        }
        if (x != rect.Left || y != rect.Top)
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        _settings.Left = Left;
        _settings.Top = Top;
    }

    public bool EditMode
    {
        get => _editMode;
        set
        {
            _editMode = value;
            BorderThickness = new Thickness(value ? 2 : 0);
            Cursor = value ? Cursors.SizeAll : null;
            ApplyWindowStyles();
            _regionDirty = true;
            UpdateRegion();
            OnEditModeChanged();
        }
    }

    protected virtual void OnEditModeChanged() { }

    /// <summary>
    /// Overlays drawn on glass panels clip the window to those panels (and blur the game behind them). Overlays
    /// without panels draw straight over the game instead.
    /// </summary>
    protected virtual bool UsesGlass => true;

    /// <summary>Back to the default layout: <paramref name="defaults"/> are this overlay's default settings.</summary>
    public void ResetPosition(OverlaySettings defaults)
    {
        _settings.Left = _settings.Top = double.NaN;
        _settings.BottomGap = null;
        _settings.Placement = defaults.Placement;
        SetScale(defaults.Scale);
        if (IsVisible)
        {
            Hide();
            ShowOverlay();
        }
    }

    internal void AdjustScale(int steps) => SetScale(_settings.Scale + steps * 0.05);

    private void SetScale(double scale)
    {
        scale = Math.Clamp(Math.Round(scale, 2), MinScale, MaxScale);
        _settings.Scale = scale;
        _scale.ScaleX = _scale.ScaleY = scale;
        ScaleChanged?.Invoke();
    }

    private void SavePosition()
    {
        _settings.Left = Left;
        _settings.Top = Top;
    }

    internal void RegisterSurface(FrameworkElement surface)
    {
        if (!_surfaces.Add(surface)) return;
        surface.Unloaded += SurfaceUnloaded;
        QueueRegionUpdate();
    }

    private void SurfaceUnloaded(object sender, RoutedEventArgs e)
    {
        UnregisterSurface((FrameworkElement)sender);
    }

    internal void UnregisterSurface(FrameworkElement surface)
    {
        if (!_surfaces.Remove(surface)) return;
        surface.Unloaded -= SurfaceUnloaded;
        QueueRegionUpdate();
    }

    private void QueueRegionUpdate()
    {
        if (_regionUpdateQueued) return;
        _regionUpdateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _regionUpdateQueued = false;
            if (IsLoaded) UpdateRegion();
        });
    }

    // The native WPF window contains the text. The compositor layer samples the game only
    // inside our rounded panels, independently of the rectangular edit outline.
    private void UpdateRegion()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible) return;
        if (!UsesGlass)
        {
            if (!_regionDirty) return;
            _regionDirty = false;
            NativeMethods.SetWindowRgn(_hwnd, IntPtr.Zero, true);
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        _pendingPanels.Clear();
        foreach (var surface in _surfaces)
        {
            if (!surface.IsVisible || !IsAncestorOf(surface)) continue;
            var bounds = surface.TransformToAncestor(this).TransformBounds(new Rect(surface.RenderSize));
            int left = (int)Math.Round(bounds.Left * dpi.DpiScaleX);
            int top = (int)Math.Round(bounds.Top * dpi.DpiScaleY);
            int right = (int)Math.Round(bounds.Right * dpi.DpiScaleX);
            int bottom = (int)Math.Round(bounds.Bottom * dpi.DpiScaleY);
            if (right <= left || bottom <= top) continue;
            double radius = surface is Border border ? border.CornerRadius.TopLeft : 0;
            var corners = new Vector2((float)(radius * _scale.ScaleX * dpi.DpiScaleX),
                (float)(radius * _scale.ScaleY * dpi.DpiScaleY));
            _pendingPanels.Add(new PanelShape(left, top, right, bottom, corners));
        }
        if (!_regionDirty && _appliedPanels.SequenceEqual(_pendingPanels)) return;
        _regionDirty = false;
        (_appliedPanels, _pendingPanels) = (_pendingPanels, _appliedPanels);

        if (_blur && !_backdropTried && _appliedPanels.Count > 0)
        {
            _backdropTried = true;
            _backdrop = PanelBackdrop.TryCreate(_hwnd);
        }
        _backdrop?.SetPanels(_appliedPanels);
        _backdrop?.Sync(IsVisible);

        if (_editMode)
        {
            NativeMethods.SetWindowRgn(_hwnd, IntPtr.Zero, true);
            return;
        }

        IntPtr region = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        foreach (var panel in _appliedPanels)
        {
            var part = NativeMethods.CreateRoundRectRgn(panel.Left, panel.Top, panel.Right + 1, panel.Bottom + 1,
                (int)Math.Round(2 * panel.Radius.X), (int)Math.Round(2 * panel.Radius.Y));
            NativeMethods.CombineRgn(region, region, part, NativeMethods.RGN_OR);
            NativeMethods.DeleteObject(part);
        }
        if (NativeMethods.SetWindowRgn(_hwnd, region, true) == 0) NativeMethods.DeleteObject(region);
    }

    private IntPtr OnWindowPositionChanged(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_WINDOWPOSCHANGED = 0x0047;
        if (msg == WM_WINDOWPOSCHANGED) _backdrop?.Sync(IsVisible);
        return IntPtr.Zero;
    }


    private void ApplyWindowStyles()
    {
        if (_hwnd == IntPtr.Zero) return;
        long ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_LAYERED;
        if (_editMode) ex &= ~NativeMethods.WS_EX_TRANSPARENT;
        else ex |= NativeMethods.WS_EX_TRANSPARENT; // clicks pass through to the game
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
    }

    private static Brush CreateEditBrush()
    {
        var b = new SolidColorBrush(Color.FromRgb(0x3D, 0x9C, 0xFF));
        b.Freeze();
        return b;
    }
}
