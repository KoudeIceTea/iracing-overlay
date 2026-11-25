using IracingOverlay.Core.Sdk;

namespace IracingOverlay.Core.Race;

/// <summary>What iRacing's spotter reports beside your car (CarLeftRight).</summary>
public enum SideTraffic
{
    Off = 0,
    Clear = 1,
    Left = 2,
    Right = 3,
    Both = 4,
    TwoLeft = 5,
    TwoRight = 6,
}

/// <summary>Cars close to your own car, of every class, for the proximity overlay.</summary>
public sealed record RadarSnapshot
{
    /// <summary>You're driving (not in the garage, spectating or watching a replay).</summary>
    public bool Active { get; init; }
    public SideTraffic Sides { get; init; }
    /// <summary>
    /// Cars beside you, innermost first: metres ahead (+) or behind (−) of your car's centre, within a car length.
    /// The spotter can report a car beside you that iRacing's positions don't place close enough (they arrive with
    /// some delay); then the bar sits level with your car.
    /// </summary>
    public IReadOnlyList<double> Left { get; init; } = [];
    public IReadOnlyList<double> Right { get; init; } = [];
    /// <summary>Distance to the nearest car ahead / behind that isn't beside you (metres, positive).</summary>
    public double? Ahead { get; init; }
    public double? Behind { get; init; }
}

/// <summary>
/// Finds the cars close to you. iRacing sends only how far along the lap each car is, not where it is across the
/// track, so the spotter's left/right report decides which cars are beside you; every other close car counts as
/// ahead or behind, wherever it is across the track.
/// </summary>
public sealed class RadarTracker
{
    /// <summary>Cars further away than this are ignored (the largest warning distance plus a margin).</summary>
    public const double MaxDistance = 60;
    public const double CarLength = 4.6;
    /// <summary>How far from your car a car reported beside you can appear, given the network delay.</summary>
    private const double SideWindow = 12;

    private readonly Dictionary<int, (double Distance, double Time)> _last = [];
    private readonly Dictionary<int, double> _closing = []; // m/s towards you, smoothed
    private readonly Dictionary<int, int> _side = [];        // car → side it was last seen beside you on (-1 / 1)
    private int _context = -1;

    internal RadarSnapshot Build(TelemetryFrame t, SessionData s, int player, int sessionId)
    {
        if (_context != sessionId)
        {
            _context = sessionId;
            _last.Clear();
            _closing.Clear();
            _side.Clear();
        }
        double length = s.TrackLengthMeters;
        float own = t.GetFloat("CarIdxLapDistPct", player, -1);
        bool driving = t.GetBool("IsOnTrack") && !s.IsReplay && !t.GetBool("IsReplayPlaying");
        if (!driving || own < 0 || length <= 0) return new RadarSnapshot { Active = driving };

        double now = t.GetDouble("SessionTime");
        bool ownPit = t.GetBool("CarIdxOnPitRoad", player);
        var near = new Dictionary<int, double>();
        foreach (var d in s.Drivers.Values)
        {
            if (d.CarIdx == player || d.IsSpectator || d.IsPaceCar) continue;
            float pct = t.GetFloat("CarIdxLapDistPct", d.CarIdx, -1);
            if (pct < 0 || t.GetInt("CarIdxTrackSurface", d.CarIdx, -1) == -1) continue;
            // The pit lane runs alongside the track: only cars on the same side of the pit wall count.
            if (t.GetBool("CarIdxOnPitRoad", d.CarIdx) != ownPit) continue;
            double gap = pct - own;
            if (gap > 0.5) gap -= 1;
            else if (gap < -0.5) gap += 1;
            double metres = gap * length;
            if (Math.Abs(metres) <= MaxDistance) near[d.CarIdx] = metres;
        }

        // How fast each car closes in on you: the car the spotter has just picked up must be coming closer.
        foreach (var (idx, distance) in near)
        {
            if (_last.TryGetValue(idx, out var last) && now > last.Time && now - last.Time < 1)
            {
                double rate = (Math.Abs(last.Distance) - Math.Abs(distance)) / (now - last.Time);
                _closing[idx] = _closing.TryGetValue(idx, out var smoothed) ? smoothed + (rate - smoothed) * 0.3 : rate;
            }
            _last[idx] = (distance, now);
        }
        foreach (int gone in _last.Keys.Where(idx => !near.ContainsKey(idx)).ToList())
        {
            _last.Remove(gone);
            _closing.Remove(gone);
            _side.Remove(gone);
        }

        var sides = (SideTraffic)t.GetInt("CarLeftRight");
        int left = sides switch { SideTraffic.Left or SideTraffic.Both => 1, SideTraffic.TwoLeft => 2, _ => 0 };
        int right = sides switch { SideTraffic.Right or SideTraffic.Both => 1, SideTraffic.TwoRight => 2, _ => 0 };

        // Who is beside you: the car already beside you on that side, then the nearest car — counting one that
        // isn't closing in as further away, since the spotter picks up a car as it draws alongside.
        double Score(int idx) => Math.Abs(near[idx]) + (_closing.GetValueOrDefault(idx) > 0.3 || Math.Abs(near[idx]) < CarLength ? 0 : 6);
        var candidates = near.Keys.Where(idx => Score(idx) <= SideWindow).OrderBy(Score).ToList();
        var beside = new HashSet<int>();
        List<int> Pick(int count, int side)
        {
            var picked = new List<int>();
            foreach (int idx in candidates.Where(idx => _side.GetValueOrDefault(idx) == side).Concat(candidates))
            {
                if (picked.Count == count) break;
                if (beside.Contains(idx) || _side.GetValueOrDefault(idx) == -side) continue;
                picked.Add(idx);
                beside.Add(idx);
            }
            return picked;
        }
        var leftCars = Pick(left, -1);
        var rightCars = Pick(right, 1);
        foreach (int idx in leftCars) _side[idx] = -1;
        foreach (int idx in rightCars) _side[idx] = 1;
        // A car that is no longer beside you can come alongside on either side next time.
        foreach (int idx in _side.Keys.Where(idx => !beside.Contains(idx)).ToList()) _side.Remove(idx);

        double Alongside(int idx) => Math.Clamp(near[idx], -CarLength, CarLength);
        var others = near.Where(c => !beside.Contains(c.Key)).Select(c => c.Value).ToList();
        return new RadarSnapshot
        {
            Active = true,
            Sides = sides,
            Left = leftCars.Select(Alongside).Concat(Enumerable.Repeat(0.0, left - leftCars.Count)).ToArray(),
            Right = rightCars.Select(Alongside).Concat(Enumerable.Repeat(0.0, right - rightCars.Count)).ToArray(),
            Ahead = others.Where(d => d >= 0).Select(d => (double?)d).Min(),
            Behind = others.Where(d => d < 0).Select(d => (double?)-d).Min(),
        };
    }
}
