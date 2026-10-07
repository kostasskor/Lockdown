"""Reference board imagery for LOCKDOWN (Task 3b): a georeferenced aerial photo on the local metric grid.

Run from the project root, after lockdown_dem.py:
    uv run --with rasterio --with numpy --with pillow Tools/terrain/lockdown_satellite.py

Fetches Esri World Imagery for a 1.2 km square around the village origin, reprojects it to the same
local transverse Mercator as the terrain (true north, true metres) and writes 2x2 JPG tiles of 1024 px
(about 0.6 m/px; the source is about 0.8 m/px) to Assets/Lockdown/Textures/Review/Satellite/. A second set has 10 m contour lines
from the DEM drawn on it. Review-only: the folder is gitignored and can be regenerated.
Tile names: sat_<col>_<row>.jpg, col 0 = west, row 0 = south.
"""
import io
import json
import urllib.parse
import urllib.request
from pathlib import Path

import numpy as np
import rasterio
from PIL import Image
from rasterio.crs import CRS
from rasterio.transform import from_bounds, from_origin
from rasterio.warp import Resampling, reproject, transform_bounds

ROOT = Path(__file__).resolve().parents[2]
DEM = ROOT / "SourceData" / "dem"
OUT = ROOT / "Assets" / "Lockdown" / "Textures" / "Review" / "Satellite"
SIZE = 1200.0            # metres, square around the origin
FETCH_CHUNKS, CHUNK_PX = 2, 832   # Esri serves nothing finer than ~1 m/px (mercator) here; larger exports fail with HTTP 500
TILE_PX = 1024
CONTOUR_STEP = 10.0
URL = ("https://services.arcgisonline.com/arcgis/rest/services/World_Imagery/MapServer/export"
       "?bbox={w},{s},{e},{n}&bboxSR=3857&imageSR=3857&size={px},{px}&format=jpg&f=image")


def main():
    meta = json.loads((DEM / "dem_meta.json").read_text())
    local = CRS.from_proj4(meta["crs"])
    half = SIZE / 2

    # Area to fetch in web mercator (a little larger than the square so reprojection has no empty corners).
    w, s, e, n = transform_bounds(local, CRS.from_epsg(3857), -half * 1.1, -half * 1.1, half * 1.1, half * 1.1)
    print("fetching Esri World Imagery ...")
    chunks, chunk = FETCH_CHUNKS, CHUNK_PX
    FETCH_PX = chunks * chunk
    img = np.zeros((FETCH_PX, FETCH_PX, 3), dtype=np.uint8)
    for cx in range(chunks):
        for cy in range(chunks):  # cy 0 = north
            cw, ce = w + (e - w) * cx / chunks, w + (e - w) * (cx + 1) / chunks
            cn, cs = n - (n - s) * cy / chunks, n - (n - s) * (cy + 1) / chunks
            url = URL.format(w=cw, s=cs, e=ce, n=cn, px=chunk)
            req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
            data = urllib.request.urlopen(req, timeout=120).read()
            part = np.asarray(Image.open(io.BytesIO(data)).convert("RGB"))
            img[cy * chunk:(cy + 1) * chunk, cx * chunk:(cx + 1) * chunk] = part
    print(f"got {img.shape[1]}x{img.shape[0]} px, mercator bounds {w:.0f},{s:.0f} .. {e:.0f},{n:.0f}")

    src_transform = from_bounds(w, s, e, n, img.shape[1], img.shape[0])
    total = 2 * TILE_PX  # 2048 px over 1200 m
    dst_transform = from_origin(-half, half, SIZE / total, SIZE / total)
    out = np.zeros((3, total, total), dtype=np.uint8)
    for band in range(3):
        reproject(img[:, :, band], out[band], src_transform=src_transform, src_crs=CRS.from_epsg(3857),
                  dst_transform=dst_transform, dst_crs=local, resampling=Resampling.cubic)
    rgb = np.moveaxis(out, 0, -1)  # rows north to south

    # Contours from the village DEM.
    res = meta["village"]["resolution"]
    vsize = meta["village"]["size"]
    raw = np.fromfile(DEM / meta["village"]["file"], dtype="<u2").reshape(res, res).astype(np.float32)
    heights = meta["base_height"] + raw / 65535.0 * (meta["top_height"] - meta["base_height"])  # south to north
    xs = (np.arange(total) + 0.5) / total * SIZE - half                  # west to east
    fx = np.clip((xs + vsize / 2) / vsize * (res - 1), 0, res - 1.001)
    zs = half - (np.arange(total) + 0.5) / total * SIZE                  # north to south
    fz = np.clip((zs + vsize / 2) / vsize * (res - 1), 0, res - 1.001)
    ix, iz = np.floor(fx).astype(int), np.floor(fz).astype(int)
    tx, tz = fx - ix, (fz - iz)[:, None]
    ix2, iz2 = ix[None, :], iz[:, None]
    h = ((heights[iz2, ix2] * (1 - tx) + heights[iz2, ix2 + 1] * tx) * (1 - tz)
         + (heights[iz2 + 1, ix2] * (1 - tx) + heights[iz2 + 1, ix2 + 1] * tx) * tz)
    band = np.floor(h / CONTOUR_STEP)
    edge = np.zeros(h.shape, dtype=bool)
    edge[:-1, :] |= band[:-1, :] != band[1:, :]
    edge[:, :-1] |= band[:, :-1] != band[:, 1:]
    major = (np.floor(h / (CONTOUR_STEP * 5)) != np.roll(np.floor(h / (CONTOUR_STEP * 5)), -1, axis=0)) | \
            (np.floor(h / (CONTOUR_STEP * 5)) != np.roll(np.floor(h / (CONTOUR_STEP * 5)), -1, axis=1))
    contour = rgb.copy()
    contour[edge] = (0.45 * contour[edge] + 0.55 * np.array([255, 235, 0])).astype(np.uint8)
    contour[edge & major] = np.array([255, 40, 40], dtype=np.uint8)

    OUT.mkdir(parents=True, exist_ok=True)
    for name, image in (("sat", rgb), ("sat_contours", contour)):
        for col in range(2):
            for row in range(2):  # row 0 = south
                y0 = (1 - row) * TILE_PX
                tile = image[y0:y0 + TILE_PX, col * TILE_PX:(col + 1) * TILE_PX]
                Image.fromarray(tile).save(OUT / f"{name}_{col}_{row}.jpg", quality=90)
    (OUT / "satellite_meta.json").write_text(json.dumps({"size": SIZE, "tile_px": TILE_PX, "m_per_px": SIZE / total}))
    print(f"wrote 8 tiles to {OUT}")


if __name__ == "__main__":
    main()
