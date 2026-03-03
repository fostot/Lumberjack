# Lumberjack

An in-game log console for Terraria, built for the [TerrariaModder](https://github.com/fostot) platform. Intercepts all mod log output and displays it in a resizable, scrollable, filterable overlay — like a Quake-style developer console tailored for Terraria mod debugging.

> **Platform:** TerrariaModder (Harmony-based mod injection)
> **Framework:** .NET Framework 4.8
> **Author:** Fostot

> **Bundled Lux Version:** 1.0.0 — [Lux Repository](https://github.com/fostot/Lux)
> **Bundled Monofont Version:** 1.0.0 — [Monofont Repository](https://github.com/fostot/Monofont)

---

> [!CAUTION]
> **Required: Lux UI Library**
>
> Lumberjack depends on **[Lux](https://github.com/fostot/Lux)** — a shared UI widget library that provides the draggable panel system, layout engine, and input handling used by the log console. **Lumberjack will not work without it.**
>
> **Installation:**
> 1. Download `Lux.dll` from the [Lux releases](https://github.com/fostot/Lux/releases) (also bundled in Lumberjack's release zip)
> 2. Place it in your `Terraria/TerrariaModder/core/` folder (next to `TerrariaModder.Core.dll`)

---

> [!IMPORTANT]
> **Optional: Monofont for Proper UI Text**
>
> Lumberjack supports **[Monofont](https://github.com/fostot/Monofont)** — a crisp 8x16 monospace bitmap font that replaces Terraria's blurry variable-width font in the log console. Without it, Lumberjack falls back to Terraria's default font, which can cause misaligned columns, text wrapping quirks, and inconsistent row heights. The log console was designed around a fixed-width font — Terraria's built-in variable-width font doesn't handle column layouts as cleanly.
>
> **Lumberjack works without Monofont installed** — all text will render using Terraria's built-in font as a fallback, but expect some ugly spots and minor layout issues here and there.
>
> **To enable Monofont:**
> 1. Download `Monofont.dll` from the [Monofont releases](https://github.com/fostot/Monofont/releases) (also bundled in Lumberjack's release zip)
> 2. Place it in your `Terraria/TerrariaModder/core/` folder (next to `TerrariaModder.Core.dll`)
> 3. Restart Terraria — Monofont activates automatically

---

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

---

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

---

## Installation

1. Install [TerrariaModder](https://github.com/fostot) into your Terraria directory.
2. **Required:** Place [`Lux.dll`](https://github.com/fostot/Lux) in `Terraria/TerrariaModder/core/`.
3. Copy `Lumberjack.dll` and `manifest.json` into:
   ```
   Terraria/TerrariaModder/mods/lumberjack/
   ```
4. *(Optional but strongly recommended)* Place [`Monofont.dll`](https://github.com/fostot/Monofont) in `Terraria/TerrariaModder/core/` for proper UI text rendering.
5. Launch Terraria via `TerrariaInjector.exe`.
6. Press **~** (tilde) in-game to open the log console.

> **Tip:** The [release zip](https://github.com/fostot/Lumberjack/releases) bundles everything (Lumberjack, Lux, Monofont) with the correct folder structure — just extract into your Terraria directory.

---

## Building from Source

```bash
dotnet build -c Release
```

Output: `bin/Lumberjack.dll`

**Requirements:**
- .NET Framework 4.8 SDK
- `TerrariaModder.Core.dll` and `0Harmony.dll` (provided by TerrariaModder in `core/`)
- `Lux.dll` (shared UI widget library)
- `Monofont.dll` *(optional — build succeeds without it, text falls back to Terraria's font)*

Update the `<HintPath>` entries in `Lumberjack.csproj` if your install paths differ from the defaults.

---

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE).
