using System.Globalization;
using System.Windows.Media;
using IracingOverlay.Core.Race;

namespace IracingOverlay.App.ViewModels;

/// <summary>One driver row in the standings or relative overlay. Updated in place every tick.</summary>
public sealed class RowViewModel : ObservableObject
{
    private string _position = "", _carNumber = "", _name = "", _license = "", _iRating = "", _iRatingChange = "";
    private string _gap = "", _interval = "", _lastLap = "", _bestLap = "", _averageLap = "";
    private Brush _rowBackground = Palette.Transparent;
    private Brush _numberForeground = Palette.Text;
    private Brush _licenseBackground = Palette.Transparent, _licenseBorder = Palette.Transparent, _licenseForeground = Palette.Text;
    private Brush _nameForeground = Palette.Text, _gapForeground = Palette.Text, _lastLapForeground = Palette.Text;
    private Brush _iRatingChangeForeground = Palette.Green;
    private Brush _bestLapForeground = Palette.Text;
    private bool _inPit, _isFocus;
    private DriverFlags _flags;
    private int _motionCar = -1;
    public event Action<int>? PositionMotion;
    private string _penaltyText = "";
    private string _positionChange = "";
    private Brush _positionChangeForeground = Palette.Green;
    /// <summary>Races: "â–²3" / "â–¼2" class places gained or lost since the start; empty when unchanged or unknown.</summary>
    public string PositionChange { get => _positionChange; private set => Set(ref _positionChange, value); }
    public Brush PositionChangeForeground { get => _positionChangeForeground; private set => Set(ref _positionChangeForeground, value); }
    /// <summary>Meatball: mandatory repairs.</summary>
    public bool Meatball => (_flags & DriverFlags.Repair) != 0;
    /// <summary>Black flag (penalty to serve) or disqualified.</summary>
    public bool BlackFlag => (_flags & (DriverFlags.Black | DriverFlags.Disqualified)) != 0;
    /// <summary>Furled black flag: iRacing's slowdown after cutting the track.</summary>
    public bool Slowdown => (_flags & DriverFlags.Furled) != 0;
    public bool HasPenalty => BlackFlag || Meatball || Slowdown;
    public ImageSource RepairIcon => DriverIcons.RepairFlag.Value;
    public ImageSource BlackFlagIcon => DriverIcons.BlackFlag.Value;
    public ImageSource SlowdownIcon => DriverIcons.SlowdownFlag.Value;
    /// <summary>"DQ", your repair time left, or how long the most important flag has been up ("8s", "1:05").</summary>
    public string PenaltyText { get => _penaltyText; private set => Set(ref _penaltyText, value); }

    private static string PenaltyTime(DriverFlags flags, double seconds, double? repairLeft)
    {
        if ((flags & DriverFlags.Disqualified) != 0) return "DQ";
        if (repairLeft is > 0) return FormatSeconds(repairLeft.Value) + " left";
        return flags == DriverFlags.None ? "" : FormatSeconds(seconds);
    }

    private static string FormatSeconds(double seconds)
    {
        int s = (int)Math.Floor(Math.Max(0, seconds));
        return s < 60 ? $"{s}s" : $"{s / 60}:{s % 60:00}";
    }

    internal void UpdateMotion(int car, int change)
    {
        if (change != 0 || car != _motionCar) PositionMotion?.Invoke(change);
        _motionCar = car;
    }
    private double _opacity = 1;
    private DriverIconInfo? _iconInfo;
    private ImageSource? _brand, _flag, _tyre;
    private TyreCompound _tyreCompound = TyreCompound.Unknown;

    public ImageSource? Brand { get => _brand; private set => Set(ref _brand, value); }
    /// <summary>Current tyre compound icon (standings only); null when unknown.</summary>
    public ImageSource? Tyre { get => _tyre; private set => Set(ref _tyre, value); }
    public ImageSource? Flag { get => _flag; private set => Set(ref _flag, value); }
    public DriverIconInfo? Icons => _iconInfo;

