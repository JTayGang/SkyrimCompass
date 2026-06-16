# Skyrim Compass — Dalamud Plugin for FFXIV

A Dalamud plugin that renders a **Skyrim-style horizontal compass bar** at the top of
your screen, giving you clean directional awareness without needing the minimap.

---

DALAMUD REPO: 
```
https://raw.githubusercontent.com/JTayGang/SkyrimCompass/main/repo.json
``` 

---

## Features

| | |
|---|---|
| 🧭 | Scrolling N / NE / E / SE / S / SW / W / NW labels with degree tick marks |
| 🔴 | Nearby enemy markers (hostile BattleNPCs only) |
| 🔵 | Nearby player markers |
| 🟢 | Gathering node markers (Mining / Botany) |
| 🟡 | Treasure coffer markers |
| ⚙️ | Fully configurable size, position, colours, visible arc, and marker range |

---

## Requirements

- [XIVLauncher](https://goatcorp.github.io/) with Dalamud enabled (v14+)
- **.NET 10 SDK 10.0.101 or later** — [download here](https://dotnet.microsoft.com/download/dotnet/10.0)
- FFXIV installed and launched with Dalamud at least once

> **Note:** Dalamud v14+ requires .NET 10. If `dotnet --version` prints `8.x` or `9.x`
> you must install the .NET 10 SDK before building.

---

## Building from Source

### 1 — Install .NET 10 SDK

Download **SDK 10.0.101** (or newer) from:
<https://dotnet.microsoft.com/download/dotnet/10.0>

Verify after installing:
```cmd
dotnet --version   # should print 10.x.x
```

### 2 — Set the `DALAMUD_HOME` environment variable

Point it at the Dalamud *dev* directory (the folder that contains `Dalamud.dll`).
The default XIVLauncher path is:

```
%APPDATA%\XIVLauncher\addon\Hooks\dev
```

Set it permanently (run once in a command prompt):

```cmd
setx DALAMUD_HOME "%APPDATA%\XIVLauncher\addon\Hooks\dev"
```

Restart your terminal or IDE after setting it.

### 3 — Build

```sh
dotnet build -c Release
```

The `Dalamud.NET.Sdk` NuGet package is fetched automatically on first build — no
manual DLL references needed.

Output lands in `bin\Release\net10.0-windows\`.

### 4 — Load in Dalamud

**Option A — Dev plugin (recommended for testing)**

1. Open XIVLauncher → ⚙ Dalamud Settings → *Experimental* tab.
2. Add the full path to `bin\Release\net10.0-windows\` as a dev-plugin folder.
3. Re-open the plugin installer and enable **Skyrim Compass**.

**Option B — Manual copy**

Copy `SkyrimCompass.dll` and `SkyrimCompass.json` into your Dalamud
`devPlugins` folder (same *Experimental* tab lets you open it).

---

## In-Game Usage

| Command | Effect |
|---|---|
| `/compass` | Toggle compass on / off |
| `/compass config` | Open the settings window |

The settings window is also reachable from the Dalamud plugin list → ⚙ button.

---

## Configuration

Open with `/compass config`.

### Layout tab
- **Width / Height** — size of the compass bar in screen pixels
- **Y Offset** — distance from the top of the screen
- **Visible Degrees** — how wide a slice of the compass is shown (30°–180°)
- **Font Scale** — scale the N/NE/E… label text
- **Show numeric heading** — shows e.g. `045°` below the bar
- **Rotation Offset** — fudge factor; set to **180** if North and South appear swapped

### Colors tab
Individually tune background, border, cardinal/intercardinal labels, and tick marks.

### Markers tab
Toggle each entity category and choose its dot colour.  
**Max distance** controls how far out (in yalms) entities are detected.

---

## Troubleshooting

**`System.Runtime` version mismatch error** → Install .NET SDK 10.0.101+.

**`ImGuiNET not found`** → Make sure you're using the current `.csproj` (`Dalamud.NET.Sdk/15.0.0`, using
the `Dalamud.Bindings.ImGui` namespace), not an old one with manual `<Reference>` tags or `using ImGuiNET;`.

**N and S are swapped** → Layout tab → set *Rotation Offset* to `180`.

**Compass doesn't appear** → Make sure the plugin is enabled (type `/compass`).

**Build error: cannot find Dalamud.dll** → Confirm `DALAMUD_HOME` is set correctly
and that XIVLauncher has been run at least once (so the `dev` folder is populated).

**API level mismatch** → If Dalamud has updated since this was written, bump
`Dalamud.NET.Sdk` to the latest version on NuGet and update `DalamudApiLevel`
in `SkyrimCompass.json` to match.

---

## How it works (briefly)

- `IClientState.LocalPlayer.Rotation` gives the character's facing angle in radians
  (0 = south, π = north in FFXIV's coordinate system).
  We convert this to a standard compass bearing (0 = N, 90 = E).
- `IObjectTable` supplies nearby entities; we compute each one's bearing from the
  player via `atan2(dx, −dz)` and place a coloured dot on the compass.
- Everything is drawn with `ImGui.GetForegroundDrawList()` so it sits above all
  other UI without needing a dedicated window.

---

## License

MIT — do whatever you like with it.
