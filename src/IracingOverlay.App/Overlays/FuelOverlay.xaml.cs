using System.Globalization;
using System.Windows;
using IracingOverlay.App.Settings;
using IracingOverlay.App.ViewModels;
using IracingOverlay.Core.Race;

namespace IracingOverlay.App.Overlays;

public partial class FuelOverlay : OverlayWindow
{
    internal override OverlayFields Fields => OverlayFields.Fuel;
    private FuelSnapshot _fuel = new();
    private FuelRow[] _rows = [];

    public FuelOverlay() => InitializeComponent();

    internal override void ApplyLayout(TableLayout layout)
    {
        IEnumerable<OverlayField> Visible(string section, IReadOnlyList<OverlayField> fields) =>
            layout.Ordered(section, fields).Where(field => layout.Shows(section, field));
        HeaderBand.ArrangeFields(Visible("Header", Fields.Header));
        FooterBand.ArrangeFields(Visible("Footer", Fields.Footer));
        _rows = Visible("Content", Fields.Content)
            .Select(field => new FuelRow(field.Id, field.Label)).ToArray();
        UsageRows.ItemsSource = _rows;
        Table.Visibility = _rows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BodyPanel.Visibility = _rows.Length > 0 || FooterBand.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        HeaderPanel.Visibility = HeaderBand.Visibility;
        HeaderPanel.Margin = new Thickness(0, 0, 0, BodyPanel.Visibility == Visibility.Visible ? 3 : 0);
        PaintValues();
    }

    internal void Update(FuelSnapshot fuel)
    {
        if (_fuel == fuel) return;
        _fuel = fuel;
        PaintValues();
    }

    private void PaintValues()
    {
        HeaderBand.Update(_fuel);
        FooterBand.Update(_fuel);
        foreach (var row in _rows) row.Update(_fuel);
    }
}

internal sealed class FuelRow(string id, string label) : ObservableObject
{
    public string Label { get; } = label;
    private string _usage = "—", _remaining = "—", _pits = "—";
    public string Usage { get => _usage; private set => Set(ref _usage, value); }
    public string Remaining { get => _remaining; private set => Set(ref _remaining, value); }
    /// <summary>Races only: fuel stops still needed at this consumption (0 = you can make it to the end).</summary>
    public string PitsToEnd { get => _pits; private set => Set(ref _pits, value); }

    public void Update(FuelSnapshot fuel)
    {
        double? burn = id switch
        {
            "Last" => fuel.Last, "Five" => fuel.Last5, "Ten" => fuel.Last10, "Personal" => fuel.Personal,
            "Min" => fuel.Minimum, "Max" => fuel.Maximum,
            _ => null,
        };
        Usage = burn?.ToString("0.00", CultureInfo.InvariantCulture) ?? "—";
        Remaining = burn > 0 && fuel.Level.HasValue
            ? (fuel.Level.Value / burn.Value).ToString("0.0", CultureInfo.InvariantCulture) : "—";
        // Fuel still needed beyond what's on board, in full tanks, rounded up.
        PitsToEnd = burn > 0 && fuel.LapsToGo is double toGo && fuel.Level is double level && fuel.Capacity is > 0 and double tank
            ? Math.Max(0, Math.Ceiling((toGo * burn.Value - level) / tank - 1e-9)).ToString(CultureInfo.InvariantCulture) : "—";
    }
}
