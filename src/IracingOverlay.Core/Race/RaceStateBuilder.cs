using System.Globalization;
using IracingOverlay.Core.Sdk;

namespace IracingOverlay.Core.Race;

/// <summary>Turns a raw telemetry frame + session info into display-ready standings and relative rows.</summary>
public static class RaceStateBuilder
{
    private const int SessionStateRacing = 4;
    private const int TrackSurfaceNotInWorld = -1;
    private const int UnlimitedLaps = 32767;
    private const float MinPlausibleLap = 15f;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed class Car
    {
        public required DriverEntry Driver { get; init; }
        public int Idx => Driver.CarIdx;
        public int Lap, LapCompleted, TelemetryClassPos, OverallPos, Surface, TyreIndex;
        public float Pct, EstTime, F2Time, LastLap, BestLap, AverageLap;
        public string TeamName = ""; // team races only
        public bool OnPitRoad;
        public DriverFlags Flags;
        public double FlagSeconds;
        public double? RepairLeft;
        public int ClassPosition; // computed (contiguous, used for ordering)
        public int StartPosition; // class position on the official grid (0 = unknown)
        public bool Ranked = true; // false = no time yet in practice/qualifying: shown without a position
        public bool InWorld => Surface != TrackSurfaceNotInWorld && Pct >= 0;
        /// <summary>Replay: standings data comes from the live session results, not the replay moment.</summary>
        public bool ResultsOnly;
        public float ResultTime = -1; // session results "Time" (race time at the last line crossing)
        public double Progress => ResultsOnly ? LapCompleted : LapCompleted + Math.Max(Pct, 0);
    }

    public static RaceSnapshot Build(TelemetryFrame t, SessionData s, RaceOptions o, LapHistory? history = null,
        FuelHistory? fuelHistory = null, FlagTracker? flags = null, SectorTimer? sectors = null, RadarTracker? radar = null)
    {
        int sessionNumber = t.GetInt("SessionNum");
        int sessionId = t.GetInt("SessionUniqueID");
        double time = s.IsReplay && t.Has("ReplaySessionTime") ? t.GetDouble("ReplaySessionTime") : t.GetDouble("SessionTime");
        history?.BeginFrame(sessionId, sessionNumber, time);
        flags?.BeginFrame(sessionId, sessionNumber);
        var session = s.GetSession(sessionNumber);
        string sessionType = session?.Type ?? "";
        bool isRace = sessionType.Equals("Race", StringComparison.OrdinalIgnoreCase);
        bool raceStarted = isRace && t.GetInt("SessionState") >= SessionStateRacing;

        var cars = ReadCars(t, s, session, history, flags, time);
        AssignClassPositions(cars, session, s, isRace);

        int player = t.GetInt("PlayerCarIdx", 0, s.DriverCarIdx);
        int cam = t.GetInt("CamCarIdx", 0, -1);
        bool hasCamera = cars.ContainsKey(cam) && (t.GetInt("CamCameraState") & 2) == 0;
        int normalFocus = (!s.IsReplay && cars.ContainsKey(player)) || !hasCamera ? player : cam;
        bool spectating = !t.GetBool("IsOnTrack") || s.IsReplay;
        int Focus(bool follow) => follow && spectating && hasCamera ? cam : normalFocus;
        int focusIdx = Focus(o.StandingsFollowCamera);
        cars.TryGetValue(focusIdx, out var focus);
        cars.TryGetValue(Focus(o.RelativeFollowCamera), out var relativeFocus);
        int focusClass = focus?.Driver.ClassId ?? cars.Values.FirstOrDefault()?.Driver.ClassId ?? 0;

        // Every class, ordered by position within it; fastest class (best overall position) first.
        var classes = cars.Values
            .GroupBy(c => c.Driver.ClassId)
            .Select(g => g.OrderBy(c => c.ClassPosition).ToList())
            .OrderBy(ClassSortKey)
            .ToList();
        var focusClassCars = classes.FirstOrDefault(c => c[0].Driver.ClassId == focusClass) ?? [];

        var header = BuildHeader(t, s, sessionType, isRace, focus, focusClassCars, cars.Count, classes.Count > 1);
        var standings = classes
            .Select(c => BuildClass(c, focusIdx, c[0].Driver.ClassId == focusClass, isRace, raceStarted, s.TyreCompounds, o))
            .ToList();
        // Always includes ourselves, also from the garage (then timed from the start/finish line).
        var relative = relativeFocus == null ? [] : BuildRelative(cars.Values, relativeFocus, raceStarted && relativeFocus.InWorld, o);
        var relativeHeader = relativeFocus == focus ? header : BuildHeader(t, s, sessionType, isRace, relativeFocus,
            classes.FirstOrDefault(c => c[0].Driver.ClassId == relativeFocus?.Driver.ClassId) ?? [], cars.Count, classes.Count > 1);

        return new RaceSnapshot(header, standings, relative, relativeHeader, sessionId, sessionNumber, focusIdx, time)
        {
            Fuel = (fuelHistory?.Update(t, s, player, sessionId, sessionNumber, time) ?? new()) with
            {
                LapsToGo = isRace && cars.TryGetValue(player, out var own)
                    && classes.FirstOrDefault(c => c[0].Driver.ClassId == own.Driver.ClassId) is { } ownClass
                    ? LapsToGo(t, ownClass[0]) : null,
            },
            Delta = BuildDelta(t, sectors, player, sessionId, sessionNumber, s, cars),
            Radar = radar?.Build(t, s, player, sessionId) ?? new(),
        };
    }

