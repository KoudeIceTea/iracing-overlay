using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using IracingOverlay.App.Settings;
using IracingOverlay.Core.Race;

namespace IracingOverlay.App.Overlays;

public partial class FuelBand : UserControl
{
    private readonly Dictionary<string, FrameworkElement> _elements;

    public FuelBand()
    {
        InitializeComponent();
        _elements = Fields.Children.Cast<FrameworkElement>().ToDictionary(element => (string)element.Tag);
    }

    internal void ArrangeFields(IEnumerable<OverlayField> fields)
    {
        Fields.Children.Clear();
        foreach (var field in fields) Fields.Children.Add(_elements[field.Id]);
        Visibility = Fields.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void Update(FuelSnapshot fuel)
    {
        static string Litres(double? value) => value?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—";
        Level.Text = $"{Litres(fuel.Level)} / {Litres(fuel.Capacity)} L";
        Fill.Width = fuel.Level.HasValue && fuel.Capacity > 0 ? 38 * Math.Clamp(fuel.Level.Value / fuel.Capacity.Value, 0, 1) : 0;
        Range.Text = fuel.Level.HasValue && fuel.Minimum > 0 && fuel.Maximum > 0
            ? FormattableString.Invariant($"{fuel.Level / fuel.Maximum:0.0}–{fuel.Level / fuel.Minimum:0.0}") : "—";
        // What the consumption figures are based on: the 10-lap window starts with iRacing's own history for this
        // car and track (fueldata.ini), and every lap you drive this session replaces the oldest history lap.
        string session = $"{fuel.Count} session lap{(fuel.Count == 1 ? "" : "s")}";
        string basis =
            fuel.Count > 0 && fuel.HistoricalCount > 0 ? $"{session} + {fuel.HistoricalCount} from history"
            : fuel.Count > 0 ? session
            : fuel.HistoricalCount > 0 ? $"{fuel.HistoricalCount} lap{(fuel.HistoricalCount == 1 ? "" : "s")} from iRacing history"
            : "";
        Samples.Text =
            fuel.InGarage ? (basis.Length > 0 ? $"Setup fuel · {basis}" : "Setup fuel (not in car)")
            : basis.Length > 0 ? basis
            : fuel.Level.HasValue ? "Learning lap consumption" : "Waiting for fuel telemetry";
    }
}
