namespace IracingOverlay.Core.Race;

/// <summary>Lap times observed by one telemetry reader, scoped to the current session.</summary>
public sealed class LapHistory
{
    private const int WindowSize = 10;
    private readonly Dictionary<int, DriverLaps> _cars = [];
    private (int Id, int Number) _session;
    private double _time;

    internal void BeginFrame(int sessionId, int sessionNumber, double time)
    {
        if (_session != (sessionId, sessionNumber) || time < _time) _cars.Clear();
        _session = (sessionId, sessionNumber);
        _time = time;
    }

    internal float Observe(DriverEntry driver, int completed, float lastTime, int bestLapNumber, float bestTime,
        ResultPosition? result)
    {
        int newest = Math.Max(completed, result?.LapsComplete ?? 0);
        if (!_cars.TryGetValue(driver.CarIdx, out var laps) || laps.UserId != driver.UserId || laps.Name != driver.UserName)
        {
            // On a driver swap the car still reports the previous driver's most recent lap.
            laps = new DriverLaps(driver.UserId, driver.UserName, laps == null ? 0 : newest);
            _cars[driver.CarIdx] = laps;
        }
        // When joining mid-session, the best-lap record can also identify the immediately preceding lap.
        if (laps.Latest == 0 && newest >= 2)
        {
            if (bestLapNumber == newest - 1) laps.Record(bestLapNumber, bestTime);
            if (result?.FastestLap == newest - 1) laps.Record(result.FastestLap, result.FastestTime);
        }
        laps.Latest = Math.Max(laps.Latest, newest);
        laps.Record(completed, lastTime);
        // The official results pair a time with its lap count, even for cars in the garage.
        if (result != null) laps.Record(result.LapsComplete, result.LastTime);
        return laps.Average();
    }

    private sealed class DriverLaps(int userId, string name, int ignoreThrough)
    {
        private readonly (int Number, float Seconds)[] _laps = new (int, float)[WindowSize];
        public int UserId { get; } = userId;
        public string Name { get; } = name;
        public int Latest { get; set; }

        public void Record(int lap, float seconds)
        {
            if (lap <= ignoreThrough || lap <= Latest - WindowSize || !float.IsFinite(seconds) || seconds <= 0) return;
            _laps[lap % WindowSize] = (lap, seconds);
        }

        public float Average()
        {
            int count = 0;
            double total = 0;
            foreach (var lap in _laps)
            {
                if (lap.Number == 0 || lap.Number <= Latest - WindowSize) continue;
                total += lap.Seconds;
                count++;
            }
            return count >= 2 ? (float)(total / count) : 0;
        }
    }
}
