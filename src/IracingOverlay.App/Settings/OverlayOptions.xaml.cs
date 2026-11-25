using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using IracingOverlay.App.Overlays;
using IracingOverlay.App.ViewModels;
using IracingOverlay.Core.Race;

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
        else if (overlay is DeltaOverlay)
        {
            DriverTitle.Visibility = DriverGroup.Visibility = Visibility.Collapsed;
            ContentHint.Text = "Choose what the overlay shows and drag to reorder it. Sector colours: purple is the best in your class, " +
                "green your own best, yellow slower. Sectors still ahead show your previous lap, dimmed.";
            BindDeltaOptions();
            BindSessionToggles();
        }
        else if (overlay is RadarOverlay)
        {
            DriverTitle.Visibility = DriverGroup.Visibility = Visibility.Collapsed;
            HeaderTab.Visibility = FooterTab.Visibility = Visibility.Collapsed;
            ContentHint.Text = "Red bars show a car beside you, as reported by iRacing's spotter, moved along your car by how far " +
                "ahead or behind it is. A yellow arc shows a car close behind or ahead: iRacing doesn't report where cars are " +
                "across the track, so the arc covers the full width.";
            BindRadarOptions(app);
            BindSessionToggles();
        }
        else BindDriverOptions(app, overlay);
        BindToggle(SyncLayouts, overlay.OverlaySettings.SessionLayouts == null, ChangeSync);
        RefreshEditor();
        UpdateScale();
    }

    private (CheckBox Toggle, LayoutSession Session)[] _sessionToggles = [];

    private void BindRadarOptions(App app)
    {
        RadarTitle.Visibility = RadarGroup.Visibility = Visibility.Visible;
        int[] ranges = [0, 6, 8, 10, 15, 20, 30];
        foreach (int metres in ranges.Append(Math.Max(0, app.Settings.RadarWarningDistance)).Distinct().Order())
            RadarRange.Items.Add(new ComboBoxItem { Content = metres == 0 ? "Off" : $"{metres} m", Tag = metres });
        RadarRange.SelectedItem = RadarRange.Items.Cast<ComboBoxItem>().First(item => (int)item.Tag == Math.Max(0, app.Settings.RadarWarningDistance));
        RadarRange.SelectionChanged += (_, _) =>
        {
            if (RadarRange.SelectedItem is ComboBoxItem { Tag: int metres }) app.SetRadarWarningDistance(metres);
        };
        BindToggle(RadarHideWhenClear, app.Settings.RadarHideWhenClear, app.SetRadarHideWhenClear);
    }

    private void BindDeltaOptions()
    {
        DeltaReferenceCard.Visibility = Visibility.Visible;
        foreach (var (reference, label, _) in OverlayFields.DeltaReferences)
            DeltaReferenceChoice.Items.Add(new ComboBoxItem { Content = label, Tag = reference });
        DeltaReferenceChoice.SelectionChanged += (_, _) =>
        {
            if (_refreshing || DeltaReferenceChoice.SelectedItem is not ComboBoxItem { Tag: DeltaReference reference }) return;
            EditedLayout.DeltaReference = reference;
            SaveLayout();
        };
    }

    private void BindSessionToggles()
    {
        SessionsRow.Visibility = Visibility.Visible;
        _sessionToggles = [(ShowInPractice, LayoutSession.Practice), (ShowInQualifying, LayoutSession.Qualifying),
            (ShowInRace, LayoutSession.Race)];
        foreach (var (toggle, session) in _sessionToggles)
        {
            void Changed(object sender, RoutedEventArgs e)
            {
                if (_refreshing) return;
                _app.SetOverlaySession(_overlay, session, toggle.IsChecked == true);
                // No session left means the overlay is off; turning it back on then shows it everywhere again.
                if (_sessionToggles.All(t => t.Toggle.IsChecked != true)) ShowOverlay.IsChecked = false;
            }
            toggle.Checked += Changed;
            toggle.Unchecked += Changed;
        }
        RefreshSessions();
    }

    /// <summary>The session toggles follow the overlay switch: all off (and locked) while the overlay is off.</summary>
    private void RefreshSessions()
    {
        if (_sessionToggles.Length == 0) return;
        var settings = _overlay.OverlaySettings;
        bool wasRefreshing = _refreshing;
        _refreshing = true;
        foreach (var (toggle, session) in _sessionToggles)
        {
            toggle.IsEnabled = settings.Enabled;
            toggle.IsChecked = settings.Enabled && settings.ShowsIn(session);
        }
        _refreshing = wasRefreshing;
        SessionsHint.Text = settings.Enabled
            ? "The overlay appears only in the session types switched on here."
            : "Turn the overlay on to choose its sessions.";
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
        DeltaReferenceChoice.SelectedItem = DeltaReferenceChoice.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is DeltaReference reference && reference == EditedLayout.DeltaReference);
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

    private void OnVisibilityChanged(object sender, RoutedEventArgs e)
    {
        bool enabled = ShowOverlay.IsChecked == true;
        var settings = _overlay.OverlaySettings;
        if (enabled && _sessionToggles.Length > 0 && !Enum.GetValues<LayoutSession>().Any(settings.ShowsIn))
            foreach (var session in Enum.GetValues<LayoutSession>()) settings.SetShownIn(session, true);
        _app.SetOverlayEnabled(_overlay, enabled);
        RefreshSessions();
    }

    private void OnSmaller(object sender, RoutedEventArgs e) => ChangeScale(-1);
    private void OnLarger(object sender, RoutedEventArgs e) => ChangeScale(1);

    private void ChangeScale(int steps)
    {
        _overlay.AdjustScale(steps);
        _app.Settings.Save();
    }
}
