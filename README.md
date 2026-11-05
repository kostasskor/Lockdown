# LOCKDOWN

A multiplayer FPS map in the style of Black Ops 2–era maps, set in a fictional hillside village inspired by the mountain villages of Crete. Built in **Unity 6 (URP)**.

The project is about **level design and technical art**. The map is built by data-driven tools, not by hand-placing boxes. The terrain is real elevation data. The layout lives in one JSON file that the greybox, the play terrain and the review checks all read. Houses come out of a procedural generator with realistic construction detail and hand-made imperfections. Automated checks test every build for reachability, sightlines, timings and mesh quality.

> **Status: work in progress.** The full layout is greyboxed and playable on foot. One fully detailed test house and a lit test corner are done. Final art, lighting and dressing come next.

<!-- Screenshots / video: add hero images to docs/media/ and link them here. -->

---

## Highlights

| Area | What it does | Where |
|---|---|---|
| **Procedural house generator** | Turns a `HouseSpec` (pure data) into a game-ready house: LOD0 mesh, a box-simple LOD1, a box collision mesh and a prefab with a `LODGroup`. Doors and windows follow real anatomy (stiles, rails, panels, gaskets, hinges, levers). Openings, walls and roof edges get seeded imperfections (out-of-plumb walls, sagging tops, jittered openings). Weathering decals go where water and dirt really collect. | `Assets/Lockdown/Editor/LockdownHouse*.cs` |
| **Two-layer plaster shader** | A custom URP shader built on URP 17's own Lit passes: two plaster layers blended along a noisy edge, stochastic **hex tiling** with no visible repetition, a large-scale tone map and a per-wall UV offset. The same shader also weathers wood and metal. | `Assets/Lockdown/Shaders/LockdownPlaster*` |
| **Real-world terrain** | Copernicus GLO-30 elevation data reprojected to a local metric grid. A 2 km village terrain sits inside a 40 km backdrop, so the real horizon and mountains are in place. | `Tools/terrain/lockdown_dem.py`, `LockdownGroundBuilder.cs` |
| **Data-driven layout & greybox** | One layout JSON holds the lanes, spawns, flags, landmarks, stairs and boundary. Python tools grade the play terrain and generate the greybox (including stairs with rails that follow the flight, and stone-paved paths). Sections are locked once approved, so new work never overwrites them. | `Assets/Lockdown/Data/Layout/`, `Tools/layout/`, `LockdownGreybox.cs` |
| **Automated level-design QA** | The **Containment test** floods the map with the real `CharacterController` (slopes, steps, jumps, mantles, drops) from every spawn, in both the build and editor worlds. The **seal test** probes every roadblock. The **walk test** walks every route. Python checks measure sprint times between flags, sightline lengths, spawn exposure, perch balance and chokepoints. | `LockdownContainment.cs`, `Tools/review/` |
| **Asset QA** | Coplanar-face audit (z-fighting), LOD-pair captures for invisible LOD switches, and straight-on wall captures scored for repeated or mirrored stains. | `LockdownMeshAudit.cs`, `LockdownHouseReview.cs`, `Tools/review/symmetry.py` |
| **Game-ready props** | CC0 scans are simplified to a budget (LOD0 + silhouette-preserving LOD1, dither cross-fade) and turned into prefabs. | `Tools/assets/lockdown_props.py`, `LockdownPropBuilder.cs` |
| **First-person walker** | Player-scale controller: walk, sprint, crouch, slide, jump with coyote time and jump buffer, head bob, strafe lean, landing dip, FOV kick, compass HUD and fps counter. | `Assets/Lockdown/Scripts/Player/` |

## How it fits together

