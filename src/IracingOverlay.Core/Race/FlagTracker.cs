namespace IracingOverlay.Core.Race;

/// <summary>
/// Remembers when each car's penalty flags were raised, so the overlay can show how long they've been active.
/// iRacing doesn't publish a penalty timer: a slowdown (the furled black flag) simply clears once the driver has
/// given back enough time, and a meatball once the car has been repaired.
/// </summary>
public sealed class FlagTracker
{
    private static readonly DriverFlags[] Priority =
        [DriverFlags.Disqualified, DriverFlags.Black, DriverFlags.Repair, DriverFlags.Furled];

    private readonly Dictionary<(int Car, DriverFlags Flag), double> _since = [];
    private (int Id, int Number) _session = (-1, -1);

    internal void BeginFrame(int sessionId, int sessionNumber)
    {
        if (_session == (sessionId, sessionNumber)) return;
        _session = (sessionId, sessionNumber);
        _since.Clear();
    }

    /// <summary>Seconds the most important active flag of this car has been up (0 when it has none).</summary>
    internal double Observe(int car, DriverFlags flags, double time)
    {
        double seconds = 0;
        bool primary = true;
        foreach (var flag in Priority)
        {
            if ((flags & flag) == 0) { _since.Remove((car, flag)); continue; }
            if (!_since.TryGetValue((car, flag), out double since) || since > time) _since[(car, flag)] = since = time;
            if (primary) { seconds = time - since; primary = false; }
        }
        return seconds;
    }
}
