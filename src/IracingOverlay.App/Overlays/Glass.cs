using System.Windows;

namespace IracingOverlay.App.Overlays;

/// <summary>
/// Marks an element (normally a rounded Border) as a glass surface. The overlay window clips itself
/// to the union of its glass surfaces, so the blurred backdrop only appears inside those shapes.
/// </summary>
public static class Glass
{
    public static readonly DependencyProperty IsSurfaceProperty = DependencyProperty.RegisterAttached(
        "IsSurface", typeof(bool), typeof(Glass), new PropertyMetadata(false, SurfaceChanged));

    private static void SurfaceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement surface) return;
        if (args.NewValue is true)
        {
            surface.Loaded += SurfaceLoaded;
            if (surface.IsLoaded) (Window.GetWindow(surface) as OverlayWindow)?.RegisterSurface(surface);
        }
        else
        {
            surface.Loaded -= SurfaceLoaded;
            (Window.GetWindow(surface) as OverlayWindow)?.UnregisterSurface(surface);
        }
    }

    private static void SurfaceLoaded(object sender, RoutedEventArgs e)
    {
        var surface = (FrameworkElement)sender;
        (Window.GetWindow(surface) as OverlayWindow)?.RegisterSurface(surface);
    }

    public static bool GetIsSurface(DependencyObject d) => (bool)d.GetValue(IsSurfaceProperty);
    public static void SetIsSurface(DependencyObject d, bool value) => d.SetValue(IsSurfaceProperty, value);
}
