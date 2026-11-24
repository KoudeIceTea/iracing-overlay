using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using IracingOverlay.App.Overlays;
using IracingOverlay.App.ViewModels;

namespace IracingOverlay.App.Settings;

public partial class OverlayOptions : UserControl
{
    private OverlayWindow _overlay = null!;
    private App _app = null!;

    private LayoutSession _editingSession;
    private bool _refreshing;
    private TableLayout EditedLayout => _overlay.OverlaySettings.LayoutFor(_editingSession);

    public OverlayOptions() => InitializeComponent();

    internal void Attach(App app, OverlayWindow overlay)
    {
        _app = app;
        _overlay = overlay;
        if (overlay is StandingsOverlay)
        {
            OtherRows.ItemsSource = Enumerable.Range(0, 73).Append(app.Settings.StandingsOtherClassRows).Distinct().Order();
            OtherRows.SelectedItem = app.Settings.StandingsOtherClassRows;
            OtherRows.SelectionChanged += (_, _) =>
            {
                if (OtherRows.SelectedItem is not int count) return;
                app.Settings.StandingsOtherClassRows = count;
                app.ApplyRaceOptions();
            };
            BindToggle(AnimatePositions, app.Settings.AnimateStandingsChanges, enabled =>
            {
                app.Settings.AnimateStandingsChanges = enabled;
                app.Settings.Save();
            });
            BindToggle(ColumnNames, EditedLayout.ShowColumnNames, enabled =>
            {
                if (_refreshing) return;
                EditedLayout.ShowColumnNames = enabled;
                SaveLayout();
            });
            BindToggle(PenaltyBadges, EditedLayout.ShowPenalties, enabled =>
            {
                if (_refreshing) return;
                EditedLayout.ShowPenalties = enabled;
                SaveLayout();
            });
        }
        else
        {
            StandingsOptions.Visibility = ColumnNamesCard.Visibility = Visibility.Collapsed;
        }
        ShowOverlayTitle.Text = $"Show {overlay.Title}";
        AutomationProperties.SetName(ShowOverlay, ShowOverlayTitle.Text);
        ShowOverlay.IsChecked = overlay.OverlaySettings.Enabled;
        ShowOverlay.Checked += OnVisibilityChanged;
        ShowOverlay.Unchecked += OnVisibilityChanged;
        overlay.ScaleChanged += UpdateScale;
        if (overlay is FuelOverlay)
        {
            DriverTitle.Visibility = DriverGroup.Visibility = Visibility.Collapsed;
            ContentHint.Text = "Choose the consumption rows and drag to reorder them. Team session includes laps observed on this PC and appears in team events.";
        }
        else BindDriverOptions(app, overlay);
        BindToggle(SyncLayouts, overlay.OverlaySettings.SessionLayouts == null, ChangeSync);
        RefreshEditor();
        UpdateScale();
    }

    private void BindDriverOptions(App app, OverlayWindow overlay)
    {
        BindToggle(FollowCamera, overlay.OverlaySettings.FollowSpectatedCar, enabled =>
        {
            overlay.OverlaySettings.FollowSpectatedCar = enabled;
            app.ApplyRaceOptions();
        });
        foreach (var (format, label) in DriverNames.Choices)
            NameFormatChoice.Items.Add(new ComboBoxItem { Content = label, Tag = format });
        NameFormatChoice.SelectedIndex = DriverNames.Choices.ToList().FindIndex(c => c.Format == overlay.OverlaySettings.NameFormat);
        NameFormatChoice.SelectionChanged += (_, _) =>
        {
            if (NameFormatChoice.SelectedItem is ComboBoxItem { Tag: NameFormat format })
                _app.SetNameFormat(_overlay, format);
        };
        BindToggle(ShowTeams, app.ShowsTeamNames(overlay), enabled => app.SetShowTeamNames(overlay, enabled));
    }

    private void ChangeSync(bool sync)
    {
        if (_refreshing) return;
        var settings = _overlay.OverlaySettings;
        if (sync)
        {
            var source = _editingSession;
            var differences = _overlay.Fields.CompareLayouts(settings);
            if (differences.Count > 0)
            {
                var dialog = new ShareLayoutDialog(_overlay.Title, differences) { Owner = Window.GetWindow(this) };
                if (dialog.ShowDialog() != true)
                {
                    RefreshEditor();
                    return;
                }
                source = dialog.SourceSession;
            }
            settings.ShareLayout(source);
        }
        else settings.SplitLayouts();
        RefreshEditor();
        SaveLayout();
    }

    private void OnSessionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl { SelectedItem: TabItem { Tag: LayoutSession session } } || session == _editingSession) return;
        _editingSession = session;
        if (_overlay != null) RefreshEditor();
    }

    private void SaveLayout()
    {
        _overlay.ApplyFieldSettings();
        _app.Settings.Save();
    }

    private void RefreshEditor()
    {
        _refreshing = true;
        bool shared = _overlay.OverlaySettings.SessionLayouts == null;
        SectionTabs.Tag = shared ? null : "Separate";
        SyncLayouts.IsChecked = shared;
        ColumnNames.IsChecked = EditedLayout.ShowColumnNames;
        PenaltyBadges.IsChecked = EditedLayout.ShowPenalties;
        AddChoices(ContentChoices, "Content", _overlay.Fields.Content);
        AddChoices(HeaderChoices, "Header", _overlay.Fields.Header);
        AddChoices(FooterChoices, "Footer", _overlay.Fields.Footer);
        _refreshing = false;
    }

    private static void BindToggle(CheckBox toggle, bool initial, Action<bool> changed)
    {
        toggle.IsChecked = initial;
        toggle.Checked += (_, _) => changed(true);
        toggle.Unchecked += (_, _) => changed(false);
    }

    private void AddChoices(ReorderableFields panel, string section, IReadOnlyList<OverlayField> fields)
    {
        panel.ResetFields();
        var settings = EditedLayout;
        foreach (var field in settings.Ordered(section, fields))
        {
            var toggle = new CheckBox
            {
                Content = field.Label,
                Margin = new Thickness(0, 6, 0, 6),
                IsChecked = settings.Shows(section, field),
            };
            void Changed(object sender, RoutedEventArgs args)
            {
                bool show = toggle.IsChecked == true;
                string key = section + "." + field.Id;
                if (show == field.DefaultOn) settings.FieldOverrides.Remove(key);
                else settings.FieldOverrides[key] = show;
                SaveLayout();
            }
            toggle.Checked += Changed;
            toggle.Unchecked += Changed;
            panel.AddField(field.Id, toggle);
        }
        panel.OrderChanged += () =>
        {
            settings.FieldOrder[section] = panel.Order;
            SaveLayout();
        };
    }

    private void UpdateScale()
    {
        double scale = _overlay.OverlaySettings.Scale;
        ScaleValue.Text = $"{scale:P0}";
        Smaller.IsEnabled = scale > OverlayWindow.MinScale;
        Larger.IsEnabled = scale < OverlayWindow.MaxScale;
    }

    private void OnVisibilityChanged(object sender, RoutedEventArgs e) =>
        _app.SetOverlayEnabled(_overlay, ShowOverlay.IsChecked == true);

    private void OnSmaller(object sender, RoutedEventArgs e) => ChangeScale(-1);
    private void OnLarger(object sender, RoutedEventArgs e) => ChangeScale(1);

    private void ChangeScale(int steps)
    {
        _overlay.AdjustScale(steps);
        _app.Settings.Save();
    }
}