    public string Position { get => _position; private set => Set(ref _position, value); }
    public string CarNumber { get => _carNumber; private set => Set(ref _carNumber, value); }
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string License { get => _license; private set => Set(ref _license, value); }
    public string IRating { get => _iRating; private set => Set(ref _iRating, value); }
    public string IRatingChange { get => _iRatingChange; private set => Set(ref _iRatingChange, value); }
    public string Gap { get => _gap; private set => Set(ref _gap, value); }
    public string Interval { get => _interval; private set => Set(ref _interval, value); }
    public string LastLap { get => _lastLap; private set => Set(ref _lastLap, value); }
    public string BestLap { get => _bestLap; private set => Set(ref _bestLap, value); }
    public string AverageLap { get => _averageLap; private set => Set(ref _averageLap, value); }
    public Brush BestLapForeground { get => _bestLapForeground; private set => Set(ref _bestLapForeground, value); }
    public Brush RowBackground { get => _rowBackground; private set => Set(ref _rowBackground, value); }
    public Brush NumberForeground { get => _numberForeground; private set => Set(ref _numberForeground, value); }
    public Brush LicenseBackground { get => _licenseBackground; private set => Set(ref _licenseBackground, value); }
    public Brush LicenseBorder { get => _licenseBorder; private set => Set(ref _licenseBorder, value); }
    public Brush LicenseForeground { get => _licenseForeground; private set => Set(ref _licenseForeground, value); }
    public Brush NameForeground { get => _nameForeground; private set => Set(ref _nameForeground, value); }
    public Brush GapForeground { get => _gapForeground; private set => Set(ref _gapForeground, value); }
    public Brush LastLapForeground { get => _lastLapForeground; private set => Set(ref _lastLapForeground, value); }
    public Brush IRatingChangeForeground { get => _iRatingChangeForeground; private set => Set(ref _iRatingChangeForeground, value); }
    public bool InPit { get => _inPit; private set => Set(ref _inPit, value); }
    public bool IsFocus { get => _isFocus; private set => Set(ref _isFocus, value); }
    public double Opacity { get => _opacity; private set => Set(ref _opacity, value); }

    private string _teamName = "";
    private bool _hasTeam;
    private double _rowHeight = NormalRowHeight;
    private const double NormalRowHeight = 28, TeamRowHeight = 36;

    /// <summary>Team races: team name shown large, with <see cref="Name"/> (driver in the car) small below.</summary>
    public string TeamName { get => _teamName; private set => Set(ref _teamName, value); }
    public bool HasTeam { get => _hasTeam; private set => Set(ref _hasTeam, value); }
    public double RowHeight { get => _rowHeight; private set => Set(ref _rowHeight, value); }

    private void SetFlags(DriverFlags flags, double seconds, double? repairLeft)
    {
        if (_flags != flags)
        {
            _flags = flags;
            OnPropertyChanged(nameof(HasPenalty));
            OnPropertyChanged(nameof(Meatball));
            OnPropertyChanged(nameof(BlackFlag));
            OnPropertyChanged(nameof(Slowdown));
        }
        PenaltyText = PenaltyTime(flags, seconds, repairLeft);
    }

