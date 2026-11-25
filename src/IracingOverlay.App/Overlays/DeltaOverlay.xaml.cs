using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using IracingOverlay.App.Settings;
using IracingOverlay.App.ViewModels;
using IracingOverlay.Core.Race;

namespace IracingOverlay.App.Overlays;

/// <summary>Live delta to a reference lap, with sector times coloured like timing screens (purple, green, yellow).</summary>
public partial class DeltaOverlay : OverlayWindow
{
    /// <summary>The bar is full at this many seconds.</summary>
    private const double BarRange = 1.0;
    private static readonly Brush Gain = Frozen(0x34, 0xD3, 0x99), Loss = Frozen(0xF8, 0x71, 0x71);

    internal override OverlayFields Fields => OverlayFields.Delta;
    private readonly Dictionary<string, FrameworkElement> _rows;
    private readonly ObservableCollection<SectorCell> _sectors = [];
    private DeltaSnapshot _delta = new();
    private DeltaReference _reference;

    public DeltaOverlay()
    {
        InitializeComponent();
        _rows = Rows.Children.Cast<FrameworkElement>().ToDictionary(row => (string)row.Tag);
        SectorTimes.ItemsSource = _sectors;
        SectorDeltas.ItemsSource = _sectors;
    }

    internal override void ApplyLayout(TableLayout layout)
    {
        IEnumerable<OverlayField> Visible(string section, IReadOnlyList<OverlayField> fields) =>
            layout.Ordered(section, fields).Where(field => layout.Shows(section, field));
        _reference = layout.DeltaReference;
        HeaderBand.ArrangeFields(Visible("Header", Fields.Header));
        FooterBand.ArrangeFields(Visible("Footer", Fields.Footer));
        Rows.Children.Clear();
        foreach (var field in Visible("Content", Fields.Content)) Rows.Children.Add(_rows[field.Id]);
        Rows.Visibility = Rows.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BodyPanel.Visibility = Rows.Children.Count > 0 || FooterBand.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
        HeaderPanel.Visibility = HeaderBand.Visibility;
        HeaderPanel.Margin = new Thickness(0, 0, 0, BodyPanel.Visibility == Visibility.Visible ? 3 : 0);
        Paint();
    }

    internal void Update(DeltaSnapshot delta)
    {
        if (_delta == delta) return;
        _delta = delta;
        Paint();
    }

    private void Paint()
    {
        HeaderBand.Update(_delta, _reference);
        FooterBand.Update(_delta, _reference);

        ReferenceCaption.Text = DeltaBand.ReferenceText(_delta, _reference, withHolder: true);
        double? delta = _delta.DeltaTo(_reference);
        DeltaValue.Text = delta?.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture) ?? "—";
        DeltaValue.Foreground = delta is < -0.0005 ? Gain : delta is > 0.0005 ? Loss : (Brush)FindResource("TextBrush");
        double fill = delta is { } d ? Math.Min(1, Math.Abs(d) / BarRange) : 0;
        GainScale.ScaleX = delta < 0 ? fill : 0;
        LossScale.ScaleX = delta > 0 ? fill : 0;

        var sectors = _delta.Sectors;
        while (_sectors.Count > sectors.Count) _sectors.RemoveAt(_sectors.Count - 1);
        while (_sectors.Count < sectors.Count) _sectors.Add(new SectorCell());
        for (int i = 0; i < sectors.Count; i++) _sectors[i].Update(sectors[i]);
    }

    private static Brush Frozen(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>One sector box (and its delta below it).</summary>
    internal sealed class SectorCell : ObservableObject
    {
        private static readonly Brush None = Frozen(0xFF, 0xFF, 0xFF, 0x0F), Running = Frozen(0xFF, 0xFF, 0xFF, 0x1F),
            Purple = Frozen(0xA8, 0x55, 0xF7, 0x66), Green = Frozen(0x22, 0xC5, 0x5E, 0x59), Yellow = Frozen(0xEA, 0xB3, 0x08, 0x4D),
            PurpleText = Frozen(0xF3, 0xE8, 0xFF), GreenText = Frozen(0xDC, 0xFC, 0xE7), YellowText = Frozen(0xFE, 0xF9, 0xC3),
            PlainText = Frozen(0xF2, 0xF4, 0xF8), FaintText = Frozen(0x8A, 0x93, 0xA3);

        private string _label = "", _time = "—", _delta = "—";
        private Brush _fill = None, _timeBrush = FaintText, _deltaBrush = FaintText;
        private double _opacity = 1;

        public string Label { get => _label; private set => Set(ref _label, value); }
        public string Time { get => _time; private set => Set(ref _time, value); }
        public string Delta { get => _delta; private set => Set(ref _delta, value); }
        public Brush Fill { get => _fill; private set => Set(ref _fill, value); }
        public Brush TimeBrush { get => _timeBrush; private set => Set(ref _timeBrush, value); }
        public Brush DeltaBrush { get => _deltaBrush; private set => Set(ref _deltaBrush, value); }
        /// <summary>Sectors still ahead show the previous lap, dimmed.</summary>
        public double Opacity { get => _opacity; private set => Set(ref _opacity, value); }

        public void Update(SectorSplit split)
        {
            Label = "S" + split.Number.ToString(CultureInfo.InvariantCulture);
            bool running = split.State == SectorState.Running;
            Time = DeltaBand.LapTime(split.Time, running ? 1 : 2);
            (Fill, TimeBrush) = split.State switch
            {
                SectorState.SessionBest => (Purple, PurpleText),
                SectorState.PersonalBest => (Green, GreenText),
                SectorState.Slower => (Yellow, YellowText),
                SectorState.Running => (Running, PlainText),
                _ => (None, FaintText),
            };
            Opacity = split.FromPreviousLap ? 0.5 : 1;
            double? change = running ? null : split.Delta;
            Delta = change?.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) ?? "—";
            DeltaBrush = change is < -0.005 ? Gain : change is > 0.005 ? Loss : change.HasValue ? PlainText : FaintText;
        }
    }
}
