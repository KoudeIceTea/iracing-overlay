using IracingOverlay.Core.Sdk;

namespace IracingOverlay.Core.Race;

public sealed record DriverIconInfo(string CarPath, string CarModel, string FlairName);

public sealed record DriverEntry(
    int CarIdx,
    string UserName,
    string AbbrevName,
    string TeamName,
    string CarNumber,
    int ClassId,
    string ClassShortName,
    uint ClassColor,
    float ClassEstLapTime,
    int IRating,
    string License,
    uint LicenseColor,
    bool IsPaceCar,
    bool IsSpectator,
    string CarName,
    DriverIconInfo Icons,
    int UserId = 0);

public sealed record ResultPosition(int Position, int CarIdx, float FastestTime, float LastTime, int LapsComplete,
    int FastestLap = -1, float Time = -1);

public sealed record SessionDefinition(int Num, string Type, string Name, IReadOnlyList<ResultPosition> Results);

/// <summary>The parts of iRacing's session-info YAML that the overlays need.</summary>
public sealed class SessionData
{
    public string TrackName { get; private init; } = "";
    public string IncidentLimit { get; private init; } = "";
    public int DriverCarIdx { get; private init; } = -1;
    public bool IsTeamRacing { get; private init; }
    public int SubSessionId { get; private init; }
    public double FuelCapacity { get; private init; }
    public double FuelKgPerLitre { get; private init; }
    public string FuelDataCarPath { get; private init; } = "";
    public string FuelDataTrackKey { get; private init; } = "";

    /// <summary>
    /// Fuel in the garage setup (litres): what the car is loaded with when you get in. The key sits in a
    /// car-specific section of CarSetup ("Chassis → Rear → FuelLevel: 60.0 L"), so it's found by name.
    /// </summary>
    public double? SetupFuel { get; private init; }

    private static double? ParseSetupFuel(YamlNode setup, double kgPerLitre)
    {
        var node = setup.FindFirst("FuelLevel");
        if (node.Value is not { } text) return null;
        double value = node.Float(-1);
        if (value < 0) return null;
        if (text.Contains("gal", StringComparison.OrdinalIgnoreCase)) value *= 3.78541;
        else if (text.Contains("kg", StringComparison.OrdinalIgnoreCase) && kgPerLitre > 0) value /= kgPerLitre;
        return value;
    }

    /// <summary>"full" when driving/spectating live, "replay" when watching a replay.</summary>
    public string SimMode { get; private init; } = "";
    public bool IsReplay => SimMode.Equals("replay", StringComparison.OrdinalIgnoreCase);
    public IReadOnlyDictionary<int, DriverEntry> Drivers { get; private init; } = new Dictionary<int, DriverEntry>();
    public IReadOnlyList<SessionDefinition> Sessions { get; private init; } = [];
    public IReadOnlyList<ResultPosition> QualifyResults { get; private init; } = [];

    /// <summary>
    /// Compound names by tyre index ("Hard", "Wet", …) from DriverInfo.DriverTires. iRacing only lists
    /// the player's car; other cars use the same index scheme in practice (0 = dry, higher = wet).
    /// </summary>
    public IReadOnlyList<string> TyreCompounds { get; private init; } = [];

    public SessionDefinition? GetSession(int num) => Sessions.FirstOrDefault(s => s.Num == num);

    public static SessionData Parse(string yaml)
    {
        var root = YamlLite.Parse(yaml);
        var weekend = root["WeekendInfo"];
        var driverInfo = root["DriverInfo"];

        var drivers = new Dictionary<int, DriverEntry>();
        foreach (var d in driverInfo["Drivers"].Items)
        {
            int idx = d["CarIdx"].Int(-1);
            if (idx < 0) continue;
            drivers[idx] = new DriverEntry(
                CarIdx: idx,
                UserName: d["UserName"].Str(),
                AbbrevName: d["AbbrevName"].Str(),
                TeamName: d["TeamName"].Str(),
                CarNumber: d["CarNumber"].Str(),
                ClassId: d["CarClassID"].Int(),
                ClassShortName: d["CarClassShortName"].Str(d["CarScreenNameShort"].Str()),
                ClassColor: d["CarClassColor"].Hex(0xFFFFFF),
                ClassEstLapTime: d["CarClassEstLapTime"].Float(),
                IRating: d["IRating"].Int(),
                License: d["LicString"].Str(),
                LicenseColor: d["LicColor"].Hex(0x808080),
                IsPaceCar: d["CarIsPaceCar"].Int() == 1,
                IsSpectator: d["IsSpectator"].Int() == 1,
                CarName: d["CarScreenNameShort"].Str(),
                Icons: new DriverIconInfo(d["CarPath"].Str(), d["CarScreenName"].Str(d["CarScreenNameShort"].Str()),
                    d["FlairName"].Str()),
                UserId: d["UserID"].Int());
        }

        var sessions = root["SessionInfo"]["Sessions"].Items
            .Select(s => new SessionDefinition(
                s["SessionNum"].Int(-1),
                s["SessionType"].Str(),
                s["SessionName"].Str(),
                ParseResults(s["ResultsPositions"])))
            .ToList();

        return new SessionData
        {
            TrackName = weekend["TrackDisplayName"].Str(weekend["TrackName"].Str()),
            IncidentLimit = weekend["WeekendOptions"]["IncidentLimit"].Str(),
            IsTeamRacing = weekend["TeamRacing"].Int() == 1,
            SubSessionId = weekend["SubSessionID"].Int(),
            FuelCapacity = driverInfo["DriverCarFuelMaxLtr"].Float() * driverInfo["DriverCarMaxFuelPct"].Float(1),
            FuelKgPerLitre = driverInfo["DriverCarFuelKgPerLtr"].Float(),
            SetupFuel = ParseSetupFuel(root["CarSetup"], driverInfo["DriverCarFuelKgPerLtr"].Float()),
            FuelDataCarPath = drivers.TryGetValue(driverInfo["DriverCarIdx"].Int(-1), out var player)
                ? player.Icons.CarPath : "",
            FuelDataTrackKey = ToFuelDataTrackKey(weekend["TrackName"].Str()),
            SimMode = weekend["SimMode"].Str(),
            DriverCarIdx = driverInfo["DriverCarIdx"].Int(-1),
            Drivers = drivers,
            Sessions = sessions,
            QualifyResults = ParseResults(root["QualifyResultsInfo"]["Results"]),
            TyreCompounds = driverInfo["DriverTires"].Items
                .OrderBy(t => t["TireIndex"].Int())
                .Select(t => t["TireCompoundType"].Str())
                .ToList(),
        };
    }

    private static string ToFuelDataTrackKey(string trackName) =>
        string.Join('\\', trackName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static List<ResultPosition> ParseResults(YamlNode node) =>
        node.Items
            .Select(r => new ResultPosition(
                r["Position"].Int(),
                r["CarIdx"].Int(-1),
                r["FastestTime"].Float(-1),
                r["LastTime"].Float(-1),
                r["LapsComplete"].Int(),
                r["FastestLap"].Int(-1),
                r["Time"].Float(-1)))
            .Where(r => r.CarIdx >= 0)
            .ToList();
}
