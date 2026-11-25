# iRacing Overlay

Lightweight custom overlays for iRacing (C# / .NET 10 / WPF). Includes **Standings**, **Relative**, and **Fuel**.

## Run

```powershell
dotnet run --project src/IracingOverlay.App
```

- Run iRacing in **borderless windowed** mode, because exclusive fullscreen hides overlays.
- The app starts in the Windows notification area. Double-click the **iRacing Overlay** tray icon or right-click it and choose **Settings** to open the control panel. You can minimize it to the taskbar or close it to return to the tray. Choose **Exit** from the tray menu to close the overlays and stop the app.
- **Settings → General → Start with Windows** starts the app in the tray when you sign in (a per-user startup entry, listed in Task Manager → Startup apps, where it can also be disabled). Only one copy runs at a time: launching it again opens the running copy's settings.
- Enabled panels appear when the simulator executable starts, show a waiting state while telemetry loads, and disappear when it exits. This also works across simulator restarts.
- Press **F6** to cycle **Off → Edit → Live → Off**, even with the control panel minimized. The app starts in **Live** with click-through panels. **Off** hides all panels. **Edit** outlines each enabled panel so you can drag it to move or scroll to resize. Returning to **Live** saves the layout.
- In **Settings → General**, enable **Hide overlays when iRacing loses focus** to hide panels when you switch apps. It is off by default. **Edit** also allows a preview while the settings window has focus.
- Each overlay has four settings tabs. **General** controls visibility and scale. **Content** selects table fields, and **Header** and **Footer** select session information independently. Disabling every field in a strip hides it. The Standings footer starts empty. Changes apply immediately and are saved for the next launch.
- Settings live in `%APPDATA%\IracingOverlay\settings.json`. Besides positions and scale, you can set:
  - `GlassBlur`: real blur of the game behind the panels (default `true`).
  - `StandingsFocusClassRows` / `StandingsOtherClassRows`: rows for your class / for each other class (10 / 3).
  - `RelativeCarsAhead` / `RelativeCarsBehind` (4 / 4) and `UpdateHz` (10).

## Release and distribution

From the repository root on Windows with the .NET 10 SDK installed, build the local Release executable with:

```powershell
dotnet build IracingOverlay.sln -c Release
```

The app is at `src\IracingOverlay.App\bin\Release\net10.0-windows\IracingOverlay.exe`. Keep its build-output files together when running it locally.

To create the self-contained Windows x64 distribution, run:

```powershell
.\scripts\dist.ps1
```

Share `dist\IracingOverlay-win-x64.zip`. Extract it and double-click `IracingOverlay.exe`. The .NET runtime, flags, car and tyre sprites, and tray icon are bundled with the executable. Double-click the tray icon to open Settings. Each user’s preferences are stored in their own Windows profile.

## Look & feel

- **Glass** uses a rounded Windows 11 compositor backdrop for each visible panel and penalty badge. The game shows through the gaps and corner cutouts. With blur disabled, or on Windows versions without host backdrop support, panels use their translucent tint.
- **Standings** show every class, fastest class first. Each class gets a header with its colored pill, car count and SoF, and its own rows: leaders, plus a window around you for your own class.
- **iRating ▲/▼** is a projection using the community-reverse-engineered iRacing formula, assuming the current class order is the finishing order. It ignores non-starters, so it's an estimate.
- Each overlay starts with one shared layout. Under the overlay’s **General** tab, turn off **Sync layouts between sessions** to edit Practice, Qualifying and Race separately in **Content**, **Header** and **Footer**. Each session starts with a copy of your current layout. Turning sync back on is immediate when the layouts match. If they differ, compare field visibility and positions side by side, then choose which session’s layout to share.
- **Standings → General** controls how many leaders appear in each other class and whether rows slide when class positions change. Zero keeps the class summaries. **Standings → Content** controls the column names beside the first class summary and the penalty badges beside affected drivers. The orange meatball means mandatory repairs. BLACK, WARN and DQ indicate a black flag, furled black flag or disqualification.
- **Relative** highlights race traffic by whole laps relative to its focus car. Red marks cars you are lapping or have lapped, and blue marks cars lapping you or already ahead by a lap.
- The session badge shows **P**, **Q**, or **R** for practice, qualifying, or race.
- **Fuel** shows litres on board against tank capacity. Its lap range uses the highest and lowest measured consumption. Each row pairs litres per lap with projected laps to empty at that rate. Under **Fuel → Content**, toggle or reorder last lap, rolling 5 and 10 laps, personal session, team session, and the lowest and highest usage.
- Fuel history starts at the first observed start/finish crossing and uses complete laps. The rolling averages use the available samples until their windows fill. Pit visits, fuel increases, driver swaps, and gaps in telemetry discard the partial lap. A new session or connection starts a fresh history. In a team event, **My session** covers your driving and **Team session** covers the team car’s laps observed locally with fuel telemetry. History stays on the PC running the overlay.
- **Best** shows each driver's session best, with the fastest in each class in purple. **Last** turns purple for a class best and green for a personal best.
- **Avg 10** averages the timed laps available from the driver's most recent ten completed laps. It appears after two known times. Joining mid-session starts the history with the lap data currently available.
- Driver rows in both overlays show the colored car make and the flag the driver selected in iRacing (`FlairName`). Global, missing and unrecognized flairs use the iRacing flag. The artwork ships with the app and works offline.
- Font: Montserrat if installed, otherwise Segoe UI Variable. Header icons come from Segoe Fluent Icons.

Debug what the overlay "sees" without the UI:

```powershell
dotnet run --project tools/IracingOverlay.Dump                       # standings + relative as text
dotnet run --project tools/IracingOverlay.Dump -- --yaml session.yaml # also dump raw session YAML
dotnet run --project tools/IracingOverlay.Dump -- --vars              # list all telemetry variables
```

## Structure

```
src/IracingOverlay.Core      no UI dependencies
  Sdk/IRacingSdk.cs          shared-memory reader (Local\IRSDKMemMapFileName)
  Sdk/TelemetryFrame.cs      typed access to one telemetry sample
  Sdk/YamlLite.cs            lenient parser for iRacing's session-info YAML
  Race/SessionData.cs        drivers, sessions, results from the YAML
  Race/RaceStateBuilder.cs   standings order, gaps, relative timing  <- the interesting logic
  TelemetryService.cs        background polling thread (10 Hz default), reconnects
src/IracingOverlay.App       WPF
  Overlays/OverlayWindow.cs  transparent / topmost / click-through / drag base window
  Overlays/*.xaml            the overlays
  ViewModels/                in-place-updated row view models (no per-tick allocations of UI rows)
tools/IracingOverlay.Dump    console debug tool
```

## iRacing data quirks handled

- **Replays**: `CarClassEstLapTime` is `1.0` and `CarIdxEstTime` is `0`, so relative gaps fall back to track-% × a plausible lap time.
- **Replays**: session results in the YAML are the *final* results, so they aren't used as a live fallback.
- **Disconnected cars** lose live lap data, so in live sessions the session results are used instead.
- **Pre-race / gridding**: no live positions yet, so the order falls back to session results, then qualifying (the grid).
- Car arrays can be longer than 64 (currently 72), so always index by `CarIdx`.
- Number formatting is always invariant culture, so a Dutch locale won't turn `4.2` into `4,2`.
- **Out of the car** (garage) your car has no track position (`CarIdxLapDistPct = -1`). The relative still lists you at 0.0, and times the cars on track from the start/finish line until you drive out. In live sessions, enable **Follow spectated car** under **General** for either overlay to follow the camera while out of the car. Replays and spectator-only sessions follow the camera by default.
- **Tyres**: `CarIdxTireCompound` is an index into `DriverInfo.DriverTires`, which iRacing only lists for *your* car. Other car types are assumed to use the same scheme (index 0 = dry). Icons come from `base-icons-sprite.png` (tiles 30–34: hard, medium, soft, intermediate, wet).
- **Wind**: `WindDir` (the compass direction the wind comes from) minus `YawNorth` (your heading) gives the angle relative to the nose. That's only valid while you're in the car; otherwise the dial is relative to north and labelled `WIND N`.

## Ideas for next steps

- iRating gain/loss estimate per driver
- Inputs/pedal trace, track map
- Multiclass standings view (all classes, grouped)
- `.ibt` replay or mock provider for developing without the sim
