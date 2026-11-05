"""Real ground for LOCKDOWN: Copernicus GLO-30 DEM -> local metric heightmaps for Unity.

Run from the project root:
    uv run --with rasterio --with numpy Tools/terrain/lockdown_dem.py

Downloads the four GLO-30 tiles around the village into SourceData/dem/ (public AWS bucket, no account),
reprojects them to a local transverse Mercator centred on the village (true north, true metres)
and writes two 16-bit RAW heightmaps plus dem_meta.json for Lockdown/Build/Real Ground:
  - village_513.r16   2031.25 m square, 513 samples, centred on the origin
  - backdrop_1025.r16 40 km square, 1025 samples; the village tile fills a hole in its centre
Rows are written south to north, columns west to east, values scaled to [base, top] metres.
"""
import json
import math
import urllib.request
from pathlib import Path

import numpy as np
import rasterio
from rasterio.crs import CRS
from rasterio.merge import merge
from rasterio.transform import from_origin
from rasterio.warp import Resampling, reproject

ORIGIN_LAT, ORIGIN_LON = 35.1503, 24.8477
LOCAL_CRS = CRS.from_proj4(
    f"+proj=tmerc +lat_0={ORIGIN_LAT} +lon_0={ORIGIN_LON} +k=1 +x_0=0 +y_0=0 +ellps=WGS84 +units=m +no_defs")

BACKDROP_SIZE, BACKDROP_RES = 40000.0, 1025
BACKDROP_CELL = BACKDROP_SIZE / (BACKDROP_RES - 1)            # 39.0625 m
VILLAGE_CELLS = 52                                             # village edge = whole backdrop cells
VILLAGE_SIZE, VILLAGE_RES = VILLAGE_CELLS * BACKDROP_CELL, 513  # 2031.25 m
SEAM_BAND = 100.0                                              # village edge blends into the backdrop
EYE_HEIGHT = 1.6

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "SourceData" / "dem"
TILES = [f"Copernicus_DSM_COG_10_{lat}_00_{lon}_00_DEM"
         for lat in ("N34", "N35") for lon in ("E024", "E025")]
BUCKET = "https://copernicus-dem-30m.s3.amazonaws.com"


def download():
    OUT.mkdir(parents=True, exist_ok=True)
    paths = []
    for name in TILES:
        path = OUT / f"{name}.tif"
        if not path.exists():
            url = f"{BUCKET}/{name}/{name}.tif"
            print(f"downloading {name} ...")
            try:
                urllib.request.urlretrieve(url, path)
            except Exception as e:  # sea-only tiles do not exist in the bucket
                print(f"  skipped ({e})")
                continue
        paths.append(path)
    return paths


def resample(mosaic, mosaic_transform, src_crs, size, res):
    """Sample the mosaic on a res x res grid of nodes spanning [-size/2, size/2] (rows north to south)."""
    cell = size / (res - 1)
    half = size / 2
    dst_transform = from_origin(-half - cell / 2, half + cell / 2, cell, cell)
    dst = np.zeros((res, res), dtype=np.float32)
    reproject(mosaic, dst, src_transform=mosaic_transform, src_crs=src_crs,
              dst_transform=dst_transform, dst_crs=LOCAL_CRS,
              resampling=Resampling.cubic, src_nodata=None, dst_nodata=0)
    return dst[::-1].copy()  # south to north


def bilinear(grid, size, x, z):
    """Height of a south-to-north node grid spanning [-size/2, size/2] at local (x, z)."""
    res = grid.shape[0]
    fx = np.clip((x + size / 2) / size * (res - 1), 0, res - 1.000001)
    fz = np.clip((z + size / 2) / size * (res - 1), 0, res - 1.000001)
    ix, iz = np.floor(fx).astype(int), np.floor(fz).astype(int)
    tx, tz = fx - ix, fz - iz
    h00, h10 = grid[iz, ix], grid[iz, ix + 1]
    h01, h11 = grid[iz + 1, ix], grid[iz + 1, ix + 1]
    return (h00 * (1 - tx) + h10 * tx) * (1 - tz) + (h01 * (1 - tx) + h11 * tx) * tz


def blend_seam(village, backdrop):
    """Fade the village edge into the backdrop's bilinear surface so the two terrains meet exactly."""
    coords = np.linspace(-VILLAGE_SIZE / 2, VILLAGE_SIZE / 2, VILLAGE_RES)
    xx, zz = np.meshgrid(coords, coords)
    base = bilinear(backdrop, BACKDROP_SIZE, xx, zz)
    edge = VILLAGE_SIZE / 2 - np.maximum(np.abs(xx), np.abs(zz))
    t = np.clip(edge / SEAM_BAND, 0, 1)
    w = t * t * (3 - 2 * t)
    return base * (1 - w) + village * w


def angle_to(h_eye, h_target, dist):
    drop = 0.87 * dist * dist / (2 * 6371000.0)  # earth curvature with standard refraction
    return math.degrees(math.atan2(h_target - drop - h_eye, dist))


