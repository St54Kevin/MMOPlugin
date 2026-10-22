# MMO Plugin & Overlays for SimHub

A SimHub plugin and overlay suite for **Le Mans Ultimate (LMU)**, focused on modern multiclass racing. The plugin reads LMU shared-memory and API data and exposes it as SimHub properties for the accompanying overlays and custom dashboards.

The project is maintained by **Kevin_Karas**. The overlay suite builds on the original [LMU SimHub MMO Modern Multiclass Overlay](https://www.overtake.gg/downloads/lmu-simhub-mmo-modern-multiclass-overlay-standings-relative-tires.74112/).

## What the plugin provides

- Per-car overall and class positions, driver and team names, car number and class.
- Relative gaps ahead and behind, plus race gap and interval values.
- Lap timing, invalid-lap state, finish status, penalties and incidents.
- Pit status and the last pit lap and pit-lane stop duration.
- Tyre compounds for all four wheels, with cached values to avoid flickering.
- Player-specific properties that stay tied to the player's car rather than its current position.
- Player fuel and virtual-energy (VE) consumption estimates, laps remaining and the RefuelVE estimate.
- Configurable settings for leaderboard team names, relative team labels, tyre-compound symbols, and pit-stop information.
- A plugin page with live property values, version/update checks, and an overlay package page that compares installed overlay versions with the latest release.

The plugin publishes properties under the `MMOPlugin.MMO_...` namespace. Property values can be used directly in SimHub Dash Studio and NCalc formulas.

## Overlay overview

The overlay package contains the following displays. Names and versions can vary between package releases.

| Overlay | Description |
|---|---|
| **MMO - Leaderboard** | Multiclass race standings with overall and class positions and supporting race-status indicators. |
| **MMO - Relative** | Cars ahead and behind with relative gaps; includes team-name variants and race fuel/VE information where available. |
| **MMO - LapInfo** | Compact lap and timing information, including lap-time context such as outlaps. |
| **MMO - MiniCarData** | A compact per-car information display for use alongside the main leaderboard or relative. |
| **MMO - Notification Center** | A dedicated area for overlay and race notifications. |
| **MMO - Rev Bar** | Compact rev and shift-light display. |
| **MMO - SectorBox** | Sector-time information in a separate timing box. |
| **MMO - TireInfoHor** | Horizontal tyre telemetry widget, including tyre and brake temperatures, pressures, wear and compounds when supplied by LMU. |
| **MMO - TireInfoVer** | Vertical layout of the tyre telemetry widget. |
| **MMO - Map** | Track map display. |

The suite also includes leaderboard/relative variants and tyre displays described on the [original OverTake page](https://www.overtake.gg/downloads/lmu-simhub-mmo-modern-multiclass-overlay-standings-relative-tires.74112/). Available items depend on the overlay package version.

## Install

1. Download the latest plugin DLL and `MMOverlays_*.zip` package from [GitHub Releases](https://github.com/St54Kevin/MMOPlugin/releases).
2. Install the plugin DLL in SimHub using the release instructions, then restart SimHub if requested.
3. Install the `.simhubdash` overlays from the ZIP through SimHub Dash Studio.
4. Open the **MMO Plugin** page in SimHub to inspect plugin status, overlay package updates and settings.

The plugin's **Overlays** page can check the latest release, compare overlay titles and versions from each dashboard's metadata with the installed SimHub overlays, and install or update selected overlays.

## Build from source

- Visual Studio with **.NET Framework 4.8** targeting support.
- SimHub installed, so the project can resolve its SimHub assemblies.

Open `MMOPlugin.slnx` and build the solution. If the project reports missing SimHub assembly references, update the reference paths in `MMOPlugin/MMOPlugin.csproj` to match your SimHub installation.

## Data availability

The plugin can only expose data that LMU provides through its shared memory or API. Some information, such as custom team names or certain car-specific values, may be unavailable or incomplete depending on the session and LMU data.

## Credits

- **Plugin and overlay suite:** Kevin_Karas
- **Original overlay release and feature background:** [OverTake.gg](https://www.overtake.gg/downloads/lmu-simhub-mmo-modern-multiclass-overlay-standings-relative-tires.74112/)