    /// <summary>Your own lap timing. iRacing's deltas exist only for the car you drive, so this never follows the camera.</summary>
    private static DeltaSnapshot BuildDelta(TelemetryFrame t, SectorTimer? sectors, int player, int sessionId, int sessionNumber,
        SessionData s, Dictionary<int, Car> cars)
    {
        sectors?.Update(t, s, sessionId, sessionNumber);
        bool driving = t.GetBool("IsOnTrack") && !s.IsReplay;
        double? Delta(string name) => driving && t.GetBool(name + "_OK") ? t.GetDouble(name) : null;
        static double? Time(double seconds) => seconds > 0 && double.IsFinite(seconds) ? seconds : null;
        int playerClass = cars.TryGetValue(player, out var own) ? own.Driver.ClassId : -1;
        var fastest = cars.Values.Where(c => c.Driver.ClassId == playerClass && c.BestLap > MinPlausibleLap)
            .OrderBy(c => c.BestLap).FirstOrDefault();
        double? bestLap = Time(t.GetDouble("LapBestLapTime")) ?? (own != null ? Time(own.BestLap) : null);
        double? classBest = fastest?.BestLap;
        // Optimal laps are added up from the sectors this app measured, which may miss the fastest ones (laps driven
        // before it started, or out of iRacing's view). An optimal lap is never slower than the fastest real lap,
        // and the class optimal never slower than your own optimal, so those cap the estimates.
        double? optimal = Fastest(sectors?.TheoreticalBest(player), bestLap);
        double? classOptimal = Fastest(sectors?.ClassOptimal(playerClass), classBest, optimal);
        return new DeltaSnapshot
        {
            BestLapDelta = Delta("LapDeltaToBestLap"),
            OptimalLapDelta = Delta("LapDeltaToOptimalLap"),
            SessionBestLapDelta = Delta("LapDeltaToSessionBestLap"),
            SessionOptimalLapDelta = Delta("LapDeltaToSessionOptimalLap"),
            LastLapDelta = Delta("LapDeltaToSessionLastlLap"), // sic: iRacing's variable name
            CurrentLap = driving ? Time(t.GetDouble("LapCurrentLapTime")) : null,
            LastLap = Time(t.GetDouble("LapLastLapTime")) ?? (own != null ? Time(own.LastLap) : null),
            BestLap = bestLap,
            SessionBestLap = classBest,
            SessionBestDriver = fastest == null ? "" : fastest.Idx == player ? "you" : fastest.Driver.UserName,
            SessionOptimal = classOptimal,
            TheoreticalBest = optimal,
            Sectors = sectors?.Splits(player) ?? [],
        };
    }

    private static double? Fastest(params double?[] times) =>
        times.Where(time => time > 0).Min();

