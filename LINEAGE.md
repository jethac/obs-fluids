# Lineage

Ink Container is original code. It is not a fork of Pavel Dobryakov’s WebGL fluid demo, and it does not contain Autodesk Maya source. The solver and the look sit on two public lineages that anyone implementing real-time dye fluids will bump into.

## Jos Stam / Alias|Wavefront / Autodesk Maya Fluids

The method is **Stable Fluids** (Stam, SIGGRAPH 1999) as popularized for games in **Real-Time Fluid Dynamics for Games** (Stam, GDC 2003). Stam did that work at Alias|Wavefront; Autodesk shipped it in Maya as **Fluid Effects** (`fluidShape`, 2D and 3D containers).

Maya Fluids is a production system with a much larger surface than this demo (3D volumes, fuel, caches, Maya fields, Arnold, etc.). What we borrowed is the *vocabulary and the 2D-container look*, not Autodesk code, assets, or trademarks as a product name.

| Maya fluidShape idea | What this repo does |
|---|---|
| 2D fluid container | Clamp-to-edge domain, optional boundary dropoff |
| Density, velocity, temperature | RGBA dye + RG velocity + R temperature fields |
| Viscosity, dissipation | Explicit smoothing pass + exponential decay |
| Swirl | Vorticity confinement |
| Buoyancy / gravity | Temperature rises, density can sink |
| Color + incandescence + opacity | Dye albedo, fire-ramp glow, optical-depth alpha |
| Self shadow | 2D extinction march along a key light |
| Attribute Editor / channel box | Compact right-hand `fluidShape1` panel |
| Viewport HUD + film-gate corners | Live readout and brass L-corners |

We did **not** implement Maya’s 3D voxel solver, fuel/combustion, texture-grid deformers, ocean shader, or any Maya scene format.

## GPU Stam pipeline (Harris, then the web)

The GPU factorization — splat forces, vorticity confinement, divergence, Jacobi pressure, gradient subtract, semi-Lagrangian advection, ping-pong render targets — is the standard one from Mark Harris’s GPU Gems chapter on fast fluid dynamics and from a decade of WebGL ports.

[Pavel Dobryakov’s WebGL Fluid Simulation](https://github.com/PavelDoGreat/WebGL-Fluid-Simulation) (MIT, 2017) is the best-known browser embodiment of that pipeline. This project used it as a **quality and feature reference** (along with the public “one-shot a single-file GPU fluid” prompt that recites the same checklist): pointer splat, higher-resolution dye, bloom, dissipation sliders, vorticity. The shaders, host, UI, and extra physics here were written from scratch. We do not copy Pavel’s source tree, texture of `dat.gui`, or mobile apps.

### Same family of steps

Both Pavel’s demo and `web/fluid.html` do, in some form:

- Gaussian splat of dye and momentum
- Curl → vorticity confinement
- Divergence → Jacobi pressure → subtract gradient
- Advect velocity and dye
- Optional bloom on a beauty pass
- Dye grid denser than the velocity grid

Those steps are the published method, not a particular repository.

### Where this code diverges from Pavel

| | Pavel (2017 demo) | Ink Container |
|---|---|---|
| Language / runtime | WebGL app, MIT source | Native D3D11 HLSL in the exe; optional WebGL page for GitHub Pages only |
| Dye advection | Bilinear (manual when needed) | MacCormack with neighborhood clamp |
| Temperature / buoyancy | No | Yes (Fedkiw-style rise) |
| Shading | Optional lighting, sunrays, bloom | Maya-like self-shadow, incandescence ramp, container dropoff, no sunrays |
| Color | Random HSV splats, neon HDR | Two-ink palettes (teal/amber, cyan/magenta), optical-depth composite on black |
| UI | dat.gui-style overlay | Maya channel-box / viewport HUD |
| Drive | Per-slider | One analog **ENERGY** pot (0 still → 10 storm) plus the sliders |
| Host | Browser tab | Browser, Windows screensaver (`.scr`), OBS Browser Source, borderless capture window |
| Hardware | Pointer | Pointer, `[` `]`, wheel, Web MIDI, WebHID / HidSharp for Logitech MX Creative Console |
| Diagnostics | Mostly beauty | Dye, velocity, pressure, divergence, vorticity, temperature |
| License of *this* tree | n/a | MIT, copyright Jetha Chan 2026 |

If you want Pavel’s demo, use Pavel’s repo. If you want Maya, buy Maya. This is a small real-time 2D container for wallpaper, OBS, and a physical dial.

## Papers worth reading

- Jos Stam, *Stable Fluids*, SIGGRAPH 1999
- Jos Stam, *Real-Time Fluid Dynamics for Games*, GDC 2003
- Ronald Fedkiw, Jos Stam, Henrik Wann Jensen, *Visual Simulation of Smoke*, SIGGRAPH 2001 (vorticity confinement, buoyancy)
- Mark Harris, “Fast Fluid Dynamics Simulation on the GPU,” *GPU Gems*

Autodesk, Maya, and Alias|Wavefront are trademarks of their owners. Logitech and MX Creative Console are trademarks of Logitech. This project is not affiliated with Autodesk, Pavel Dobryakov, or Logitech.
