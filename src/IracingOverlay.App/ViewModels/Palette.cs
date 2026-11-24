using System.Windows.Media;

namespace IracingOverlay.App.ViewModels;

/// <summary>Frozen, cached brushes so per-tick updates don't allocate.</summary>
public static class Palette
{
    private static readonly Dictionary<uint, SolidColorBrush> Cache = new();

    public static readonly Brush Text = FromArgb(0xFFF2F4F8);
    public static readonly Brush TextDim = FromArgb(0xFF8A93A3);
    public static readonly Brush Dark = FromArgb(0xFF0B0D12);
    public static readonly Brush Purple = FromArgb(0xFFC48BFF);     // class best lap
    public static readonly Brush Green = FromArgb(0xFF4ADE80);      // personal best / iRating gain
    public static readonly Brush Red = FromArgb(0xFFF87171);        // iRating loss
    public static readonly Brush LapsAhead = FromArgb(0xFF9ECCFF);
    public static readonly Brush LapsBehind = FromArgb(0xFFFFB2A9);
    public static readonly Brush AheadRow = FromArgb(0x63396AB8);
    public static readonly Brush BehindRow = FromArgb(0x63AF4140);
    public static readonly Brush Transparent = Brushes.Transparent;

    public static Brush FocusRow { get; } = CreateFocusRow();

    public static Brush FromRgb(uint rgb) => FromArgb(0xFF000000 | (rgb & 0xFFFFFF));

    public static Brush WithAlpha(uint rgb, byte alpha) => FromArgb(((uint)alpha << 24) | (rgb & 0xFFFFFF));

    /// <summary>Mixes the color towards white; keeps dark license/class colors readable on dark glass.</summary>
    public static Brush Lighten(uint rgb, double amount) => FromRgb(LightenRgb(rgb, amount));

    public static uint LightenRgb(uint rgb, double amount)
    {
        uint Mix(uint c) => (uint)Math.Round(c + (255 - c) * amount);
        return (Mix((rgb >> 16) & 0xFF) << 16) | (Mix((rgb >> 8) & 0xFF) << 8) | Mix(rgb & 0xFF);
    }

    public static Brush FromArgb(uint argb)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(argb, out var b)) return b;
            b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            Cache[argb] = b;
            return b;
        }
    }

    /// <summary>Black or white text, whichever reads better on the given background.</summary>
    public static Brush ContrastText(uint rgb)
    {
        double r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
        return 0.299 * r + 0.587 * g + 0.114 * b > 150 ? Dark : Text;
    }

    private static Brush CreateFocusRow()
    {
        var b = new LinearGradientBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0);
        b.Freeze();
        return b;
    }
}