    private static double ClassSortKey(List<Car> classCars)
    {
        int bestOverall = classCars.Where(c => c.OverallPos > 0).Select(c => c.OverallPos).DefaultIfEmpty(0).Min();
        if (bestOverall > 0) return bestOverall;
        float est = classCars[0].Driver.ClassEstLapTime;
        return est > MinPlausibleLap ? 1000 + est : 100000 + classCars[0].Driver.ClassId;
    }

    private static Dictionary<int, Car> ReadCars(TelemetryFrame t, SessionData s, SessionDefinition? session, LapHistory? history,
        FlagTracker? flags, double time)
    {
        var results = session?.Results.ToDictionary(r => r.CarIdx) ?? [];
        // Standings always show live data. iRacing reports car data from the replay whenever a replay is showing:
        // a replay file, scrubbing back, and also simply being out of the car in a live session (that view is the
        // replay ~5 s behind live). Only while you are driving is the car data truly live; otherwise positions,
        // lap times and laps come from the live session results, which iRacing keeps current in the background.
        // (The relative keeps using the car data: it is about track position around you.)
        bool watchingReplay = s.IsReplay || t.GetBool("IsReplayPlaying");
        bool fromResults = watchingReplay && results.Count > 0;
        var cars = new Dictionary<int, Car>();
        const DriverFlags driverFlags = DriverFlags.Black | DriverFlags.Disqualified | DriverFlags.Furled | DriverFlags.Repair;
        int player = t.GetInt("PlayerCarIdx", 0, s.DriverCarIdx);
        foreach (var d in s.Drivers.Values)
        {
            if (d.IsPaceCar || d.IsSpectator) continue;
            int i = d.CarIdx;
            var car = new Car
            {
                Driver = d,
                TeamName = s.IsTeamRacing ? d.TeamName.Trim() : "",
                Lap = t.GetInt("CarIdxLap", i),
                LapCompleted = t.GetInt("CarIdxLapCompleted", i),
                TelemetryClassPos = t.GetInt("CarIdxClassPosition", i),
                OverallPos = t.GetInt("CarIdxPosition", i),
                Surface = t.GetInt("CarIdxTrackSurface", i, TrackSurfaceNotInWorld),
                Pct = t.GetFloat("CarIdxLapDistPct", i, -1),
                EstTime = t.GetFloat("CarIdxEstTime", i),
                F2Time = t.GetFloat("CarIdxF2Time", i),
                LastLap = t.GetFloat("CarIdxLastLapTime", i),
                BestLap = t.GetFloat("CarIdxBestLapTime", i),
                OnPitRoad = t.GetBool("CarIdxOnPitRoad", i),
                TyreIndex = t.GetInt("CarIdxTireCompound", i, -1),
                Flags = (DriverFlags)t.GetInt("CarIdxSessionFlags", i,
                    i == player && !s.IsReplay ? t.GetInt("SessionFlags") : 0) & driverFlags,
            };

            results.TryGetValue(i, out var res);
            if (fromResults)
            {
                car.ResultsOnly = true;
                car.TelemetryClassPos = 0;                 // rank by the results instead
                car.OverallPos = res?.Position ?? 0;
                car.LapCompleted = res?.LapsComplete ?? 0;
                car.LastLap = res?.LastTime ?? -1;
                car.BestLap = res?.FastestTime ?? -1;
                car.ResultTime = res?.Time ?? -1;
            }
            car.AverageLap = history?.Observe(d, car.LapCompleted, car.LastLap,
                fromResults ? res?.FastestLap ?? -1 : t.GetInt("CarIdxBestLapNum", i, -1), car.BestLap, res) ?? 0;
            if (res is { FastestTime: > 0 } && (!(car.BestLap > 0) || res.FastestTime < car.BestLap))
                car.BestLap = res.FastestTime;

            // Disconnected/towed cars lose their live lap data; fall back to the official results.
            if (!fromResults && !car.InWorld && res != null)
            {
                car.LapCompleted = Math.Max(car.LapCompleted, res.LapsComplete);
                if (car.LastLap <= 0) car.LastLap = res.LastTime;
            }
            car.FlagSeconds = flags?.Observe(i, car.Flags, time) ?? 0;
            // iRacing only gives the repair time left for your own car.
            if (i == player && (car.Flags & DriverFlags.Repair) != 0 && t.GetFloat("PitRepairLeft") is > 0 and var repair)
                car.RepairLeft = repair;
            cars[i] = car;
        }
        return cars;
    }

