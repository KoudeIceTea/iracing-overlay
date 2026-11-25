using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using IracingOverlay.App.Settings;
using IracingOverlay.Core.Race;

namespace IracingOverlay.App.Overlays;

/// <summary>
/// A transparent proximity indicator around your car. A car the spotter reports beside you shows as a red bar on
/// that side, sliding along your car with how far ahead or behind it is. A close car that isn't beside you shows as
/// a yellow arc behind or ahead, nearer and brighter as it closes in. iRacing doesn't say where cars are across the
/// track, so the arc covers the full width.
/// </summary>
public partial class RadarOverlay : OverlayWindow
{
    private const double CarPixels = 64;                                  // your car's drawn length
    private const double PixelsPerMetre = CarPixels / RadarTracker.CarLength;
    private const double ArcNear = 0.5, ArcFar = 1.5;                     // arc scale (radius 100 px) close / at the warning distance

    internal override OverlayFields Fields => OverlayFields.Radar;
    protected override bool UsesGlass => false;
    private RadarSnapshot _radar = new();
    private double _range = 10;
    private bool _hideWhenClear = true, _showAhead = true, _showBehind = true;

    public RadarOverlay() => InitializeComponent();

    /// <summary>
    /// Warning distance in metres: a car closer than this ahead or behind shows a yellow arc. 0 turns the warnings
    /// off, so the overlay only appears for a car beside you.
    /// </summary>
    internal double Range
    {
        get => _range;
        set
        {
            _range = value <= 0 ? 0 : Math.Clamp(value, RadarTracker.CarLength + 1, RadarTracker.MaxDistance);
            Paint(animate: false);
        }
    }

    /// <summary>Hide the overlay while no car is close (it always shows in Edit mode).</summary>
    internal bool HideWhenClear
    {
        get => _hideWhenClear;
        set
        {
            _hideWhenClear = value;
            Paint(animate: false);
        }
    }

    /// <summary>How long bars and arcs glide to their new position: the telemetry update interval.</summary>
    internal TimeSpan GlideTime { get; set; } = TimeSpan.FromMilliseconds(100);

    internal override void ApplyLayout(TableLayout layout)
    {
        bool Shows(string id) => layout.Shows("Content", Fields.Content.First(field => field.Id == id));
        Guides.Visibility = Shows("Guides") ? Visibility.Visible : Visibility.Collapsed;
        Car.Visibility = Shows("Car") ? Visibility.Visible : Visibility.Collapsed;
        _showAhead = Shows("Ahead");
        _showBehind = Shows("Behind");
        Paint(animate: false);
    }

    internal void Update(RadarSnapshot radar)
    {
        if (_radar == radar) return;
        _radar = radar;
        Paint(animate: true);
    }

    protected override void OnEditModeChanged() => Paint(animate: false);

    private void Paint(bool animate)
    {
        bool any = false;
        any |= Bar(Left1, _radar.Left, 0, animate);
        any |= Bar(Left2, _radar.Left, 1, animate);
        any |= Bar(Right1, _radar.Right, 0, animate);
        any |= Bar(Right2, _radar.Right, 1, animate);
        any |= Arc(BehindArc, _showBehind ? _radar.Behind : null, animate);
        any |= Arc(AheadArc, _showAhead ? _radar.Ahead : null, animate);

        var visibility = !_hideWhenClear || EditMode || any ? Visibility.Visible : Visibility.Hidden;
        if (Root.Visibility != visibility) Root.Visibility = visibility;
    }

    /// <summary>A red bar beside your car, moved forward or back by how far ahead or behind that car is.</summary>
    private bool Bar(Rectangle bar, IReadOnlyList<double> cars, int index, bool animate)
    {
        bool shown = index < cars.Count;
        bool appearing = shown && bar.Visibility != Visibility.Visible;
        bar.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (shown) Glide((TranslateTransform)bar.RenderTransform, TranslateTransform.YProperty, -cars[index] * PixelsPerMetre, animate && !appearing);
        return shown;
    }

    /// <summary>A yellow arc behind / ahead of your car: closer and brighter as the car closes in.</summary>
    private bool Arc(Path arc, double? distance, bool animate)
    {
        bool shown = _range > 0 && distance is { } d && d <= _range;
        bool appearing = shown && arc.Visibility != Visibility.Visible;
        arc.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown) return false;
        // 0 when it overlaps your car, 1 at the warning distance.
        double far = Math.Clamp((distance!.Value - RadarTracker.CarLength) / (_range - RadarTracker.CarLength), 0, 1);
        double scale = ArcNear + (ArcFar - ArcNear) * far;
        arc.Opacity = 1 - 0.65 * far;
        arc.StrokeThickness = 16 / scale; // the same drawn thickness at any radius
        var transform = (ScaleTransform)arc.RenderTransform;
        Glide(transform, ScaleTransform.ScaleXProperty, scale, animate && !appearing);
        Glide(transform, ScaleTransform.ScaleYProperty, scale, animate && !appearing);
        return true;
    }

    private void Glide(Animatable target, DependencyProperty property, double to, bool animate)
    {
        if (!animate)
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, to);
            return;
        }
        target.BeginAnimation(property, new DoubleAnimation(to, new Duration(GlideTime)), HandoffBehavior.SnapshotAndReplace);
    }
}
