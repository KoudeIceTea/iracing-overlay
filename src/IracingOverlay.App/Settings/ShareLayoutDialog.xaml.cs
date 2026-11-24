using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace IracingOverlay.App.Settings;

public partial class ShareLayoutDialog : Window
{
    internal LayoutSession SourceSession { get; private set; }

    internal ShareLayoutDialog(string overlayName, IReadOnlyList<LayoutDifference> differences)
    {
        InitializeComponent();
        Prompt.Text = $"Choose a layout for {overlayName}";
        var view = CollectionViewSource.GetDefaultView(differences);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LayoutDifference.Section)));
        Differences.ItemsSource = view;
    }

    private void UseLayout(object sender, RoutedEventArgs e)
    {
        SourceSession = (LayoutSession)((Button)sender).Tag;
        DialogResult = true;
    }
}