    /// <summary>
    /// Class position priority: live telemetry → session results → qualifying (grid) → best lap → race progress.
    /// Positions are then renumbered 1..n per class so the display is always contiguous.
    /// In practice/qualifying, cars without any time get no position at all (shown as "–" at the bottom, by
    /// car number), like iRacing's own timing. Ranking them by e.g. iRating put random drivers near the top
    /// in replays, where iRacing only knows positions as of the replay moment.
    /// </summary>
    private static void AssignClassPositions(Dictionary<int, Car> cars, SessionDefinition? session, SessionData s, bool isRace)
    {
        var resultRank = RankByPosition(session?.Results, cars);
        var qualifyRank = RankByPosition(s.QualifyResults, cars);

        foreach (var group in cars.Values.GroupBy(c => c.Driver.ClassId))
        {
            int Official(Car c) =>
                c.TelemetryClassPos > 0 ? c.TelemetryClassPos
                : resultRank.TryGetValue(c.Idx, out var r) ? r
                : isRace && qualifyRank.TryGetValue(c.Idx, out var q) ? q
                : 0;

            var ordered = group
                .Select(c => (Car: c, Pos: Official(c), Timed: c.BestLap > 0))
                .Select(x => { x.Car.Ranked = x.Pos > 0 || x.Timed || isRace; return x; })
                .OrderBy(x => x.Pos > 0 ? 0 : x.Timed ? 1 : 2)
                .ThenBy(x => x.Pos)
                .ThenBy(x => x.Timed && x.Pos <= 0 ? x.Car.BestLap : 0)
                .ThenByDescending(x => isRace ? x.Car.Progress : 0)
                .ThenBy(x => int.TryParse(x.Car.Driver.CarNumber, out int n) ? n : int.MaxValue)
                .ThenBy(x => x.Car.Driver.CarNumber, StringComparer.Ordinal)
                .Select(x => x.Car);

            foreach (var c in group) c.StartPosition = qualifyRank.TryGetValue(c.Idx, out int start) ? start : 0;
            int pos = 1;
            foreach (var c in ordered) c.ClassPosition = pos++;
        }
    }

    /// <summary>Ranks results within each class by overall Position (YAML ClassPosition is unreliable/0-based).</summary>
    private static Dictionary<int, int> RankByPosition(IReadOnlyList<ResultPosition>? results, Dictionary<int, Car> cars)
    {
        var rank = new Dictionary<int, int>();
        if (results == null) return rank;
        foreach (var group in results.Where(r => cars.ContainsKey(r.CarIdx)).GroupBy(r => cars[r.CarIdx].Driver.ClassId))
        {
            int n = 1;
            foreach (var r in group.OrderBy(r => r.Position)) rank[r.CarIdx] = n++;
        }
        return rank;
    }

    private static SessionHeader BuildHeader(TelemetryFrame t, SessionData s, string sessionType, bool isRace,
        Car? focus, List<Car> focusClassCars, int totalCars, bool isMulticlass)
    {
        double remain = t.GetDouble("SessionTimeRemain", 0, -1);
        bool timed = remain is >= 0 and <= 86400 * 2;
        string time = timed ? FormatClock(remain) : "--:--";

        int done = Math.Max(focus?.LapCompleted ?? 0, 0);
        int lapsTotal = t.GetInt("SessionLapsTotal", 0, UnlimitedLaps);
        var leader = focusClassCars.FirstOrDefault();
        string laps;
        if (lapsTotal is > 0 and < UnlimitedLaps)
            laps = $"{done}/{lapsTotal}";
        else if (leader != null && LapsToGo(t, leader) is double toGo)
            laps = $"{done}/~{(leader.Progress + toGo).ToString("0.00", Inv)}";
        else
            laps = done.ToString(Inv);

        float bias = t.GetFloat("dcBrakeBias");
        bool ownCar = !s.IsReplay && focus != null && focus.Idx == t.GetInt("PlayerCarIdx", 0, s.DriverCarIdx);
        bool driving = ownCar && t.GetBool("IsOnTrack");

        return new SessionHeader(
            TrackName: s.TrackName,
            SessionType: sessionType,
            IsRace: isRace,
            TimeRemaining: time,
            Laps: laps,
            AirTempC: t.GetFloat("AirTemp"),
            TrackTempC: t.Has("TrackTempCrew") ? t.GetFloat("TrackTempCrew") : t.GetFloat("TrackTemp"),
            TotalCars: totalCars,
            IsMulticlass: isMulticlass,
            FocusClassName: focus?.Driver.ClassShortName ?? "",
            FocusClassSof: StrengthOfField(focusClassCars.Select(c => c.Driver.IRating)),
            Incidents: ownCar ? t.GetInt("PlayerCarMyIncidentCount") : null,
            IncidentLimit: s.IncidentLimit,
            BrakeBias: driving && bias > 0 ? bias : null,
            GameTime: FormatTimeOfDay(t.GetDouble("SessionTimeOfDay", 0, -1)),
            Wind: ReadWind(t, driving));
    }

