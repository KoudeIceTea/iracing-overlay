using System.Globalization;

namespace IracingOverlay.Core.Race;

/// <summary>Reads iRacing's local fuel history for a car and track. Its values are stored in kilograms per lap.</summary>
internal static class FuelData
{
    private const int MaxSamples = 10;

    public static IReadOnlyList<double> Load(SessionData session)
    {
        if (string.IsNullOrWhiteSpace(session.FuelDataCarPath) ||
            string.IsNullOrWhiteSpace(session.FuelDataTrackKey) ||
            session.FuelKgPerLitre <= 0)
            return [];

        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "fueldata.ini");
        if (!File.Exists(file)) return [];

        string? section = null;
        foreach (string raw in File.ReadLines(file))
        {
            string line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }

            if (!string.Equals(section, session.FuelDataCarPath, StringComparison.OrdinalIgnoreCase)) continue;
            int separator = line.IndexOf('=');
            if (separator < 1 || !string.Equals(line[..separator].Trim(), session.FuelDataTrackKey,
                    StringComparison.OrdinalIgnoreCase)) continue;

            string values = line[(separator + 1)..].Split(';')[0];
            string[] parts = values.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                return [];

            return parts.Skip(1).Take(Math.Clamp(count, 0, MaxSamples))
                .Select(value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double kilograms)
                    ? kilograms / session.FuelKgPerLitre : double.NaN)
                .Where(litres => double.IsFinite(litres) && litres > 0)
                .Take(MaxSamples)
                .ToArray();
        }

        return [];
    }
}
