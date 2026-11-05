# LOCKDOWN

A multiplayer FPS map in the style of Black Ops 2–era maps, set in a fictional hillside village inspired by the mountain villages of Crete. Built in **Unity 6 (URP)**.

The focus is **level design and technical art**: the map is built by data-driven tools, not hand-placed boxes. The terrain is real elevation data, the layout is one JSON file, houses come from a procedural generator, and automated checks test every build.

> **Status: work in progress.** The full layout is greyboxed and playable on foot. One detailed test house and a lit test corner are done.

<!-- Screenshots / video: add hero images here. -->

## Highlights

- **Procedural house generator:** turns a `HouseSpec` (pure data) into a game-ready house with LOD0, LOD1, collision and a LOD prefab. It has real door and window anatomy, seeded imperfections, and weathering placed where water and dirt collect. (`Assets/Lockdown/Editor/LockdownHouse*.cs`)
- **Custom plaster shader:** a two-layer URP shader with hex tiling (no visible repetition), large-scale tone and a per-wall UV offset. (`Assets/Lockdown/Shaders/`)
- **Real-world terrain:** Copernicus GLO-30 elevation data, with a 2 km village tile inside a 40 km backdrop. (`Tools/terrain/`)
- **Data-driven layout & greybox:** one layout JSON drives the play terrain, greybox, stairs and boundary. (`Tools/layout/`, `LockdownGreybox.cs`)
- **Automated QA:** a containment flood test with the real character controller, roadblock seal and walk tests, plus sightline, timing and spawn-balance checks. Mesh audits cover z-fighting, LOD switches and repeated stains. (`LockdownContainment.cs`, `Tools/review/`)
- **First-person walker:** walk, sprint, crouch, slide, jump, a compass HUD and an fps counter. (`Assets/Lockdown/Scripts/Player/`)

## Getting started

Requires **Unity 6000.2.9f1** and **Git LFS**. Python 3.11+ is only needed for the data tools (see `Tools/README.md`).

1. Run `git lfs install`, clone the repo, and open the project in Unity Hub.
2. Open `Assets/Lockdown/Scenes/Lockdown.unity` and press Play.
   - **Controls:** WASD and mouse to move, Shift to sprint, Ctrl/C to crouch or slide, Space to jump, Esc to release the mouse.
   - **Toggles:** F switches the FOV, F1 shows the fps counter, F2 the compass.
3. The build tools are under the **Lockdown** menu (`Lockdown/Build/*` and `Lockdown/Review/*`).

Baked lighting isn't committed. Baking uses Bakery, a paid Asset Store tool that isn't included. Everything else works without it.

## Credits & license

- Third-party assets (CC0, from Poly Haven and ambientCG) are listed in [`thirdparty_manifest.json`](Assets/Lockdown/thirdparty_manifest.json).
- Elevation data: Copernicus GLO-30 DEM © DLR e.V. / Airbus, provided under COPERNICUS by the European Union and ESA.
- The source code is MIT licensed (see [LICENSE](LICENSE)). The map design, layout data, scenes and generated content are © Konstantinos Skordilis, all rights reserved.
