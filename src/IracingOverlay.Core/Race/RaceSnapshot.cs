namespace IracingOverlay.Core.Race;

/// <summary>How a car relates to the focus car in lap count (race sessions only).</summary>
public enum LapRelation
{
    SameLap,
    /// <summary>The other car is one or more laps ahead of us (it is lapping us).</summary>
    LapsAhead,
    /// <summary>The other car is one or more laps behind us (we are lapping it).</summary>
    LapsBehind,
}

[Flags]
public enum DriverFlags
{
    None = 0,
    /// <summary>Black flag: a penalty to serve in the pits.</summary>
    Black = 0x10000,
    Disqualified = 0x20000,
    /// <summary>Furled black flag: iRacing's slowdown after cutting the track; clears once enough time is given back.</summary>
    Furled = 0x80000,
    /// <summary>Meatball: mandatory repairs.</summary>
    Repair = 0x100000,
}

public enum LapTimeKind
{
    Normal,
    /// <summary>The lap equals the driver's own best lap.</summary>
    PersonalBest,
    /// <summary>The lap is the fastest lap in the class.</summary>
    ClassBest,
}

/// <summary>Tyre compound family, used to pick the tyre icon.</summary>
public enum TyreCompound
{
    Unknown,
    /// <summary>Hard, or the single dry compound of cars that only have "Dry".</summary>
    Hard,
    Medium,
    Soft,
    Intermediate,
    Wet,
}

/// <summary>
/// Wind as seen from the focus car: <see cref="FromDegrees"/> is the direction the wind blows
/// <em>from</em>, clockwise, where 0° is the nose of the car (or north when not driving).
/// </summary>
public sealed record WindInfo(double FromDegrees, double SpeedKph, bool RelativeToCar);

public sealed record SessionHeader(
    string TrackName,
    string SessionType,
    bool IsRace,
    string TimeRemaining,
    /// <summary>"12/20" for lap races, "12/~17.98" (estimate) for timed races.</summary>
    string Laps,
    float AirTempC,
    float TrackTempC,
    int TotalCars,
    bool IsMulticlass,
    /// <summary>Class name and SoF of the focus car's class.</summary>
    string FocusClassName,
    int FocusClassSof,
    int? Incidents,
    string IncidentLimit,
    /// <summary>Brake bias in %, or null when the car doesn't expose it (or we're not driving).</summary>
    float? BrakeBias,
    /// <summary>In-game time of day, "HH:mm".</summary>
    string GameTime,
    WindInfo? Wind);

public sealed record StandingRow(
    int Position,
    int CarIdx,
    string CarNumber,
    string Name,
    /// <summary>Team name in team races (the driver above is whoever is in the car now); empty otherwise.</summary>
    string TeamName,
    DriverIconInfo Icons,
    string License,
    uint LicenseColor,
    int IRating,
    /// <summary>Projected iRating change at the current position (race sessions only).</summary>
    int? IRatingChange,
    string Gap,
    string Interval,
    string LastLap,
    LapTimeKind LastLapKind,
    string BestLap,
    bool IsClassBest,
    string AverageLap,
    bool IsFocus,
    bool InPit,
    bool InWorld,
    uint ClassColor,
    TyreCompound Tyre,
    DriverFlags Flags,
    /// <summary>Races only: class places gained (+) or lost (-) since the start (grid = qualifying result).</summary>
    int? PositionsGained = null,
    /// <summary>Seconds the most important active flag has been up (DQ > black > meatball > slowdown).</summary>
    double FlagSeconds = 0,
    /// <summary>Your own car only: mandatory repair time left while the meatball is out.</summary>
    double? RepairLeft = null);

/// <summary>One class block in the standings (all classes are shown, fastest class first).</summary>
public sealed record ClassStandings(
    int ClassId,
    string ClassName,
    uint ClassColor,
    int CarCount,
    int StrengthOfField,
    bool IsFocusClass,
    IReadOnlyList<StandingRow> Rows);

public sealed record RelativeRow(
    int Position,
    int CarIdx,
    string CarNumber,
    string Name,
    /// <summary>Team name in team races (the driver above is whoever is in the car now); empty otherwise.</summary>
    string TeamName,
    DriverIconInfo Icons,
    string License,
    uint LicenseColor,
    int IRating,
    float GapSeconds,
    string Gap,
    bool IsFocus,
    bool InPit,
    LapRelation Relation,
    uint ClassColor);

public sealed record RaceSnapshot(
    SessionHeader Header,
    IReadOnlyList<ClassStandings> Classes,
    IReadOnlyList<RelativeRow> Relative,
    SessionHeader RelativeHeader,
    int SessionId, int SessionNumber, int StandingsFocus, double SampleTime)
{
    public FuelSnapshot Fuel { get; init; } = new();
    public DeltaSnapshot Delta { get; init; } = new();
    public RadarSnapshot Radar { get; init; } = new();
}

public sealed record RaceOptions(
    /// <summary>Max rows for the focus car's class (leaders + a window around the focus car).</summary>
    int FocusClassRows = 10,
    /// <summary>Max rows (the class leaders) for every other class.</summary>
    int OtherClassRows = 3,
    /// <summary>Leaders always shown at the top of the focus class when its window is truncated.</summary>
    int TopRows = 3,
    int RelativeAhead = 4,
    int RelativeBehind = 4,
    bool StandingsFollowCamera = false,
    bool RelativeFollowCamera = false);
