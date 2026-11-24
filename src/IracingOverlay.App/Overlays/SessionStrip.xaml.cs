using System.Windows.Controls;
using System.Windows;
using IracingOverlay.App.Settings;

namespace IracingOverlay.App.Overlays;

public partial class SessionStrip : UserControl
{
    private readonly Dictionary<string, FrameworkElement> _elements;

    public SessionStrip()
    {
        InitializeComponent();
        _elements = LeadingFields.Children.Cast<FrameworkElement>().Concat(TrailingFields.Children.Cast<FrameworkElement>())
            .ToDictionary(element => (string)element.Tag);
    }

    internal void ArrangeFields(IReadOnlyList<OverlayField> visible, bool footer)
    {
        LeadingFields.Children.Clear();
        TrailingFields.Children.Clear();
        // Keep trailing clocks aligned to the right only while they are last in the chosen order.
        int split = visible.Count;
        while (split > 0 && (visible[split - 1].Id is "Clock" or "GameTime" || footer && visible[split - 1].Id == "Remaining"))
            split--;
        for (int i = 0; i < visible.Count; i++)
        {
            Panel host = i < split ? LeadingFields : TrailingFields;
            host.Children.Add(_elements[visible[i].Id]);
        }
    }
}
