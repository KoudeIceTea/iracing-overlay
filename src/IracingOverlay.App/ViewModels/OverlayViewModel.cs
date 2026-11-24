using System.Collections.ObjectModel;
using System.Globalization;
using IracingOverlay.Core.Race;

namespace IracingOverlay.App.ViewModels;

/// <summary>Shared state bound by both overlays. Only touched on the UI thread.</summary>
public sealed class OverlayViewModel : ObservableObject
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private string _sessionType = "WAITING", _timeRemaining = "--:--", _laps = "", _trackName = "";
    private string _airTemp = "--", _trackTemp = "--", _sof = "--", _incidents = "--", _totalCars = "--";
    private string _className = "", _brakeBias = "", _clock = "", _gameTime = "--:--";
    private string _windSpeed = "";
    private double _windAngle;
    private bool _isMulticlass, _hasWind;

    /// <summary>Driver name format per overlay; applied to the rows on the next telemetry tick.</summary>
    public NameFormat StandingsNames { get; set; } = NameFormat.Full;
    public NameFormat RelativeNames { get; set; } = NameFormat.Full;
    /// <summary>Show team names per overlay (team races only); applied on the next telemetry tick.</summary>
    public bool StandingsTeams { get; set; } = true;
    public bool RelativeTeams { get; set; }
    /// <summary>Relative slots: always ahead + 1 + behind rows, with the focus car in the middle slot.</summary>
    public int RelativeAhead { get; set; } = 4;
    public int RelativeBehind { get; set; } = 4;

    /// <summary>
    /// Fixed-size relative: the focus car always sits in slot <see cref="RelativeAhead"/> (the middle), cars ahead
    /// fill the slots above it from the bottom up, cars behind the slots below it. Unused slots stay as invisible
    /// rows, so the overlay never changes size and never shifts when cars come and go.
    /// </summary>
    private void SyncRelative(IReadOnlyList<RelativeRow> rows)
    {
        int slots = RelativeAhead + 1 + RelativeBehind;
        while (RelativeRows.Count > slots) RelativeRows.RemoveAt(RelativeRows.Count - 1);
        while (RelativeRows.Count < slots) RelativeRows.Add(new RowViewModel());

        int focus = -1;
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].IsFocus) { focus = i; break; }

        // Row i goes to slot i + offset; with no focus car (not connected to a car) everything is blank.
        int offset = focus < 0 ? int.MinValue / 2 : RelativeAhead - focus;
        var filled = new bool[slots];
        for (int i = 0; i < rows.Count; i++)
        {
            int slot = i + offset;
            if (slot < 0 || slot >= slots) continue;
            RelativeRows[slot].Update(rows[i], RelativeNames, RelativeTeams);
            filled[slot] = true;
        }

        double height = focus >= 0 ? RelativeRows[RelativeAhead].RowHeight : 28;
        for (int slot = 0; slot < slots; slot++)
            if (!filled[slot]) RelativeRows[slot].Blank(height);
    }

    /// <summary>Flat list of <see cref="ClassHeaderViewModel"/> and <see cref="RowViewModel"/> items.</summary>
    public ObservableCollection<object> StandingItems { get; } = [];
    public ObservableCollection<RowViewModel> RelativeRows { get; } = [];

    public string SessionType { get => _sessionType; private set => Set(ref _sessionType, value); }
    private string _sessionBadge = "WAITING";
    public string SessionBadge { get => _sessionBadge; private set => Set(ref _sessionBadge, value); }
    public string TimeRemaining { get => _timeRemaining; private set => Set(ref _timeRemaining, value); }
    public string Laps { get => _laps; private set => Set(ref _laps, value); }
    public string TrackName { get => _trackName; private set => Set(ref _trackName, value); }
    public string AirTemp { get => _airTemp; private set => Set(ref _airTemp, value); }
    public string TrackTemp { get => _trackTemp; private set => Set(ref _trackTemp, value); }
    public string Sof { get => _sof; private set => Set(ref _sof, value); }
    public string Incidents { get => _incidents; private set => Set(ref _incidents, value); }
    public string TotalCars { get => _totalCars; private set => Set(ref _totalCars, value); }
    /// <summary>Focus car's class; shown in the header only for single-class sessions.</summary>
    public string ClassName { get => _className; private set => Set(ref _className, value); }
    /// <summary>"60.0%" or empty when unavailable.</summary>
    public string BrakeBias { get => _brakeBias; private set => Set(ref _brakeBias, value); }
    public string Clock { get => _clock; private set => Set(ref _clock, value); }
    public bool IsMulticlass { get => _isMulticlass; private set => Set(ref _isMulticlass, value); }
    /// <summary>In-game time of day, "HH:mm".</summary>
    public string GameTime { get => _gameTime; private set => Set(ref _gameTime, value); }
    /// <summary>
    /// Rotation of the wind arrow: the direction the wind blows towards, clockwise, 0° = car nose (or north).
    /// The wind comes <em>from</em> the opposite side, so a headwind points straight down.
    /// </summary>
    public double WindArrowAngle { get => _windAngle; private set => Set(ref _windAngle, value); }
    public string WindSpeed { get => _windSpeed; private set => Set(ref _windSpeed, value); }
    public bool HasWind { get => _hasWind; private set => Set(ref _hasWind, value); }

    private void ApplyHeader(SessionHeader h)
    {
        SessionType = string.IsNullOrEmpty(h.SessionType) ? "SESSION" : h.SessionType.ToUpperInvariant();
        SessionBadge = SessionType.Contains("QUAL", StringComparison.Ordinal) ? "Q"
            : SessionType switch { "PRACTICE" or "OPEN PRACTICE" => "P", "RACE" => "R", _ => SessionType };
        TimeRemaining = h.TimeRemaining;
        Laps = h.Laps;
        TrackName = h.TrackName;
        AirTemp = h.AirTempC.ToString("0.0", Inv) + "°";
        TrackTemp = h.TrackTempC.ToString("0.0", Inv) + "°";
        Sof = h.FocusClassSof > 0 ? h.FocusClassSof.ToString(Inv) : "--";
        TotalCars = h.TotalCars.ToString(Inv);
        IsMulticlass = h.IsMulticlass;
        ClassName = ""; // the class header in the standings body shows it now
        Incidents = h.Incidents is not int incidents ? "--"
            : string.IsNullOrEmpty(h.IncidentLimit) || h.IncidentLimit == "unlimited" ? incidents.ToString(Inv)
            : $"{h.Incidents}/{h.IncidentLimit}";
        BrakeBias = h.BrakeBias is float bb ? bb.ToString("0.0", Inv) + "%" : "";
        Clock = DateTime.Now.ToString("HH:mm", Inv);
        GameTime = h.GameTime;
        HasWind = h.Wind != null;
        if (h.Wind is { } wind)
        {
            // Round to whole degrees so the pointer doesn't re-render for sub-degree noise.
            WindArrowAngle = Math.Round(wind.FromDegrees + 180) % 360;
            WindSpeed = Math.Round(wind.SpeedKph).ToString(Inv);
        }

    }

    public void ApplyRelative(RaceSnapshot snapshot)
    {
        ApplyHeader(snapshot.RelativeHeader);
        SyncRelative(snapshot.Relative);
    }

    public void Clear()
    {
        SessionType = "WAITING";
        SessionBadge = SessionType;
        Laps = TrackName = ClassName = BrakeBias = "";
        TimeRemaining = "--:--";
        AirTemp = TrackTemp = Sof = Incidents = TotalCars = "--";
        Clock = WindSpeed = "";
        GameTime = "--:--";
        IsMulticlass = HasWind = false;
        StandingItems.Clear();
        _positions.Clear();
        SyncRelative([]); // keep the fixed number of (now empty) slots
    }

    /// <summary>
    /// Flattens class blocks into one list (class header + rows). Existing view models are reused when
    /// the item at a slot has the same type, so steady-state ticks only update properties.
    /// </summary>
    private Dictionary<int, int> _positions = [], _nextPositions = [];
    private (int SessionId, int Number, int Focus) _context;
    private double _previousTime;

    public void ApplyStandings(RaceSnapshot s, bool animate)
    {
        ApplyHeader(s.Header);
        var context = (s.SessionId, s.SessionNumber, s.StandingsFocus);
        bool continuous = _context == context && s.SampleTime >= _previousTime && s.SampleTime - _previousTime < 3;
        _context = context;
        _previousTime = s.SampleTime;
        _nextPositions.Clear();
        int i = 0;
        foreach (var cls in s.Classes)
        {
            Slot<ClassHeaderViewModel>(i).Update(cls, i == 0);
            i++;
            foreach (var row in cls.Rows)
            {
                var vm = Slot<RowViewModel>(i++);
                vm.Update(row, StandingsNames, StandingsTeams);
                int change = animate && continuous && _positions.TryGetValue(row.CarIdx, out int before)
                    ? before - row.Position : 0;
                vm.UpdateMotion(row.CarIdx, change);
                _nextPositions[row.CarIdx] = row.Position;
            }
        }
        while (StandingItems.Count > i) StandingItems.RemoveAt(StandingItems.Count - 1);
        (_positions, _nextPositions) = (_nextPositions, _positions);
    }

    private T Slot<T>(int index) where T : new()
    {
        if (index < StandingItems.Count)
        {
            if (StandingItems[index] is T existing) return existing;
            var replacement = new T();
            StandingItems[index] = replacement;
            return replacement;
        }
        var created = new T();
        StandingItems.Add(created);
        return created;
    }
}
