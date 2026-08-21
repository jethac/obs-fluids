# Ink Container

[![build](https://github.com/jethac/obs-fluids/actions/workflows/build.yml/badge.svg)](https://github.com/jethac/obs-fluids/actions/workflows/build.yml)
[![license](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

GPU fluid simulation that runs three ways from the same engine:

- **Desktop app** — `InkContainer.exe`, or open `web/fluid.html` in a browser
- **Windows screensaver** — `InkContainer.scr`
- **OBS source** — Browser Source, or a borderless capture window

It is a WebGL2 Stam solver (advect, project, vorticity, buoyancy) with a Maya 2D-container look: density dropoff, self-shadow, temperature incandescence, and a channel-box UI.

The sim itself is one file, `web/fluid.html`, matching the usual “single HTML, no build” fluid-demo brief. The host only wraps that page in WebView2.

**Live demo:** [jethac.github.io/obs-fluids](https://jethac.github.io/obs-fluids/)

MIT licensed. Not a fork of Pavel Dobryakov’s demo and not Autodesk code — method and look are documented in [LINEAGE.md](LINEAGE.md).

## Quick look

```text
web/fluid.html          # the simulation
host/                   # WinForms + WebView2 wrapper
obs/ink-container.lua   # adds a Browser Source to the current scene
```

Open `web/fluid.html` in Edge or Chrome. Drag to stir. `H` hides the panel, `Space` pauses, `R` resets, `1`–`7` switch visualizations. `[` / `]` (or the brass **ENERGY** pot, or the MX Creative Console dial) is the one analog control: 0 is a still pond, 5 is the preset as authored, 10 is a storm.

Presets: **Tech Demo**, **Ink**, **Cloud**, **Fire**, **Fog**.

MX Console setup: [`logi/README.md`](logi/README.md).

## Query string

| Param | Values |
|---|---|
| `mode` | `app` (default), `screensaver`, `obs` |
| `preset` | `demo`, `ink`, `cloud`, `fire`, `fog` |
| `quality` | `low`, `medium`, `high`, `ultra` |
| `alpha` | `1` — density as transparency (OBS overlay) |
| `bloom` | `0` / `1` |
| `attract` | `0` / `1` self-stirring |
| `ui` | `0` hide chrome |
| `hud` | `1` force the viewport readout |
| `energy` | `0`–`1` initial drive (0.5 = authored preset) |

Screensaver and OBS modes hide the panel and keep attract on so the field never goes idle.

## Screensaver

Needs the [WebView2 runtime](https://go.microsoft.com/fwlink/p/?LinkId=2124703) (already on most Windows 11 machines) and, if you used a framework-dependent build, the .NET 10 desktop runtime.

```powershell
.\build.ps1
.\install-screensaver.ps1
```

Or right-click `dist\InkContainer\InkContainer.scr` → **Install**. It is a self-contained ~50 MB exe (WebView2 runtime is already on Windows 11).

Settings (preset, quality, all monitors) live in `%LocalAppData%\InkContainer\settings.json`. Configure from Screen Saver Settings, or run `InkContainer.exe --config`.

`InkContainer.exe` with no args is the interactive desktop app. Move the mouse or press a key to dismiss the screensaver.

## OBS

### Browser Source (usual path)

1. Add **Browser Source**
2. Uncheck Local file, set URL to:

   `https://jethac.github.io/obs-fluids/fluid.html?mode=obs&preset=demo&quality=high`

   Or a local `file:///` path to `web/fluid.html`. Overlay: add `&alpha=1` and enable **Transparent**.
3. Width/height e.g. 1920×1080, FPS 60
4. **Uncheck** “Shutdown source when not visible”
5. **Uncheck** “Refresh browser when scene becomes active”

Right-click the source → **Interact** if you want to stir it by hand.

Or: OBS **Tools → Scripts → +** and add `obs/ink-container.lua`, then **Add to current scene**.

### Window capture

```text
InkContainer.exe --obs
```

Borderless 1920×1080 window, no UI. Capture with Game/Window Capture.

## Build

```powershell
.\build.ps1
```

Produces `dist\InkContainer\` (self-contained win-x64). Keep that folder together; the `.scr` is a copy of the exe.

## Controls (app mode)

| | |
|---|---|
| Drag | inject dye + momentum |
| Middle-click | random color |
| Space | pause |
| R | reset and re-seed |
| C | clear density |
| A | toggle attract |
| H | hide attribute panel |
| 1–7 | shaded dye, raw dye, velocity, pressure, divergence, vorticity, temperature |
| `[` `]` | energy down / up (Shift = fine). MX Creative Console: see [logi/README.md](logi/README.md) |

## License

[MIT](LICENSE) © 2026 Jetha Chan. See [NOTICE](NOTICE) and [LINEAGE.md](LINEAGE.md).
