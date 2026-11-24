using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using IracingOverlay.App.ViewModels;

namespace IracingOverlay.App.Settings;

public sealed class OverlaySettings : TableLayout
{
    public bool Enabled { get; set; } = true;
    public bool FollowSpectatedCar { get; set; }
    /// <summary>NaN = not positioned yet; a default is computed on first show.</summary>
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    /// <summary>Distance from the monitor bottom in DIPs when this overlay is anchored there.</summary>
    public double? BottomGap { get; set; }
    public double Scale { get; set; } = 1.0;
    /// <summary>How driver names are written in this overlay.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<NameFormat>))]
    public NameFormat NameFormat { get; set; } = NameFormat.Full;
    /// <summary>
    /// Team races: show the team name (large) above the driver. Null = the overlay's default
    /// (on for standings, off for relative). Sessions without teams always show just the driver.
    /// </summary>
    public bool? ShowTeamNames { get; set; }
    internal bool TeamNames(bool defaultOn) => ShowTeamNames ?? defaultOn;
    // Null represents one shared layout. The inherited fields keep the original settings file format.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<LayoutSession, TableLayout>? SessionLayouts { get; set; }

    internal TableLayout LayoutFor(LayoutSession session)
    {
        if (SessionLayouts == null) return this;
        if (!SessionLayouts.TryGetValue(session, out var layout) || layout == null)
            SessionLayouts[session] = layout = Copy();
        return layout;
    }

    internal void SplitLayouts() =>
        SessionLayouts ??= Enum.GetValues<LayoutSession>().ToDictionary(session => session, _ => Copy());

    internal void ShareLayout(LayoutSession source)
    {
        var selected = LayoutFor(source);
        FieldOverrides = selected.FieldOverrides;
        FieldOrder = selected.FieldOrder;
        ShowColumnNames = selected.ShowColumnNames;
        SessionLayouts = null;
    }
}

public enum LayoutSession { Practice, Qualifying, Race }

public class TableLayout
{
    public Dictionary<string, bool> FieldOverrides { get; set; } = [];
    public Dictionary<string, string[]> FieldOrder { get; set; } = [];

    public bool ShowColumnNames { get; set; } = true;

    internal TableLayout Copy() => new()
    {
        FieldOverrides = new(FieldOverrides),
        FieldOrder = FieldOrder.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()),
        ShowColumnNames = ShowColumnNames,
    };

    // Retain the visibility preference from the former standings penalty column.
    internal bool ShowPenalties
    {
        get => FieldOverrides.GetValueOrDefault("Content.Penalty", true);
        set
        {
            if (value) FieldOverrides.Remove("Content.Penalty");
            else FieldOverrides["Content.Penalty"] = false;
        }
    }

    internal IReadOnlyList<OverlayField> Ordered(string section, IReadOnlyList<OverlayField> defaults)
    {
        if (!FieldOrder.TryGetValue(section, out var saved) || saved == null) return defaults;
        var remaining = defaults.ToDictionary(field => field.Id);
        var ordered = new List<OverlayField>(defaults.Count);
        foreach (var id in saved)
            if (id != null && remaining.Remove(id, out var field)) ordered.Add(field);
        // Fields added in an update keep their default place: right after the field they follow by default
        // (or first). Retired IDs and duplicates in the saved order are ignored.
        for (int i = 0; i < defaults.Count; i++)
        {
            var field = defaults[i];
            if (!remaining.ContainsKey(field.Id)) continue;
            int after = -1;
            for (int j = i - 1; j >= 0 && after < 0; j--) after = ordered.IndexOf(defaults[j]);
            ordered.Insert(after + 1, field);
            remaining.Remove(field.Id);
        }
        return ordered;
    }

    internal bool Shows(string section, OverlayField field) =>
        FieldOverrides.GetValueOrDefault(section + "." + field.Id, field.DefaultOn);
}

public sealed class AppSettings
{
    public OverlaySettings Standings { get; set; } = new();
    public OverlaySettings Relative { get; set; } = new();
    public OverlaySettings Fuel { get; set; } = new();
    public int UpdateHz { get; set; } = 10;
    public bool HideWhenSimulatorUnfocused { get; set; }
    /// <summary>Real blur of the game behind the glass panels. Turn off if you want the last bit of GPU time.</summary>
    public bool GlassBlur { get; set; } = true;
    /// <summary>Rows for your own class (leaders + window around you).</summary>
    public int StandingsFocusClassRows { get; set; } = 10;
    /// <summary>Rows (the class leaders) for every other class in multiclass sessions.</summary>
    public int StandingsOtherClassRows { get; set; } = 3;
    // Read the old global preference into the shared Standings layout when upgrading.
    [JsonPropertyName("ShowStandingsColumnNames"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyColumnNames { get; set; }
    public bool AnimateStandingsChanges { get; set; } = true;
    public int RelativeCarsAhead { get; set; } = 4;
    public int RelativeCarsBehind { get; set; } = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IracingOverlay", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new();
                if (settings.LegacyColumnNames is bool show) settings.Standings.ShowColumnNames = show;
                settings.LegacyColumnNames = null;
                return settings;
            }
        }
        catch (Exception)
        {
            // Corrupt settings: fall back to defaults rather than refusing to start.
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
