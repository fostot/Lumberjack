# Lumberjack

An in-game log console for Terraria, built for the [TerrariaModder](https://github.com/fostot) platform. Intercepts all mod log output and displays it in a resizable, scrollable, filterable overlay — like a Quake-style developer console tailored for Terraria mod debugging.

## Features

- **Toggle with tilde (~)** — press the tilde key to open/close the console at any time during gameplay
- **Three-column layout** — Timestamp (optional), Mod ID, and Message columns
- **Resizable columns** — drag column dividers to resize (Excel-style), minimum 40px per column
- **Text wrapping** — messages wrap within their cell; rows grow automatically to fit wrapped content
- **MonoFont rendering** — fixed-width bitmap font for consistent, readable log output
- **Mod filter dropdown** — filter logs to a specific mod or view all mods at once
- **Log level toggles** — independently toggle DEBUG, INFO, WARN, and ERROR levels
- **Pixel-based scrolling** — smooth vertical scroll with variable row heights; mouse wheel support with auto-scroll-to-bottom
- **Draggable scrollbar** — proportionally sized thumb for quick navigation through large log buffers
- **Row selection** — click to select a row (auto-copies to clipboard), Shift+click to extend selection
- **Copy support** — Ctrl+C to copy selection, or use the Copy All button for all visible (filtered) entries
- **Clear button** — wipe the log buffer
- **Timestamp toggle** — show/hide the timestamp column via a checkbox
- **Toast notifications** — "Copied to Clipboard" confirmation fades in/out after copy actions
- **Thread-safe log buffer** — up to 1,000 entries with oldest-first eviction
- **Resizable & draggable panel** — move and resize the console window freely; close with the X button or Escape key

## Requirements

- [TerrariaModder](https://github.com/fostot) platform installed
- [Lux](https://github.com/fostot/Lux) — UI widget framework (provides `DraggablePanel`)
- [Monofont](https://github.com/fostot/Monofont) — fixed-width bitmap font renderer
- [HarmonyLib](https://github.com/pardeike/Harmony) — runtime method patching (included with TerrariaModder)

## Installation

1. Download the latest release from the [Releases](https://github.com/fostot/Lumberjack/releases) page
2. The release zip contains:
   - `Lumberjack.dll` — the mod itself
   - `Monofont.dll` — the required font renderer (see version note below)
3. Place `Lumberjack.dll` in your TerrariaModder mods directory
4. Place `Monofont.dll` in the Monofont mod directory (if you don't already have it installed)
5. Ensure [Lux](https://github.com/fostot/Lux) is also installed
6. Launch Terraria — press **~** (tilde) to open the console

## Bundled Monofont Version

Each Lumberjack release bundles a copy of Monofont for convenience. The version included is noted in the release description.

**Check for updates:** Monofont is actively developed. Before using the bundled version, check the [Monofont releases page](https://github.com/fostot/Monofont/releases) to make sure you have the latest version. If a newer Monofont release is available, download it directly from there instead of using the bundled copy.

## Usage

| Action | How |
|---|---|
| Open / close console | Press **~** (tilde) |
| Filter by mod | Click the mod dropdown in the toolbar, select a mod (or "ALL MODS") |
| Toggle log levels | Click **DEBUG**, **INFO**, **WARN**, or **ERROR** buttons in the toolbar |
| Show / hide timestamps | Check or uncheck the **Time** checkbox |
| Resize columns | Drag the dividers between column headers |
| Scroll | Mouse wheel, or drag the scrollbar |
| Select a row | Click on it (auto-copies to clipboard) |
| Select range | Click a row, then Shift+click another row |
| Copy selection | **Ctrl+C** |
| Copy all visible logs | Click **Copy All** |
| Clear logs | Click **Clear** |
| Move the console | Drag the title bar |
| Resize the console | Drag the edges/corners of the panel |
| Close the console | Click **X** or press **Escape** |

## Building from Source

Requires .NET Framework 4.8 SDK.

```
dotnet build -c Release
```

The output `Lumberjack.dll` will be in the `bin/` directory.

### Build Dependencies

The project references these assemblies (not included in this repo):

- `TerrariaModder.Core.dll` — from your TerrariaModder installation
- `Lux.dll` — from the [Lux](https://github.com/fostot/Lux) mod
- `Monofont.dll` — from the [Monofont](https://github.com/fostot/Monofont) mod
- `0Harmony.dll` — from TerrariaModder's core dependencies

Update the `<HintPath>` entries in `Lumberjack.csproj` if your install paths differ from the defaults.

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE).
