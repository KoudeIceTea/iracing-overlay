using IracingOverlay.Core.Sdk;

namespace IracingOverlay.Core.Race;

/// <summary>
/// Times every car's sectors (the splits in the session info) from its track position, so sector colours can
/// compare you with the best of your class.
/// <para>
/// Samples arrive at the overlay's update rate, so each line crossing is interpolated between the two samples
/// around it. iRacing stops sending the position of cars far away from you (the far side of the track), so other
/// cars often can't be timed through every sector. Crossings during such a gap still keep track of the car's laps
/// but aren't timed; when a lap misses just one sector, that sector is worked out from iRacing's official lap time.
/// Sectors driven partly in the pit lane, and laps interrupted by a tow or reset, aren't timed.
/// </para>
/// </summary>
public sealed class SectorTimer
{
    private const double PreciseGap = 0.6;    // seconds between samples for a crossing to be timed
    private const double RecentData = 2.0;    // a car with older data has no sector "in progress"
    private const double MaxStep = 0.25;      // lap fraction between close samples; more is a tow or reset
    private const double MinSectorTime = 1.0;
    private const double LapTimeWait = 5.0;   // seconds after the line to wait for iRacing's lap time

    private readonly Dictionary<int, CarSectors> _cars = [];
    private readonly Dictionary<int, double?[]> _classBest = [];
    private (int SubSession, int SessionId, int SessionNumber) _session = (-1, -1, -1);
    private double[] _starts = [0];
    private double _time = double.NaN;
    private bool _replaying;

    public int SectorCount => _starts.Length;

    /// <summary>Reads one frame.</summary>
    public void Update(TelemetryFrame t, SessionData s, int sessionId, int sessionNumber)
    {
        // Out of the car, iRacing reports car positions from the replay (a few seconds behind live), so they're
        // timed with the replay clock; switching between the two clocks drops the sectors in progress.
        bool replaying = s.IsReplay || t.GetBool("IsReplayPlaying");
        double time = replaying && t.Has("ReplaySessionTime") ? t.GetDouble("ReplaySessionTime") : t.GetDouble("SessionTime");
        var session = (s.SubSessionId, sessionId, sessionNumber);
        double[] starts = s.SectorStarts.Count > 0 && s.SectorStarts[0] == 0 ? [.. s.SectorStarts] : [0, .. s.SectorStarts];
        if (session != _session || !starts.SequenceEqual(_starts))
        {
            _session = session;
            _starts = starts;
            _cars.Clear();
            _classBest.Clear();
        }
        // The clock going backwards (replay scrub) or switching clocks invalidates every sector in progress.
        bool jumped = double.IsFinite(_time) && time < _time || replaying != _replaying;
        _time = time;
        _replaying = replaying;

        foreach (var d in s.Drivers.Values)
        {
            if (d.IsPaceCar || d.IsSpectator) continue;
            int i = d.CarIdx;
            if (!_cars.TryGetValue(i, out var car) || car.UserId != d.UserId)
                _cars[i] = car = new CarSectors(d.UserId, d.ClassId, _starts.Length);
            float pct = t.GetFloat("CarIdxLapDistPct", i, -1);
            // Cars drop out of the data now and then (single frames, or longer when far away); the next sample
            // bridges the gap.
            if (pct >= 0 && t.GetInt("CarIdxTrackSurface", i, -1) != -1)
            {
                if (jumped)
                {
                    car.Interrupt(clearLap: false);
                    car.Sample(pct, time);
                }
                else Advance(car, pct, time, t.GetBool("CarIdxOnPitRoad", i));
            }
            ReadLapTime(car, t.GetFloat("CarIdxLastLapTime", i), time);
        }
    }

    private void Advance(CarSectors car, double pct, double time, bool onPitRoad)
    {
        if (car.LastTime is not { } t0)
        {
            car.Sample(pct, time);
            return;
        }
        double p0 = car.LastPct, dt = time - t0;
        if (dt <= 0) return;
        double step = pct - p0;
        if (step < -0.5) step += 1; // crossed the start/finish line
        if (step is < 0 and > -0.001) // a parked car's position jitters slightly
        {
            car.LastTime = time;
            return;
        }
        // Backwards, or further than the car could have driven: a tow, reset or replay jump.
        if (step < 0 || step >= 0.5 || step > MaxStep && dt <= RecentData)
        {
            car.Interrupt(clearLap: true);
            car.Sample(pct, time);
            return;
        }
        car.InPit |= onPitRoad;
        car.LapInPit |= onPitRoad;
        bool precise = dt <= PreciseGap;

        // Each sector line crossed since the last sample, in the order they were crossed.
        var crossings = new List<(double Distance, int Line)>(2);
        for (int k = 0; k < _starts.Length; k++)
        {
            double distance = _starts[k] - p0;
            if (distance <= 0) distance += 1;
            if (distance <= step) crossings.Add((distance, k));
        }
        crossings.Sort();
        foreach (var (distance, line) in crossings)
        {
            double at = t0 + distance / step * dt;
            if (precise && car.LineTime is { } started && car.Line is int previous && (previous + 1) % _starts.Length == line && !car.InPit)
                Record(car, car.Current, previous, at - started);
            if (line == 0) car.NewLap(at);
            car.Line = line;
            car.LineTime = precise ? at : null;
            car.InPit = onPitRoad;
        }
        car.Sample(pct, time);
    }

