using System.Windows;
using System.Windows.Controls;

namespace IracingOverlay.App.Overlays;

/// <summary>All standings headings and driver rows use the same ordered column widths.</summary>
public class FieldGrid : Grid
{
    public static readonly DependencyProperty WidthsProperty = DependencyProperty.Register(
        nameof(Widths), typeof(GridLength[]), typeof(FieldGrid), new PropertyMetadata(null, WidthsChanged));

    public GridLength[]? Widths
    {
        get => (GridLength[]?)GetValue(WidthsProperty);
        set => SetValue(WidthsProperty, value);
    }

    private static void WidthsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var columns = ((FieldGrid)sender).ColumnDefinitions;
        columns.Clear();
        if (args.NewValue is GridLength[] widths)
            foreach (var width in widths) columns.Add(new ColumnDefinition { Width = width });
    }
}
