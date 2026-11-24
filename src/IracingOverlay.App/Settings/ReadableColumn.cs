using System.Windows;
using System.Windows.Controls;

namespace IracingOverlay.App.Settings;

/// <summary>
/// Vertical stack that fills the available width up to <see cref="ColumnWidth"/> and stays left-aligned.
/// Keeps settings cards a consistent, readable width instead of stretching across a wide window.
/// (A plain MaxWidth on a stretched element would centre it, and a Left-aligned one would shrink to content.)
/// </summary>
public sealed class ReadableColumn : StackPanel
{
    public double ColumnWidth { get; set; } = 720;

    protected override Size MeasureOverride(Size constraint)
    {
        double width = Math.Min(constraint.Width, ColumnWidth);
        var desired = base.MeasureOverride(new Size(width, constraint.Height));
        return new Size(double.IsInfinity(width) ? desired.Width : width, desired.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = new Size(Math.Min(arrangeSize.Width, ColumnWidth), arrangeSize.Height);
        base.ArrangeOverride(size);
        return size;
    }
}