def horizon(backdrop, x0, z0, h_eye, az_deg, max_dist=20000.0, step=30.0):
    a = math.radians(az_deg)
    d = np.arange(step, max_dist, step)
    xs, zs = x0 + d * math.sin(a), z0 + d * math.cos(a)
    hs = bilinear(backdrop, BACKDROP_SIZE, xs, zs)
    drop = 0.87 * d * d / (2 * 6371000.0)
    angles = np.degrees(np.arctan2(hs - drop - h_eye, d))
    i = int(np.argmax(angles))
    return float(angles[i]), float(d[i]), float(hs[i])


def main():
    paths = download()
    srcs = [rasterio.open(p) for p in paths]
    src_crs = srcs[0].crs
    mosaic, mosaic_transform = merge(srcs)
    mosaic = mosaic[0].astype(np.float32)
    for s in srcs:
        s.close()

    backdrop = resample(mosaic, mosaic_transform, src_crs, BACKDROP_SIZE, BACKDROP_RES)
    village = resample(mosaic, mosaic_transform, src_crs, VILLAGE_SIZE, VILLAGE_RES)
    village = blend_seam(village, backdrop)

    base = 0.0
    top = math.ceil(max(backdrop.max(), village.max()) / 10.0) * 10.0 + 10.0
    for name, grid in (("village_513.r16", village), ("backdrop_1025.r16", backdrop)):
        q = np.clip((grid - base) / (top - base), 0, 1) * 65535.0
        q.round().astype("<u2").tofile(OUT / name)

    origin_h = float(bilinear(village, VILLAGE_SIZE, 0.0, 0.0))
    meta = {
        "origin_lat": ORIGIN_LAT, "origin_lon": ORIGIN_LON,
        "crs": LOCAL_CRS.to_proj4(),
        "base_height": base, "top_height": top, "origin_height": round(origin_h, 2),
        "village": {"file": "village_513.r16", "size": VILLAGE_SIZE, "resolution": VILLAGE_RES},
        "backdrop": {"file": "backdrop_1025.r16", "size": BACKDROP_SIZE, "resolution": BACKDROP_RES,
                     "hole_first_cell": int((BACKDROP_RES - 1) / 2 - VILLAGE_CELLS / 2),
                     "hole_cells": VILLAGE_CELLS},
        "seam_band": SEAM_BAND,
    }
    (OUT / "dem_meta.json").write_text(json.dumps(meta, indent=2))

    # ---- Checks ----
    print(f"\nheight range {base:.0f}..{top:.0f} m; backdrop min {backdrop.min():.0f} max {backdrop.max():.0f}")
    print(f"origin (35.1503 N, 24.8477 E): {origin_h:.0f} m")
    vz = np.linspace(-VILLAGE_SIZE / 2, VILLAGE_SIZE / 2, VILLAGE_RES)
    print(f"village tile: min {village.min():.0f} max {village.max():.0f}; "
          f"N-S profile at x=0: z=-300 {bilinear(village, VILLAGE_SIZE, 0, -300):.0f}, "
          f"z=0 {origin_h:.0f}, z=+300 {bilinear(village, VILLAGE_SIZE, 0, 300):.0f}")

    # Psiloritis: highest point of the backdrop's northern half.
    north = backdrop.copy()
    north[: BACKDROP_RES // 2] = -1
    iz, ix = np.unravel_index(np.argmax(north), north.shape)
    px = -BACKDROP_SIZE / 2 + ix * BACKDROP_CELL
    pz = -BACKDROP_SIZE / 2 + iz * BACKDROP_CELL
    dist = math.hypot(px, pz)
    bearing = (math.degrees(math.atan2(px, pz)) + 360) % 360
    print(f"Psiloritis summit: {backdrop[iz, ix]:.0f} m, {dist / 1000:.1f} km, bearing {bearing:.0f} deg, "
          f"{angle_to(origin_h + EYE_HEIGHT, backdrop[iz, ix], dist):.1f} deg above the eye")

    for d in (4000, 8000, 12000):
        print(f"south {d / 1000:.0f} km: {bilinear(backdrop, BACKDROP_SIZE, 0, -d):.0f} m")

    print("\nwestern horizon (sun target 18-22 deg, az 260-266):")
    worst = -90.0
    for x0 in (-75.0, 0.0, 75.0):
        h_eye = float(bilinear(village, VILLAGE_SIZE, x0, 0.0)) + EYE_HEIGHT
        for az in range(250, 281, 5):
            ang, d, h = horizon(backdrop, x0, 0.0, h_eye, az)
            worst = max(worst, ang) if 255 <= az <= 270 else worst
            print(f"  from x={x0:+.0f}: az {az}: {ang:5.1f} deg  (ridge {h:.0f} m at {d / 1000:.1f} km)")
    print(f"highest western horizon az 255-270: {worst:.1f} deg -> "
          f"{'sun at 18 deg clears it' if worst < 18 else 'SUN HIDDEN at 18 deg'}")
    print(f"\nwrote {OUT}")


if __name__ == "__main__":
    main()
