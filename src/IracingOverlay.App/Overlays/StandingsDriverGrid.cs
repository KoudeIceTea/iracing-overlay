using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using IracingOverlay.App.ViewModels;

namespace IracingOverlay.App.Overlays;

public sealed class StandingsDriverGrid : FieldGrid
{
    private readonly TranslateTransform _slide = new();
    private RowViewModel? _row;

    public StandingsDriverGrid()
    {
        RenderTransform = _slide;
        Loaded += (_, _) => Bind(DataContext as RowViewModel);
        Unloaded += (_, _) => Bind(null);
        DataContextChanged += (_, e) => { if (IsLoaded) Bind(e.NewValue as RowViewModel); };
    }

    private void Bind(RowViewModel? row)
    {
        if (_row != null) _row.PositionMotion -= Move;
        _row = row;
        if (_row != null) _row.PositionMotion += Move;
        Move(0);
    }

    private void Move(int places)
    {
        var animation = places == 0 || !SystemParameters.ClientAreaAnimation ? null
            : new DoubleAnimation(Math.Clamp(places, -2, 2) * ActualHeight, 0, TimeSpan.FromMilliseconds(350))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            };
        _slide.BeginAnimation(TranslateTransform.YProperty, animation);
    }
}
