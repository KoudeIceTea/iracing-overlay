using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace IracingOverlay.App.Overlays;

public static class Converters
{
    /// <summary>Collapses an element when the bound string is null/empty (e.g. brake bias not available).</summary>
    public static readonly IValueConverter EmptyToCollapsed = new EmptyToCollapsedConverter();

    private sealed class EmptyToCollapsedConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