    private static string FormatTimeOfDay(double seconds)
    {
        if (seconds < 0) return "--:--";
        var ts = TimeSpan.FromSeconds(seconds % 86400);
        return $"{ts.Hours:00}:{ts.Minutes:00}";
    }

    /// <summary>
    /// WindDir is the compass direction the wind blows from (clockwise from north) and YawNorth is our
    /// heading on the same compass, so their difference is where the wind comes from relative to the
    /// nose. YawNorth is only meaningful with us in the car; otherwise report it relative to north.
    /// </summary>
    private static WindInfo? ReadWind(TelemetryFrame t, bool driving)
    {
        if (!t.Has("WindDir")) return null;
        double fromRad = t.GetDouble("WindDir") - (driving ? t.GetDouble("YawNorth") : 0);
        double degrees = (fromRad * 180 / Math.PI % 360 + 360) % 360;
        return new WindInfo(degrees, t.GetDouble("WindVel") * 3.6, driving);
    }

    /// <summary>Maps iRacing compound names ("Hard", "Medium", "Soft", "Dry", "Wet", "Primary", …) to an icon family.</summary>
    private static TyreCompound ClassifyTyre(int index, IReadOnlyList<string> names)
    {
        if (index < 0) return TyreCompound.Unknown;
        string name = index < names.Count ? names[index].ToLowerInvariant() : "";
        if (name.Contains("wet") || name.Contains("rain")) return TyreCompound.Wet;
        if (name.Contains("inter")) return TyreCompound.Intermediate;
        if (name.Contains("soft") || name.Contains("alternate")) return TyreCompound.Soft;
        if (name.Contains("medium")) return TyreCompound.Medium;
        if (name.Length > 0) return TyreCompound.Hard; // "Hard", "Dry", "Primary"
        // Unknown list (other car type): iRacing puts the dry compound first.
        return index == 0 ? TyreCompound.Hard : TyreCompound.Unknown;
    }

    /// <summary>
    /// Laps the class leader still has to drive until the chequered flag (null when unknown, e.g. open practice).
    /// Lap races: total minus progress. Timed: iRacing's own SessionLapsRemainEx when it has one (it counts the
    /// lap in progress), otherwise time left divided by the leader's lap time.
    /// </summary>
    private static double? LapsToGo(TelemetryFrame t, Car leader)
    {
        int lapsTotal = t.GetInt("SessionLapsTotal", 0, UnlimitedLaps);
        if (lapsTotal is > 0 and < UnlimitedLaps) return Math.Max(0, lapsTotal - leader.Progress);
        int remainEx = t.GetInt("SessionLapsRemainEx", 0, UnlimitedLaps);
        if (remainEx is >= 0 and < UnlimitedLaps) return Math.Max(0, remainEx - Math.Max(leader.Pct, 0));
        double remain = t.GetDouble("SessionTimeRemain", 0, -1);
        if (remain is >= 0 and <= 86400 * 2 && LapReference(leader) is { } leaderLap) return remain / leaderLap;
        return null;
    }

    private static float? LapReference(Car c) =>
        c.LastLap > MinPlausibleLap ? c.LastLap
        : c.BestLap > MinPlausibleLap ? c.BestLap
        : c.Driver.ClassEstLapTime > MinPlausibleLap ? c.Driver.ClassEstLapTime
        : null;