    public void Update(StandingRow r, NameFormat names, bool showTeam)
    {
        SetFlags(r.Flags, r.FlagSeconds, r.RepairLeft);
        PositionChange = r.PositionsGained is int gained && gained != 0
            ? (gained > 0 ? "â–²" : "â–¼") + Math.Abs(gained).ToString(CultureInfo.InvariantCulture) : "";
        PositionChangeForeground = r.PositionsGained > 0 ? Palette.Green : Palette.Red;
        SetTeam(showTeam ? r.TeamName : "");
        SetCommon(r.Position, r.CarNumber, DriverNames.Format(r.Name, names), r.License, r.LicenseColor, r.IRating, r.ClassColor, r.IsFocus, r.InPit, r.Icons);
        Gap = r.Gap;
        Interval = r.Interval;
        LastLap = r.LastLap;
        BestLap = r.BestLap;
        BestLapForeground = r.IsClassBest ? Palette.Purple : Palette.Text;
        AverageLap = r.AverageLap;
        if (_tyreCompound != r.Tyre)
        {
            _tyreCompound = r.Tyre;
            Tyre = DriverIcons.Tyre(r.Tyre);
        }
        LastLapForeground = r.LastLapKind switch
        {
            LapTimeKind.ClassBest => Palette.Purple,
            LapTimeKind.PersonalBest => Palette.Green,
            _ => Palette.Text,
        };

        if (r.IRatingChange is int delta)
        {
            IRatingChange = (delta >= 0 ? "▲" : "▼") + Math.Abs(delta).ToString(CultureInfo.InvariantCulture);
            IRatingChangeForeground = delta >= 0 ? Palette.Green : Palette.Red;
        }
        else
        {
            IRatingChange = "";
        }

        NameForeground = Palette.Text;
        GapForeground = Palette.Text;
        RowBackground = r.IsFocus ? Palette.FocusRow : Palette.Transparent;
        Opacity = 1;
    }

    public void Update(RelativeRow r, NameFormat names, bool showTeam)
    {
        SetFlags(r.Flags, r.FlagSeconds, r.RepairLeft);
        SetTeam(showTeam ? r.TeamName : "");
        SetCommon(r.Position, r.CarNumber, DriverNames.Format(r.Name, names), r.License, r.LicenseColor, r.IRating, r.ClassColor, r.IsFocus, r.InPit, r.Icons);
        Gap = r.Gap;
        var relationBrush = r.Relation switch
        {
            LapRelation.LapsAhead => Palette.LapsAhead,
            LapRelation.LapsBehind => Palette.LapsBehind,
            _ => Palette.Text,
        };
        NameForeground = relationBrush;
        GapForeground = relationBrush;
        RowBackground = r.IsFocus ? Palette.FocusRow : r.Relation switch
        {
            LapRelation.LapsAhead => Palette.AheadRow,
            LapRelation.LapsBehind => Palette.BehindRow,
            _ => Palette.Transparent,
        };
        Opacity = r.InPit ? 0.5 : 1; // including ourselves
    }

    /// <summary>
    /// Empty relative slot: keeps its place (and the row height) so the overlay stays the same size and
    /// the focus car stays in the middle, but draws nothing.
    /// </summary>
    public void Blank(double height)
    {
        IsFocus = false;
        InPit = false;
        SetFlags(DriverFlags.None, 0, null);
        RowBackground = Palette.Transparent;
        RowHeight = height;
        Opacity = 0;
    }

    private void SetTeam(string teamName)
    {
        TeamName = teamName;
        HasTeam = teamName.Length > 0;
        RowHeight = HasTeam ? TeamRowHeight : NormalRowHeight;
    }

    private void SetCommon(int pos, string number, string name, string license, uint licColor, int iRating,
        uint classColor, bool isFocus, bool inPit, DriverIconInfo icons)
    {
        if (Set(ref _iconInfo, icons, nameof(Icons)))
        {
            Brand = DriverIcons.Brand(icons);
            Flag = DriverIcons.Flag(icons.FlairName);
        }
        Position = pos > 0 ? pos.ToString(CultureInfo.InvariantCulture) : "-";
        CarNumber = "#" + number;
        Name = name;
        License = license;
        IRating = iRating > 0 ? iRating.ToString(CultureInfo.InvariantCulture) : "";

        // Outlined "tinted glass" badges, as in the reference: tinted fill, light border and text.
        LicenseBackground = Palette.WithAlpha(licColor, 0x40);
        LicenseBorder = Palette.WithAlpha(Palette.LightenRgb(licColor, 0.25), 0xCC);
        LicenseForeground = Palette.Lighten(licColor, 0.55);
        // Plain "#24" in the class colour, lightened a touch so dark class colours stay readable on glass.
        NumberForeground = Palette.Lighten(classColor, 0.2);

        IsFocus = isFocus;
        InPit = inPit;
    }
}