    /// <summary>
    /// Fills in the one sector of a completed lap that couldn't be timed (usually because the car was too far away
    /// to be sent), from iRacing's official lap time.
    /// </summary>
    private void ReadLapTime(CarSectors car, float lapTime, double time)
    {
        // The lap time can change a moment before or after the crossing is seen (much earlier when the car
        // reappears after a gap), so the two are paired when they're close together.
        if (lapTime > 0 && lapTime != car.SeenLapTime) car.LapTimeChangedAt = time;
        car.SeenLapTime = lapTime;
        if (car.LapEndedAt is { } old && (time - old > LapTimeWait || time < old)) car.LapEndedAt = null;
        if (car.LapTimeChangedAt is { } seen && (time - seen > 2 * LapTimeWait || time < seen)) car.LapTimeChangedAt = null;
        if (car.LapEndedAt is not { } ended || car.LapTimeChangedAt is not { } changed || Math.Abs(changed - ended) > LapTimeWait) return;
        car.LapEndedAt = car.LapTimeChangedAt = null;
        var lap = car.Previous;
        int missing = Array.FindIndex(lap, split => split == null);
        if (missing < 0 || Array.FindIndex(lap, missing + 1, split => split == null) >= 0) return;
        double sector = lapTime - lap.Sum(split => split?.Time ?? 0);
        if (sector >= MinSectorTime && sector < lapTime * 0.8) Record(car, lap, missing, sector);
    }

    private void Record(CarSectors car, SectorSplit?[] lap, int sector, double seconds)
    {
        if (seconds < MinSectorTime) return;
        if (!_classBest.TryGetValue(car.ClassId, out var classBest))
            _classBest[car.ClassId] = classBest = new double?[_starts.Length];
        double? previousBest = car.Best[sector];
        var state = classBest[sector] is not { } fastest || seconds <= fastest ? SectorState.SessionBest
            : previousBest is not { } own || seconds <= own ? SectorState.PersonalBest
            : SectorState.Slower;
        lap[sector] = new SectorSplit(sector + 1, seconds, seconds - previousBest, state, false);
        if (previousBest is not { } best || seconds < best) car.Best[sector] = seconds;
        if (classBest[sector] is not { } classTime || seconds < classTime) classBest[sector] = seconds;
    }

    /// <summary>The sectors of one car's lap in progress, for display.</summary>
    public IReadOnlyList<SectorSplit> Splits(int carIdx)
    {
        if (!_cars.TryGetValue(carIdx, out var car))
            return Enumerable.Range(1, _starts.Length).Select(n => new SectorSplit(n, null, null, SectorState.None, false)).ToArray();
        double time = _time;
        int running = car.LastTime is { } seen && time - seen <= RecentData ? SectorAt(car.LastPct) : -1;
        var splits = new SectorSplit[_starts.Length];
        for (int k = 0; k < splits.Length; k++)
        {
            if (k == running)
                splits[k] = new SectorSplit(k + 1, car.Line == k && car.LineTime is { } started ? Math.Max(0, time - started) : null,
                    null, SectorState.Running, false);
            else if (running >= 0 && k > running || car.Current[k] == null)
                splits[k] = car.Previous[k] is { } previous ? previous with { FromPreviousLap = true }
                    : new SectorSplit(k + 1, null, null, SectorState.None, false);
            else splits[k] = car.Current[k]!;
        }
        return splits;
    }

    /// <summary>The best sectors driven in a class added up, once each sector has been timed.</summary>
    public double? ClassOptimal(int classId) =>
        _classBest.TryGetValue(classId, out var best) && best.All(b => b.HasValue) ? best.Sum() : null;

    /// <summary>The car's best sectors added up, once each sector has been timed.</summary>
    public double? TheoreticalBest(int carIdx) =>
        _cars.TryGetValue(carIdx, out var car) && car.Best.All(b => b.HasValue) ? car.Best.Sum() : null;

    private int SectorAt(double pct)
    {
        for (int k = _starts.Length - 1; k > 0; k--)
            if (pct >= _starts[k]) return k;
        return 0;
    }

    private sealed class CarSectors(int userId, int classId, int sectors)
    {
        public int UserId { get; } = userId;
        public int ClassId { get; } = classId;
        public double?[] Best { get; } = new double?[sectors];
        public SectorSplit?[] Current { get; private set; } = new SectorSplit?[sectors];
        public SectorSplit?[] Previous { get; private set; } = new SectorSplit?[sectors];
        public double LastPct;
        public double? LastTime;
        /// <summary>The last sector line crossed, and when (null when that crossing couldn't be timed).</summary>
        public int? Line;
        public double? LineTime;
        /// <summary>On pit road during the sector / lap in progress.</summary>
        public bool InPit, LapInPit;
        /// <summary>A lap just ended and its official time hasn't arrived yet.</summary>
        public double? LapEndedAt;
        /// <summary>When iRacing last reported a new lap time for this car.</summary>
        public double? LapTimeChangedAt;
        public float SeenLapTime;

        public void Sample(double pct, double time)
        {
            LastPct = pct;
            LastTime = time;
        }

        public void NewLap(double time)
        {
            // A lap with a pit stop can't have its missing sector worked out from the lap time.
            LapEndedAt = LapInPit ? null : time;
            if (LapInPit) LapTimeChangedAt = null;
            LapInPit = false;
            Previous = Current;
            Current = new SectorSplit?[Current.Length];
        }

        /// <summary>The sector in progress can't be timed; after a tow or reset the whole lap is void.</summary>
        public void Interrupt(bool clearLap)
        {
            Line = null;
            LineTime = null;
            LapEndedAt = null;
            if (!clearLap) return;
            LapInPit = true;
            Current = new SectorSplit?[Current.Length];
        }
    }
}
