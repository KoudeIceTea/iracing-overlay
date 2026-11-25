namespace IracingOverlay.Core.Race;

/// <summary>What the live delta compares your current lap with (iRacing computes all of them).</summary>
public enum DeltaReference
{
    /// <summary>Your fastest lap this session.</summary>
    BestLap,
    /// <summary>Your best sectors this session, combined.</summary>
    OptimalLap,
    /// <summary>The fastest lap anyone has driven this session.</summary>
    SessionBestLap,
    /// <summary>The best sectors anyone has driven this session, combined.</summary>
    SessionOptimalLap,
    /// <summary>Your previous lap.</summary>
    LastLap,
}

public enum SectorState
{
    /// <summary>Not driven yet.</summary>
    None,
    /// <summary>The sector the car is in now; the time is the time spent in it so far.</summary>
    Running,
    /// <summary>Slower than your best.</summary>
    Slower,
    /// <summary>Your best so far.</summary>
    PersonalBest,
    /// <summary>The best of your class this session.</summary>
    SessionBest,
}

/// <summary>
/// One sector of the lap being driven. Sectors still ahead show the previous lap
/// (<see cref="FromPreviousLap"/>) until the car gets there.
/// </summary>
/// <param name="Delta">Compared with your best time in this sector before this one was set.</param>
public sealed record SectorSplit(int Number, double? Time, double? Delta, SectorState State, bool FromPreviousLap);

/// <summary>Your own lap timing: iRacing's live deltas plus sector times measured by <see cref="SectorTimer"/>.</summary>
public sealed record DeltaSnapshot
{
    public double? BestLapDelta { get; init; }
    public double? OptimalLapDelta { get; init; }
    public double? SessionBestLapDelta { get; init; }
    public double? SessionOptimalLapDelta { get; init; }
    public double? LastLapDelta { get; init; }
    public double? CurrentLap { get; init; }
    public double? LastLap { get; init; }
    public double? BestLap { get; init; }
    /// <summary>Fastest lap in your class this session, and who drove it.</summary>
    public double? SessionBestLap { get; init; }
    public string SessionBestDriver { get; init; } = "";
    /// <summary>The best sector times in your class added up, never slower than the class best lap or your optimal.</summary>
    public double? SessionOptimal { get; init; }
    /// <summary>Your best sector times added up, never slower than your best lap.</summary>
    public double? TheoreticalBest { get; init; }
    public IReadOnlyList<SectorSplit> Sectors { get; init; } = [];

    /// <summary>
    /// The lap time the delta is measured against. The optimal references are added up from the sector times
    /// this app measured (<see cref="IsEstimate"/>): iRacing times its optimal laps with finer splits.
    /// </summary>
    public double? ReferenceTime(DeltaReference reference) => reference switch
    {
        DeltaReference.OptimalLap => TheoreticalBest,
        DeltaReference.SessionBestLap => SessionBestLap,
        DeltaReference.SessionOptimalLap => SessionOptimal,
        DeltaReference.LastLap => LastLap,
        _ => BestLap,
    };

    public static bool IsEstimate(DeltaReference reference) =>
        reference is DeltaReference.OptimalLap or DeltaReference.SessionOptimalLap;

    public double? DeltaTo(DeltaReference reference) => reference switch
    {
        DeltaReference.OptimalLap => OptimalLapDelta,
        DeltaReference.SessionBestLap => SessionBestLapDelta,
        DeltaReference.SessionOptimalLap => SessionOptimalLapDelta,
        DeltaReference.LastLap => LastLapDelta,
        _ => BestLapDelta,
    };
}
