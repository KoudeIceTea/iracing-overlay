using System.Globalization;
using System.Windows.Media;
using IracingOverlay.Core.Race;

namespace IracingOverlay.App.ViewModels;

/// <summary>A class divider in the multiclass standings ("LMP2 · 11 cars · SoF 2960").</summary>
public sealed class ClassHeaderViewModel : ObservableObject
{
    private string _className = "", _details = "";
    private bool _isFirst;
    public bool IsFirst { get => _isFirst; private set => Set(ref _isFirst, value); }
    private Brush _classBackground = Palette.Transparent, _classForeground = Palette.Text, _lineBrush = Palette.Transparent;

    public string ClassName { get => _className; private set => Set(ref _className, value); }
    public string Details { get => _details; private set => Set(ref _details, value); }
    public Brush ClassBackground { get => _classBackground; private set => Set(ref _classBackground, value); }
    public Brush ClassForeground { get => _classForeground; private set => Set(ref _classForeground, value); }
    public Brush LineBrush { get => _lineBrush; private set => Set(ref _lineBrush, value); }

    public void Update(ClassStandings c, bool first)
    {
        IsFirst = first;
        ClassName = c.ClassName.ToUpperInvariant();
        Details = c.StrengthOfField > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{c.CarCount}  ·  SoF {c.StrengthOfField}")
            : c.CarCount.ToString(CultureInfo.InvariantCulture);
        ClassBackground = Palette.FromRgb(c.ClassColor);
        ClassForeground = Palette.ContrastText(c.ClassColor);
        LineBrush = Palette.WithAlpha(c.ClassColor, 0x55);
    }
}