```
 Real elevation (Copernicus DEM)            Layout data (lockdown_layout.json)
          │                                    │              │
 Tools/terrain/lockdown_dem.py     Tools/terrain/          Tools/layout/
          │                       lockdown_playground.py    lockdown_greybox.py
          ▼                                    │              │
 Lockdown/Build/Real Ground       Lockdown/Build/Play Ground  Lockdown/Build/Greybox
          └──────────────┬─────────────────────┴──────────────┘
                         ▼
               Lockdown_Lockdown.unity  ◄── houses: HouseSpec ─► LockdownHouseBuilder ─► meshes + LOD prefab
                         │              ◄── props:  CC0 scans ─► lockdown_props.py ─► LockdownPropBuilder
                         ▼
     QA: Containment / Seal / Walk tests (Unity)  +  Tools/review/*.py (timings, sightlines, balance)
```

Every builder can be re-run and replaces only its own output. Review fixes change the **data** (the layout JSON or a `HouseSpec`), never hand-edited geometry.

## Repository layout

```
Assets/Lockdown/
  Data/Layout/      lockdown_layout.json: the map layout (single source of truth)
  Editor/           Editor tools: builders (ground, play ground, greybox, houses, props), QA tests, review tools
  Scripts/          Runtime: Player/ (walker, compass), Gameplay/ (out-of-bounds), Review/ (fps counter)
  Shaders/          Plaster, greybox grid, decal and prop shaders (URP)
  Materials/ Models/ Prefabs/ Textures/ Terrain/   generated and authored content (Models/Generated = generator output)
  Scenes/           Lockdown_Lockdown.unity (the map) + Review_* scenes (test house, test corner, walker test area)
  Settings/         URP asset/renderer, volume profiles, review lighting settings
  ThirdParty/       Downloaded CC0 assets (Poly Haven, ambientCG), listed in thirdparty_manifest.json
Tools/              Python pipeline: terrain/, layout/, assets/, review/ (see Tools/README.md)
docs/               Design reviews and measurements
```

All C# lives in the `Lockdown` (runtime) and `Lockdown.EditorTools` (editor) namespaces. Editor tools are under the **Lockdown** menu: `Lockdown/Build/*` builds content, `Lockdown/Review/*` holds review kits and QA tests.

## Getting started

**Requirements:** Unity **6000.2.9f1** (URP 17.2), Git LFS, and optionally Python 3.11+ with [uv](https://docs.astral.sh/uv/) for the data tools.

1. `git lfs install`, clone, then open the project in Unity Hub.
2. Open `Assets/Lockdown/Scenes/Lockdown_Lockdown.unity` and press Play. Controls: WASD, mouse, Shift sprint, Ctrl or C crouch (slide while sprinting), Space jump, F switches the FOV, Esc releases the mouse, F1 shows the fps counter, F2 the compass.
3. To rebuild from source data, run the Python tools (see `Tools/README.md`; downloaded source data goes to `SourceData/`, which isn't committed), then use the `Lockdown/Build/*` menu items.

Baked lighting data isn't committed. It's regenerated from the committed lighting settings.

## Technical targets

- Stable 60 fps at 1080p on a GTX 1660 Ti (6 GB): mostly baked lighting, LODs everywhere, 2K textures max, GPU Resident Drawer.
- House budget ~58k vertices LOD0 / ~17k LOD1. Small props a few thousand vertices with an LOD1.
- Realistic PBR only: CC0 scans tinted to albedo values sampled from reference photos.

## Credits & license

- Third-party assets (all CC0: Poly Haven, ambientCG) are listed with source, author and license in [`Assets/Lockdown/thirdparty_manifest.json`](Assets/Lockdown/thirdparty_manifest.json). Elevation data: Copernicus GLO-30 DEM © DLR e.V. / Airbus, provided under COPERNICUS by the European Union and ESA.
- Baked lighting uses Bakery (a paid Asset Store tool). It isn't part of this project's license and isn't redistributed in public copies.
- Source code (C#, shaders, Python) is under the MIT License (see [LICENSE](LICENSE)). The map's own design, layout data, scenes and generated content are © Konstantinos Skordilis, all rights reserved.
