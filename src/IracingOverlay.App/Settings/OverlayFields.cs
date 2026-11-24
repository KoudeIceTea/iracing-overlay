namespace IracingOverlay.App.Settings;

internal sealed record OverlayField(string Id, string Label, bool DefaultOn = true, int? ColumnWidth = null);
internal sealed record LayoutDifference(string Section, string Setting, string Practice, string Qualifying, string Race);

internal sealed class OverlayFields
{
    public static readonly OverlayFields Standings = new(true);
    public static readonly OverlayFields Relative = new(false);
    public static readonly OverlayFields Fuel = new();
    public IReadOnlyList<OverlayField> Content { get; }
    public IReadOnlyList<OverlayField> Header { get; }
    public IReadOnlyList<OverlayField> Footer { get; }

    internal List<LayoutDifference> CompareLayouts(OverlaySettings settings)
    {
        TableLayout[] layouts = [settings.LayoutFor(LayoutSession.Practice),
            settings.LayoutFor(LayoutSession.Qualifying), settings.LayoutFor(LayoutSession.Race)];
        var differences = new List<LayoutDifference>();

        void Compare(string section, string label, Func<int, string> value)
        {
            var row = new LayoutDifference(section, label, value(0), value(1), value(2));
            if (row.Practice != row.Qualifying || row.Practice != row.Race) differences.Add(row);
        }

        static string Visibility(bool shown) => shown ? "Shown" : "Hidden";
        if (this == Standings)
        {
            Compare("Content", "Column names", i => Visibility(layouts[i].ShowColumnNames));
            Compare("Content", "Penalty badges", i => Visibility(layouts[i].ShowPenalties));
        }

        foreach (var (section, fields) in new[] { ("Content", Content), ("Header", Header), ("Footer", Footer) })
        {
            // Use the same order and defaults as the editor, including positions of hidden fields.
            var orders = layouts.Select(layout => layout.Ordered(section, fields).ToList()).ToArray();
            foreach (var field in fields)
            {
                var positions = orders.Select(order => order.IndexOf(field) + 1).ToArray();
                bool moved = positions[0] != positions[1] || positions[0] != positions[2];
                Compare(section, field.Label, i =>
                {
                    var state = Visibility(layouts[i].Shows(section, field));
                    return moved ? $"{state} · #{positions[i]}" : state;
                });
            }
        }
        return differences;
    }

    private OverlayFields()
    {
        Content = [new("Last", "Last lap"), new("Five", "Last 5 laps"), new("Ten", "Last 10 laps"),
            new("Personal", "My session"), new("Min", "Lowest usage", false),
            new("Max", "Highest usage", false)];
        Header = [new("Title", "Fuel label"), new("Level", "Fuel gauge and tank level"), new("Range", "Remaining lap range"),
            new("Samples", "Measured lap count", false)];
        Footer = Header.Select(field => field with { DefaultOn = field.Id == "Samples" }).ToArray();
    }

    private OverlayFields(bool standings)
    {
        List<OverlayField> content =
        [
            new("Position", "Position", ColumnWidth: 34), new("Number", "Car number", ColumnWidth: 40),
            new("Brand", "Car brand", ColumnWidth: 30), new("Flag", "Driver flag", ColumnWidth: 24),
            new("Name", "Driver name"), new("Pit", "Pit indicator", ColumnWidth: 32),
            new("License", "License", ColumnWidth: 58),
            new("Rating", standings ? "iRating and projected change" : "iRating", ColumnWidth: standings ? 92 : 62),
            new("Gap", "Gap", ColumnWidth: standings ? 80 : 62),
        ];
        if (standings)
        {
            content.Insert(0, new("Class", "Class badge and summary"));
            content.Insert(content.FindIndex(f => f.Id == "Position") + 1, new("Change", "Positions gained since start", ColumnWidth: 30));
            content.Insert(content.FindIndex(f => f.Id == "License"), new("Tyre", "Tyre compound", ColumnWidth: 28));
            content.AddRange([new("Interval", "Interval", ColumnWidth: 58),
                new("Best", "Best lap", ColumnWidth: 80), new("Last", "Last lap", ColumnWidth: 80),
                new("Average", "10-lap average", ColumnWidth: 80)]);
        }
        Content = content;
        Header = Band(standings
            ? ["Session", "Remaining", "Laps", "Cars", "Air", "Track", "Sof"]
            : ["Air", "Track", "Wind", "Bias", "Incidents", "Clock", "GameTime"]);
        Footer = Band(standings ? [] : ["Laps", "Sof", "Remaining"]).OrderBy(f => f.Id == "Remaining").ToArray();
    }

    private static OverlayField[] Band(string[] defaults)
    {
        (string Id, string Label)[] fields =
        [
            ("Session", "Session type"), ("Remaining", "Time remaining"), ("Laps", "Lap count"), ("Cars", "Car count"),
            ("Air", "Air temperature"), ("Track", "Track temperature"), ("Wind", "Wind speed and direction"),
            ("Sof", "Strength of field"), ("Bias", "Brake bias"), ("Incidents", "Incidents"),
            ("Clock", "Local time"), ("GameTime", "In-game time"),
        ];
        return fields.Select(f => new OverlayField(f.Id, f.Label, defaults.Contains(f.Id))).ToArray();
    }
}
