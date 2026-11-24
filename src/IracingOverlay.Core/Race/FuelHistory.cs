using IracingOverlay.Core.Sdk;

namespace IracingOverlay.Core.Race;

/// <param name="Level">Fuel on board (litres): live while you're in the car, otherwise the setup fuel (see <paramref name="InGarage"/>).</param>
/// <param name="Count">Laps measured in this session.</param>
/// <param name="HistoricalCount">Laps from iRacing's fueldata.ini still in the 10-lap window (10 at the start, one fewer per measured lap).</param>
/// <param name="InGarage">True when you're not in the car: <paramref name="Level"/> is the setup fuel from the garage.</param>
public sealed record FuelSnapshot(double? Level = null, double? Capacity = null, double? Last = null,
    double? Last5 = null, double? Last10 = null, double? Personal = null,
    double? Minimum = null, double? Maximum = null, int Count = 0, int HistoricalCount = 0, bool InGarage = false)
{
    /// <summary>Races only: laps your class still has to drive until the chequered flag, for "pits to end".</summary>
    public double? LapsToGo { get; init; }
}

/// <summary>
/// Measures line-to-line consumption in litres from live data of your own car while you drive it. iRacing doesn't
/// send a teammate's fuel data to your PC, so their stints can't be measured. The 10-lap window starts filled with
/// iRacing's own history for this car and track (fueldata.ini); each lap you drive replaces the oldest of those.
/// Kept per session and car, so getting out of the car or watching a replay never clears it.
/// </summary>
public sealed class FuelHistory
{
    private const int Window = 10;
    private readonly Queue<double> _recent = new(Window);
    private (int SubSession, int SessionId, int SessionNumber, int Car) _context = (-1, -1, -1, -1);
    private double _time, _lastLevel, _lastPct;
    private int _lastLap;
    private bool _sampling;
    private double? _lineFuel;
    private int _count, _historicalCount;
    private double _total;
    private double? _min, _max, _last, _last5, _last10;

    public FuelSnapshot Update(TelemetryFrame frame, SessionData session, int player, int sessionId, int number, double time)
    {
        double? capacity = session.FuelCapacity > 0 && double.IsFinite(session.FuelCapacity) ? session.FuelCapacity : null;

        var context = (session.SubSessionId, sessionId, number, player);
        if (context != _context)
        {
            _context = context;
            _recent.Clear();
            foreach (double burn in FuelData.Load(session)) _recent.Enqueue(burn);
            _historicalCount = _recent.Count;
            _count = 0;
            _total = 0;
            _last = null;
            RecalculateRollingValues();
            _sampling = false;
        }
        double elapsed = time - _time;
        _time = time;
        bool inCar = frame.GetBool("IsOnTrack");
        double raw = frame.GetDouble("FuelLevel", fallback: double.NaN);
        bool entry = session.Drivers.TryGetValue(player, out var driver) && !driver.IsPaceCar && !driver.IsSpectator;
        double? level = entry && inCar && double.IsFinite(raw) && raw >= 0 ? raw : null;
        double fraction = frame.GetDouble("FuelLevelPct");
        capacity ??= level > 0 && fraction is > 0 and <= 1 ? level / fraction : null;
        int lap = frame.GetInt("Lap", fallback: -1);
        double pct = frame.GetDouble("LapDistPct", fallback: -1);
        bool pit = frame.GetBool("OnPitRoad");
        if (level.HasValue && lap >= 0 && pct is >= 0 and <= 1 && !pit)
        {
            bool crossing = lap == _lastLap + 1 && _lastPct > 0.8 && pct < 0.2;
            bool continuous = _sampling && elapsed is >= 0 and < 3 && raw <= _lastLevel + 0.001 &&
                (crossing || (lap == _lastLap && pct >= _lastPct - 0.001 && pct - _lastPct < 0.2));
            if (!continuous) _lineFuel = null;
            else if (crossing)
            {
                double distance = 1 - _lastPct + pct;
                double line = distance > 0 ? _lastLevel + (raw - _lastLevel) * (1 - _lastPct) / distance : raw;
                if (_lineFuel is double start && start > line) Record(start - line);
                _lineFuel = line;
            }
            _sampling = true;
            _lastLevel = raw;
            _lastPct = pct;
            _lastLap = lap;
        }
        else _sampling = false;

        // Not in the car: show the setup fuel, which is what you'll have when you get in.
        if (!inCar)
            return Snapshot(entry ? session.SetupFuel : null, capacity, inGarage: entry && session.SetupFuel.HasValue);
        return Snapshot(level, capacity, inGarage: false);
    }

    private FuelSnapshot Snapshot(double? level, double? capacity, bool inGarage) =>
        new(level, capacity, _last ?? (_recent.Count > 0 ? _recent.Last() : null), _last5, _last10,
            _count > 0 ? _total / _count : null, _min, _max, _count, _historicalCount, inGarage);

    private void Record(double burn)
    {
        if (_recent.Count == Window)
        {
            _recent.Dequeue();
            if (_historicalCount > 0) _historicalCount--;
        }
        _recent.Enqueue(burn);
        _last = burn;
        _total += burn;
        _count++;
        RecalculateRollingValues();
    }

    private void RecalculateRollingValues()
    {
        _min = _recent.Count > 0 ? _recent.Min() : null;
        _max = _recent.Count > 0 ? _recent.Max() : null;
        _last5 = _recent.Count > 0 ? _recent.TakeLast(5).Average() : null;
        _last10 = _recent.Count > 0 ? _recent.Average() : null;
    }
}