    private static ClassStandings BuildClass(List<Car> classCars, int focusIdx, bool isFocusClass, bool isRace, bool raceStarted,
        IReadOnlyList<string> tyreNames, RaceOptions o)
    {
        float fastest = classCars.Where(c => c.BestLap > 0).Select(c => c.BestLap).DefaultIfEmpty(0).Min();
        var visible = isFocusClass
            ? SelectVisible(classCars, focusIdx, o.FocusClassRows, o.TopRows)
            : classCars.Take(o.OtherClassRows).ToList();
        var iRatingChanges = isRace ? EstimateIRatingChanges(classCars) : null;
        var leader = classCars[0];
        var rows = new List<StandingRow>(visible.Count);

        foreach (var c in visible)
        {
            var ahead = c.ClassPosition > 1 ? classCars[c.ClassPosition - 2] : null;
            string gap, interval;
            if (isRace)
            {
                gap = c == leader ? "Leader" : raceStarted ? RaceGap(c, leader) : "--";
                interval = ahead == null ? "" : raceStarted ? RaceGap(c, ahead) : "--";
            }
            else
            {
                gap = c == leader ? FormatLap(c.BestLap) : LapDelta(c.BestLap, leader.BestLap);
                interval = ahead == null ? "" : LapDelta(c.BestLap, ahead.BestLap);
            }

            var lastKind = c.LastLap <= 0 ? LapTimeKind.Normal
                : fastest > 0 && c.LastLap <= fastest ? LapTimeKind.ClassBest
                : c.LastLap <= c.BestLap ? LapTimeKind.PersonalBest
                : LapTimeKind.Normal;

            var d = c.Driver;
            rows.Add(new StandingRow(
                Position: c.Ranked ? c.ClassPosition : 0,
                CarIdx: c.Idx,
                CarNumber: d.CarNumber,
                Name: d.UserName,
                TeamName: c.TeamName,
                Icons: d.Icons,
                License: ShortLicense(d.License),
                LicenseColor: d.LicenseColor,
                IRating: d.IRating,
                IRatingChange: iRatingChanges != null && iRatingChanges.TryGetValue(c.Idx, out var delta) ? delta : null,
                Gap: gap,
                Interval: interval,
                LastLap: FormatLap(c.LastLap),
                LastLapKind: lastKind,
                BestLap: FormatLap(c.BestLap),
                IsClassBest: fastest > 0 && c.BestLap == fastest,
                AverageLap: FormatLap(c.AverageLap),
                IsFocus: c.Idx == focusIdx,
                InPit: c.OnPitRoad,
                InWorld: c.InWorld,
                ClassColor: d.ClassColor,
                // Cars that aren't in the world report a stale/unknown compound.
                Tyre: c.InWorld ? ClassifyTyre(c.TyreIndex, tyreNames) : TyreCompound.Unknown,
                Flags: c.Flags,
                // Races only, once started: class places gained (+) or lost (-) since the grid.
                PositionsGained: raceStarted && c.Ranked && c.StartPosition > 0 ? c.StartPosition - c.ClassPosition : null,
                FlagSeconds: c.FlagSeconds,
                RepairLeft: c.RepairLeft));
        }

        var first = classCars[0].Driver;
        return new ClassStandings(
            ClassId: first.ClassId,
            ClassName: string.IsNullOrEmpty(first.ClassShortName) ? first.CarName : first.ClassShortName,
            ClassColor: first.ClassColor,
            CarCount: classCars.Count,
            StrengthOfField: StrengthOfField(classCars.Select(c => c.Driver.IRating)),
            IsFocusClass: isFocusClass,
            Rows: rows);
    }

    /// <summary>Leaders on top, then a window around the focus car when the class is too large.</summary>
    private static List<Car> SelectVisible(List<Car> ordered, int focusIdx, int maxRows, int topRows)
    {
        if (ordered.Count <= maxRows) return ordered;

        int focusPos = ordered.FindIndex(c => c.Idx == focusIdx);
        if (focusPos < 0 || focusPos < maxRows - 1) return ordered.Take(maxRows).ToList();

        int window = maxRows - topRows;
        int start = Math.Clamp(focusPos - window / 2, topRows, ordered.Count - window);
        return ordered.Take(topRows).Concat(ordered.Skip(start).Take(window)).ToList();
    }

