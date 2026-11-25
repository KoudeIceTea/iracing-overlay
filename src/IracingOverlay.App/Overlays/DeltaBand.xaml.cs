using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using IracingOverlay.App.Settings;
using IracingOverlay.Core.Race;

namespace IracingOverlay.App.Overlays;

/// <summary>The delta overlay's header or footer: lap times and what the delta is compared with.</summary>
public partial class DeltaBand : UserControl
{
    private readonly Dictionary<string, FrameworkElement> _elements;

    public DeltaBand()
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

    internal void Update(DeltaSnapshot delta, DeltaReference reference)
    {
        Reference.Inlines.Clear();
        Reference.Inlines.Add(new System.Windows.Documents.Run("VS " + Short(reference) + " ")
            { FontSize = 10, FontWeight = FontWeights.Bold, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB4, 0xC0, 0xD0)) });
        Reference.Inlines.Add(new System.Windows.Documents.Run(ReferenceTime(delta, reference)));
        Current.Text = LapTime(delta.CurrentLap, 1);
        Last.Text = LapTime(delta.LastLap);
        Best.Text = LapTime(delta.BestLap);
        Optimal.Text = LapTime(delta.TheoreticalBest);
        SessionBest.Text = LapTime(delta.SessionBestLap);
    }

    private static string Short(DeltaReference reference) =>
        OverlayFields.DeltaReferences.First(r => r.Reference == reference).Short;

    /// <summary>The reference lap time; "≈" marks optimal laps added up from this app's sector times.</summary>
    private static string ReferenceTime(DeltaSnapshot delta, DeltaReference reference) =>
        delta.ReferenceTime(reference) is { } time
            ? (DeltaSnapshot.IsEstimate(reference) ? "≈" : "") + LapTime(time)
            : "—";

    /// <summary>"vs SESSION BEST 2:35.575 · Roman Korobkin": what the delta is measured against.</summary>
    internal static string ReferenceText(DeltaSnapshot delta, DeltaReference reference, bool withHolder)
    {
        string text = "vs " + Short(reference) + "  " + ReferenceTime(delta, reference);
        if (withHolder && reference == DeltaReference.SessionBestLap && delta.SessionBestDriver.Length > 0 && delta.SessionBestLap.HasValue)
            text += " · " + delta.SessionBestDriver;
        return text;
    }

    /// <summary>"1:23.456", or "58.123" under a minute.</summary>
    internal static string LapTime(double? seconds, int decimals = 3)
    {
        if (seconds is not { } value || value <= 0 || !double.IsFinite(value)) return "—";
        string fraction = "0." + new string('0', decimals);
        int minutes = (int)(value / 60);
        double rest = value - minutes * 60;
        // Round first so 59.9996 becomes "1:00.000" rather than "0:60.000".
        if (Math.Round(rest, decimals) >= 60) { minutes++; rest = 0; }
        return minutes > 0
            ? minutes.ToString(CultureInfo.InvariantCulture) + ":" + rest.ToString("0" + fraction, CultureInfo.InvariantCulture)
            : rest.ToString(fraction, CultureInfo.InvariantCulture);
    }
}
