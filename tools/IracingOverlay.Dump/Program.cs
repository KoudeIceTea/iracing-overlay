// Debug tool: prints what the overlay "sees" so you can verify data without the UI.
//   dotnet run --project tools/IracingOverlay.Dump              -> one snapshot
//   dotnet run --project tools/IracingOverlay.Dump -- --yaml f  -> also save raw session YAML to f
//   dotnet run --project tools/IracingOverlay.Dump -- --vars    -> list all telemetry variables
using IracingOverlay.Core.Race;
using IracingOverlay.Core.Sdk;

using var sdk = new IRacingSdk();
if (!sdk.TryOpen() || !sdk.IsConnected)
{
    Console.WriteLine("iRacing is not running / not in a session.");
    return 1;
}

var frame = sdk.ReadTelemetry();
var yaml = sdk.ReadSessionInfoIfChanged();
if (frame == null || yaml == null)
{
    Console.WriteLine("Could not read telemetry.");
    return 1;
}

int yamlArg = Array.IndexOf(args, "--yaml");
if (yamlArg >= 0 && yamlArg + 1 < args.Length)
{
    File.WriteAllText(args[yamlArg + 1], yaml);
    Console.WriteLine($"Session YAML written to {args[yamlArg + 1]}");
}

if (args.Contains("--vars"))
{
    foreach (var v in sdk.Vars.Values.OrderBy(v => v.Name))
        Console.WriteLine($"{v.Name,-32} {v.Type,-8} x{v.Count,-3} {v.Unit,-10} {v.Description}");
    return 0;
}

var session = SessionData.Parse(yaml);
var snap = RaceStateBuilder.Build(frame, session, new RaceOptions(), fuelHistory: new FuelHistory());
var h = snap.Header;

Console.WriteLine($"Track: {h.TrackName} | {h.SessionType} | {h.TotalCars} cars{(h.IsMulticlass ? " (multiclass)" : "")} | {h.TimeRemaining} | laps {h.Laps}");
Console.WriteLine($"Air {h.AirTempC:0.0}C  Track {h.TrackTempC:0.0}C  Focus class {h.FocusClassName} SoF {h.FocusClassSof}  Inc {h.Incidents}/{h.IncidentLimit}  BB {h.BrakeBias?.ToString("0.0") ?? "-"}");
Console.WriteLine($"Game time {h.GameTime}  Wind {(h.Wind is { } w ? $"from {w.FromDegrees:0}° {w.SpeedKph:0} km/h ({(w.RelativeToCar ? "relative to car" : "relative to north")})" : "-")}  Tyres [{string.Join(", ", session.TyreCompounds)}]");
Console.WriteLine($"Drivers in session info: {session.Drivers.Count}, sessions: {string.Join(", ", session.Sessions.Select(s => $"{s.Num}:{s.Type}({s.Results.Count})"))}, mode {session.SimMode}");
Console.WriteLine($"SessionNum={frame.GetInt("SessionNum")} SessionState={frame.GetInt("SessionState")} CamCarIdx={frame.GetInt("CamCarIdx")} PlayerCarIdx={frame.GetInt("PlayerCarIdx")}");
Console.WriteLine($"Fuel data: car={session.FuelDataCarPath}, track={session.FuelDataTrackKey}, kg/L={session.FuelKgPerLitre:0.###}, current={snap.Fuel.Count}, history={snap.Fuel.HistoricalCount}, last={snap.Fuel.Last:0.00}, last5={snap.Fuel.Last5:0.00}, last10={snap.Fuel.Last10:0.00}");

foreach (var c in snap.Classes)
{
    Console.WriteLine();
    Console.WriteLine($"== {c.ClassName} ({c.CarCount} cars, SoF {c.StrengthOfField}){(c.IsFocusClass ? " [focus]" : "")}");
    foreach (var r in c.Rows)
        Console.WriteLine($"{(r.IsFocus ? ">" : " ")}{r.Position,3} #{r.CarNumber,-4} {r.Name,-26} {r.License,-6} {r.IRating,5} {(r.IRatingChange is int d ? d.ToString("+0;-0") : ""),4}  gap {r.Gap,-9} int {r.Interval,-9} last {r.LastLap,-9} {r.LastLapKind,-12} {r.Tyre,-8}{(r.InPit ? " PIT" : "")}{(r.InWorld ? "" : " (not in world)")}");
}

Console.WriteLine();
Console.WriteLine("RELATIVE");
foreach (var r in snap.Relative)
    Console.WriteLine($"{(r.IsFocus ? ">" : " ")}{r.Position,3} #{r.CarNumber,-4} {r.Name,-26} {r.License,-6} {r.IRating,5}  {r.Gap,6}  {r.Relation}{(r.InPit ? " PIT" : "")}");

return 0;