    private static string RaceGap(Car car, Car reference)
    {
        static bool Unknown(Car c) => !c.InWorld && c.LapCompleted <= 0;
        if (Unknown(car) || Unknown(reference)) return "--";

        int lapsDown = (int)Math.Floor(reference.Progress - car.Progress);
        if (lapsDown >= 1) return $"+{lapsDown}L";
        // From the session results: the gap at the line, from the race time each car had when it last crossed it.
        if (car.ResultsOnly || reference.ResultsOnly)
        {
            float atLine = car.ResultTime - reference.ResultTime;
            return car.ResultTime > 0 && reference.ResultTime > 0 && atLine > 0 ? "+" + atLine.ToString("0.0", Inv) : "--";
        }
        float delta = car.F2Time - reference.F2Time;
        return delta > 0 ? "+" + delta.ToString("0.0", Inv) : "--";
    }

    private static string LapDelta(float time, float reference) =>
        time > 0 && reference > 0 ? (time - reference).ToString("+0.000;-0.000;0.000", Inv) : "--";

    /// <summary>
    /// Projected iRating change if the class finished in its current order. This is the
    /// community-reverse-engineered iRacing formula (Elo-like with a small position fudge);
    /// it ignores non-starters, so treat it as an estimate.
    /// </summary>
    private static Dictionary<int, int> EstimateIRatingChanges(List<Car> classOrdered)
    {
        var result = new Dictionary<int, int>();
        int n = classOrdered.Count;
        if (n < 2) return result;

        const double br = 1600 / 0.69314718055994530942; // 1600 / ln 2
        var ratings = classOrdered.Select(c => (double)Math.Max(c.Driver.IRating, 1)).ToArray();
        var decay = ratings.Select(r => Math.Exp(-r / br)).ToArray();

        // Probability that driver a finishes ahead of driver b.
        double Beats(int a, int b) =>
            (1 - decay[a]) * decay[b] / ((1 - decay[b]) * decay[a] + (1 - decay[a]) * decay[b]);

        for (int i = 0; i < n; i++)
        {
            double expected = 0;
            for (int j = 0; j < n; j++)
                if (j != i) expected += Beats(i, j);

            int position = i + 1;
            double actual = n - position;                 // drivers beaten
            double fudge = (n / 2.0 - position) / 100.0;
            double change = (actual - expected - fudge) * 200.0 / n;
            result[classOrdered[i].Idx] = (int)Math.Round(change);
        }
        return result;
    }

    private static List<RelativeRow> BuildRelative(IEnumerable<Car> all, Car focus, bool showLapRelation, RaceOptions o)
    {
        // Reference lap time for the focus car, used to convert track % to seconds and to wrap
        // gaps around the S/F line. iRacing reports CarClassEstLapTime = 1.0 in replays, so sanity-check it.
        float lapTime = focus.Driver.ClassEstLapTime > MinPlausibleLap ? focus.Driver.ClassEstLapTime
            : focus.BestLap > MinPlausibleLap ? focus.BestLap
            : focus.LastLap > MinPlausibleLap ? focus.LastLap
            : all.Where(c => c.Driver.ClassId == focus.Driver.ClassId && c.BestLap > MinPlausibleLap)
                 .Select(c => c.BestLap).DefaultIfEmpty(120f).Min();

        // Out of the car (garage) we have no track position; time everyone from the start/finish
        // line instead, so cars on track still show up with sensible gaps and we sit at 0.0.
        bool onTrack = focus.InWorld;
        float refPct = onTrack ? focus.Pct : 0f;
        float refEst = onTrack ? focus.EstTime : 0f;

        // CarIdxEstTime is the best source (accounts for slow/fast track sections) but iRacing leaves it
        // at 0 in some modes (e.g. replays/spectating). Fall back to track-position % x lap time.
        bool useEstTime = onTrack ? focus.EstTime > 0 : all.Any(c => c.InWorld && c.EstTime > 0);

        var entries = new List<(Car Car, float Gap)>();
        foreach (var c in all)
        {
            // Only cars actually on track / in the car; we ourselves are always added below.
            if (c == focus || !c.InWorld) continue;
            // Each class has its own estimate at the same piece of track. Compare percentages
            // across classes so a nearby faster car stays on the correct side of the focus car.
            float gap = useEstTime && c.EstTime > 0 && c.Driver.ClassId == focus.Driver.ClassId ? Wrap(c.EstTime - refEst, lapTime)
                : Wrap((c.Pct - refPct) * lapTime, lapTime);
            entries.Add((c, gap));
        }

        // Positive gap = car is ahead of us on track.
        var ahead = entries.Where(e => e.Gap >= 0).OrderBy(e => e.Gap).Take(o.RelativeAhead).Reverse();
        var behind = entries.Where(e => e.Gap < 0).OrderByDescending(e => e.Gap).Take(o.RelativeBehind);

        return ahead
            .Append((Car: focus, Gap: 0f))
            .Concat(behind)
            .Select(e => ToRelativeRow(
                e.Car,
                e.Gap,
                // Cars ahead shown negative (we're behind them), cars behind positive.
                e.Car == focus ? "0.0" : (e.Gap >= 0 ? "-" : "+") + Math.Abs(e.Gap).ToString("0.0", Inv),
                showLapRelation ? LapRelationTo(e.Car, focus) : LapRelation.SameLap,
                isFocus: e.Car == focus))
            .ToList();
    }

