# Tools

Python tools for the data side of the pipeline. Run them from the **project root**. Each script's docstring starts with its exact command, e.g.

```
uv run --with numpy --with scipy --with pillow Tools/layout/lockdown_greybox.py
```

Without uv: `pip install -r Tools/requirements.txt`, then `python Tools/<folder>/<script>.py`.

Outputs go either into `Assets/Lockdown/` (textures, meshes) or into `SourceData/` (intermediate data, not committed), which the Unity `Lockdown/Build/*` menu items then read.

## terrain/ — real ground and play ground
| Script | Does | Feeds |
|---|---|---|
| `lockdown_dem.py` | Downloads Copernicus GLO-30 elevation tiles and reprojects them to a local metric grid (true north, metres): a 2 km village tile and a 40 km backdrop | `Lockdown/Build/Real Ground` |
| `lockdown_playground.py` | Grades the real slope under the play area from the layout data (lane heights, connectors, ravine, banks), at 0.5 m | `Lockdown/Build/Play Ground` |
| `lockdown_satellite.py` | Georeferenced aerial reference board with contour lines (review only, not committed) | `Lockdown/Review/Build Satellite Board` |

## layout/ — layout and greybox
| Script | Does | Feeds |
|---|---|---|
| `lockdown_layout.py` | Draws the layout plan sheet (lanes, spawns, flags, sightlines, sun) from `lockdown_layout.json` | review sheets |
| `lockdown_greybox.py` | Generates the greybox blocks, stairs, rails and boundary from the layout data into `greybox_meta.json` | `Lockdown/Build/Greybox` |
| `lockdown_stones.py` | Flagstone packing for the stone-paved paths (used by the greybox) | `lockdown_greybox.py` |

## assets/ — textures and props
| Script | Does |
|---|---|
| `lockdown_fetch.py` | Downloads the CC0 texture sets, decals and props at the resolution used, into `Assets/Lockdown/ThirdParty/` |
| `lockdown_props.py` | Simplifies CC0 prop scans to a vertex budget (LOD0 + silhouette-keeping LOD1) for `Lockdown/Build/Props` |
| `lockdown_noise.py` | Tileable fBm noise textures for the shaders |
| `lockdown_decals.py` | Procedural weathering decals (chipped corners, hairline cracks) |
| `lockdown_kalderimi_texture.py`, `lockdown_kalderimi_palette.py` | Stone-path PBR texture set and its colour palette |

## review/ — level-design and art QA
| Script | Does |
|---|---|
| `playground_checks.py` | Level-design checks on the play ground: relief, slopes, sprint times, sightlines, perch balance |
| `review2_layout.py`, `review_lib.py`, `ring_probe.py` | Layout review measurements (flag/spawn timings, spawn exposure, sightlines from flag rings) |
| `lower_lane_ways.py` | Chokepoint check: at least two separate ways through every cross-section of a lane band |
| `boundary_plan.py` | Proposes a natural map boundary from the reachable area |
| `greybox_diff.py` | Which greybox blocks a data change added, removed or moved |
| `symmetry.py` | Scores straight-on wall captures for repeated or mirrored stains |
| `luminance.py`, `flicker_report.py` | Lighting brightness ratios; flicker (z-fighting) detection in jittered capture sequences |
| `compare.py`, `pair.py`, `before_after.py` | Side-by-side and before/after review sheets |

The physics-based checks (containment, roadblock seal, walk test) run inside Unity: `Lockdown/Review/*`.
