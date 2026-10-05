"""Generates the tileable noise textures used by the Lockdown shaders (procedural fBm value noise).

    uv run --with numpy --with pillow Tools/assets/lockdown_noise.py
"""
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "Assets/Lockdown/Textures/Noise"


def tileable_value_noise(size, cells, rng):
    """Smooth value noise on a periodic grid of `cells` x `cells`, sampled at size x size."""
    grid = rng.random((cells, cells))
    t = np.arange(size) / size * cells
    i0 = np.floor(t).astype(int) % cells
    i1 = (i0 + 1) % cells
    f = t - np.floor(t)
    f = f * f * (3 - 2 * f)  # smoothstep
    a = grid[np.ix_(i0, i0)]
    b = grid[np.ix_(i0, i1)]
    c = grid[np.ix_(i1, i0)]
    d = grid[np.ix_(i1, i1)]
    fy, fx = f[:, None], f[None, :]
    return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy


def fbm(size, base_cells, octaves, seed):
    rng = np.random.default_rng(seed)
    total = np.zeros((size, size))
    amp, norm = 1.0, 0.0
    for o in range(octaves):
        total += amp * tileable_value_noise(size, base_cells * 2 ** o, rng)
        norm += amp
        amp *= 0.5
    total /= norm
    total = (total - total.min()) / (total.max() - total.min())
    return total


def height_to_normal(h, strength):
    """Tileable OpenGL-style normal map (green up) from a 0..1 height field, by wrapped central differences."""
    dx = (np.roll(h, -1, axis=1) - np.roll(h, 1, axis=1)) * 0.5 * strength
    dy = (np.roll(h, -1, axis=0) - np.roll(h, 1, axis=0)) * 0.5 * strength
    n = np.stack([-dx, dy, np.ones_like(h)], axis=-1)  # image rows run down, texture v runs up
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return ((n * 0.5 + 0.5) * 255).round().astype(np.uint8)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    n = fbm(512, 4, 6, seed=1943)
    Image.fromarray((n * 255).astype(np.uint8), "L").save(OUT / "T_Noise_Plaster_mask.png")
    print("wrote", OUT / "T_Noise_Plaster_mask.png")

    # Hand-built imperfections. R: repaired-patch mask (blobs ~1/4 of the tile), G: fine speckle for chipped
    # paint, B: unused. The macro normal map is the soft unevenness of hand-applied plaster.
    patches = fbm(512, 4, 5, seed=7)
    speckle = fbm(512, 32, 3, seed=23)
    rgba = np.stack([patches, speckle, np.zeros_like(patches), np.ones_like(patches)], axis=-1)
    Image.fromarray((rgba * 255).astype(np.uint8), "RGBA").save(OUT / "T_Noise_Macro.png")
    bumps = fbm(512, 3, 5, seed=11)
    Image.fromarray(height_to_normal(bumps, 40.0), "RGB").save(OUT / "T_Noise_Macro_nor.png")
    print("wrote", OUT / "T_Noise_Macro.png", OUT / "T_Noise_Macro_nor.png")

    # Large-scale tone of a wall (no visible repetition): one tile covers 64 m, blobs 2-6 m across. Three
    # independent fields: R dirtier/darker, G greyer, B bleached or repainted. Domain-warped so the blobs are
    # irregular rather than round.
    size = 1024
    warp_x = fbm(size, 8, 3, seed=101) - 0.5
    warp_y = fbm(size, 8, 3, seed=102) - 0.5
    yy, xx = np.mgrid[0:size, 0:size]
    wx = ((xx + warp_x * size * 0.06) % size).astype(int)
    wy = ((yy + warp_y * size * 0.06) % size).astype(int)
    chans = [fbm(size, 16, 4, seed=s)[wy, wx] for s in (201, 202, 203)]
    rgb = np.stack(chans, axis=-1)
    Image.fromarray((rgb * 255).astype(np.uint8), "RGB").save(OUT / "T_Noise_MacroTone.png")
    print("wrote", OUT / "T_Noise_MacroTone.png")


if __name__ == "__main__":
    main()