    private static RelativeRow ToRelativeRow(Car car, float gapSeconds, string gap, LapRelation relation, bool isFocus)
    {
        var d = car.Driver;
        return new RelativeRow(
            Position: car.Ranked ? car.ClassPosition : 0,
            CarIdx: car.Idx,
            CarNumber: d.CarNumber,
            Name: d.UserName,
            TeamName: car.TeamName,
            Icons: d.Icons,
            License: ShortLicense(d.License),
            LicenseColor: d.LicenseColor,
            IRating: d.IRating,
            GapSeconds: gapSeconds,
            Gap: gap,
            IsFocus: isFocus,
            // Sitting in the garage (not in the world) counts as being in the pits.
            InPit: car.OnPitRoad || !car.InWorld,
            Relation: relation,
            ClassColor: d.ClassColor,
            Flags: car.Flags,
            FlagSeconds: car.FlagSeconds,
            RepairLeft: car.RepairLeft);
    }

    private static float Wrap(float delta, float lapTime)
    {
        float half = lapTime / 2;
        while (delta > half) delta -= lapTime;
        while (delta < -half) delta += lapTime;
        return delta;
    }

    private static LapRelation LapRelationTo(Car car, Car focus)
    {
        if (car.Lap < 1 || focus.Lap < 1) return LapRelation.SameLap;
        // Total race distance difference, minus the on-track distance difference = whole laps apart.
        double onTrack = car.Pct - focus.Pct;
        if (onTrack > 0.5) onTrack -= 1;
        if (onTrack < -0.5) onTrack += 1;
        int laps = (int)Math.Round((car.Lap + car.Pct) - (focus.Lap + focus.Pct) - onTrack);
        return laps > 0 ? LapRelation.LapsAhead : laps < 0 ? LapRelation.LapsBehind : LapRelation.SameLap;
    }

    public static int StrengthOfField(IEnumerable<int> iRatings)
    {
        var valid = iRatings.Where(r => r > 0).ToList();
        if (valid.Count == 0) return 0;
        double k = 1600 / Math.Log(2);
        double sum = valid.Sum(r => Math.Exp(-r / k));
        return (int)Math.Round(k * Math.Log(valid.Count / sum));
    }

    private static string ShortLicense(string lic)
    {
        // "A 4.12" -> "A 4.1"
        var parts = lic.Split(' ', 2);
        if (parts.Length == 2 && float.TryParse(parts[1], NumberStyles.Float, Inv, out var sr))
            return parts[0] + " " + (Math.Floor(sr * 10) / 10).ToString("0.0", Inv);
        return lic;
    }

    public static string FormatLap(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds <= 0) return "-:--.---";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalMinutes >= 1
            ? $"{(int)ts.TotalMinutes}:{ts.Seconds:00}.{ts.Milliseconds:000}"
            : $"{ts.Seconds}.{ts.Milliseconds:000}";
    }

    private static string FormatClock(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }
}
