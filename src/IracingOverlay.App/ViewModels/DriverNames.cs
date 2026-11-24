namespace IracingOverlay.App.ViewModels;

/// <summary>How driver names are written in an overlay (chosen per overlay in settings).</summary>
public enum NameFormat
{
    /// <summary>Max Verstappen</summary>
    Full,
    /// <summary>Max V.</summary>
    FirstNameInitial,
    /// <summary>M. Verstappen</summary>
    InitialLastName,
    /// <summary>Verstappen</summary>
    LastName,
    /// <summary>VER</summary>
    Code,
}

public static class DriverNames
{
    public static readonly IReadOnlyList<(NameFormat Format, string Label)> Choices =
    [
        (NameFormat.Full, "Full name — Max Verstappen"),
        (NameFormat.FirstNameInitial, "First name + initial — Max V."),
        (NameFormat.InitialLastName, "Initial + last name — M. Verstappen"),
        (NameFormat.LastName, "Last name — Verstappen"),
        (NameFormat.Code, "Three-letter code — VER"),
    ];

    /// <summary>Name column width (DIPs) that fits the format without wasting space.</summary>
    public static int ColumnWidth(NameFormat format, bool standings) => format switch
    {
        NameFormat.Full => standings ? 150 : 165,
        NameFormat.FirstNameInitial => 100,
        NameFormat.InitialLastName => 110,
        NameFormat.LastName => 95,
        NameFormat.Code => 46,
        _ => 150,
    };

    /// <summary>
    /// The last name is the final word ("Jesper Van Gennip" → "Gennip"). iRacing appends a digit to
    /// duplicate member names ("Raúl Valbuena Gil4"); that suffix is dropped from the last name.
    /// </summary>
    public static string Format(string fullName, NameFormat format)
    {
        if (format == NameFormat.Full || string.IsNullOrWhiteSpace(fullName)) return fullName;

        var words = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string first = words[0];
        string last = words.Length > 1 ? words[^1].TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9') : "";
        if (last.Length == 0) last = words.Length > 1 ? words[^1] : first;

        return format switch
        {
            NameFormat.FirstNameInitial => words.Length > 1 ? $"{first} {char.ToUpperInvariant(last[0])}." : first,
            NameFormat.InitialLastName => words.Length > 1 ? $"{char.ToUpperInvariant(first[0])}. {last}" : first,
            NameFormat.LastName => last,
            NameFormat.Code => (last.Length >= 3 ? last[..3] : last).ToUpperInvariant(),
            _ => fullName,
        };
    }
}
