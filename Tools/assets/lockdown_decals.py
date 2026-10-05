"""Procedural weathering decals for the hand-built imperfections: chipped plaster corners and hairline cracks.

    uv run --with numpy --with pillow Tools/assets/lockdown_decals.py

Chip: the rough base under the whitewash (Poly Haven concrete_wall_009, CC0, a joint-free scan, warmed) cut by a ragged noise blob, with a thin
bright lip of broken plaster and inner shading for depth (no normal map: chips project diagonally onto corners). 4 variants.
Crack: branching random-walk hairlines from the bottom-left corner of each cell (the builder mirrors them per
opening corner). 4 variants in a row.
Damp / Grime / Streak / PeelAtlas: 4-variant atlases cut from the CC0 leak and peel scans with soft, ragged ends,
so the house generator can give every stain its own shape (no tiling bands, no repeated stains).
"""
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "Assets/Lockdown/Textures/Decals"
STONE = ROOT / "Assets/Lockdown/ThirdParty/PolyHaven/Textures/concrete_wall_009/concrete_wall_009_diff_2k.jpg"
CELL = 256


def value_noise(size, cells, rng):
    grid = rng.random((cells + 1, cells + 1))
    t = np.linspace(0, cells, size, endpoint=False)
    i = np.floor(t).astype(int)
    f = t - i
    f = f * f * (3 - 2 * f)
    a, b = grid[np.ix_(i, i)], grid[np.ix_(i, i + 1)]
    c, d = grid[np.ix_(i + 1, i)], grid[np.ix_(i + 1, i + 1)]
    fy, fx = f[:, None], f[None, :]
    return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy


def chip_cell(rng, stone):
    """A jagged piece of plaster broken off: polygon outline with a random radius per angle, longer than wide,
    grey stone inside shaded darker toward the broken edge (depth), a bright 2-3 px lip of broken plaster."""
    yy, xx = (np.mgrid[0:CELL, 0:CELL] + 0.5) / CELL - 0.5
    ang = np.arctan2(yy, xx)
    k = 9 + rng.integers(0, 5)                                # number of break points around the outline
    radii = rng.uniform(0.55, 1.0, k)
    t = (ang + np.pi) / (2 * np.pi) * k
    i0 = np.floor(t).astype(int) % k
    f = t - np.floor(t)
    rad = radii[i0] * (1 - f) + radii[(i0 + 1) % k] * f      # straight-ish segments between break points
    rx, ry = rng.uniform(0.13, 0.2), rng.uniform(0.25, 0.4)
    d = np.sqrt((xx / rx) ** 2 + (yy / ry) ** 2) / rad
    d = d + (value_noise(CELL, 24, rng) - 0.5) * 0.12          # small ragged teeth
    hole = np.clip((1.0 - d) / 0.03, 0, 1)
    lip = np.clip((1.07 - d) / 0.03, 0, 1) - hole
    x0, y0 = rng.integers(0, stone.shape[1] - CELL), rng.integers(0, stone.shape[0] - CELL)
    st = stone[y0:y0 + CELL, x0:x0 + CELL]
    st = st * 0.45 + st.mean(axis=2, keepdims=True) * 0.55     # mostly grey: limestone rubble and mortar
    depth = np.clip(d, 0, 1) ** 3                               # darker toward the broken edge
    rgb = st * np.array([0.86, 0.83, 0.77])[None, None, :] * (1.0 - 0.4 * depth)[..., None]
    rgb = rgb * hole[..., None] + np.array([226, 222, 212]) * lip[..., None]
    alpha = np.clip(hole + lip, 0, 1)
    return rgb, alpha


def crack_cell(rng):
    big = CELL * 4                                           # drawn large, then filtered down: soft hairline
    im = Image.new("L", (big, big), 0)
    dr = ImageDraw.Draw(im)

    def walk(x, y, ang, length, width):
        pts = [(x, y)]
        for _ in range(int(length / 6)):
            ang += rng.normal(0, 0.28)
            ang = 0.85 * ang + 0.15 * (-np.pi / 4)           # keep heading up-right (image y runs down)
            x += np.cos(ang) * 6
            y += np.sin(ang) * 6
            pts.append((x, y))
            if rng.random() < 0.012 and width > 3:
                walk(x, y, ang + rng.choice([-1, 1]) * rng.uniform(0.5, 0.9), length * rng.uniform(0.2, 0.4), width - 2)
        dr.line(pts, fill=255, width=width)

    walk(4, big - 4, -np.pi / 4, rng.uniform(0.75, 1.1) * big, 7)
    a = np.asarray(im.filter(ImageFilter.GaussianBlur(1.5)).resize((CELL, CELL), Image.LANCZOS)).astype(np.float32) / 255.0
    a = np.clip(a * 1.8, 0, 1) * 0.95
    rgb = np.ones((CELL, CELL, 3)) * np.array([48, 46, 43])
    return rgb, a


AMB = ROOT / "Assets/Lockdown/ThirdParty/AmbientCG/Decals"


def smooth(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


def noise1d(n, cells, rng):
    return value_noise(n, cells, rng)[0]


def load_rgba(path):
    return np.asarray(Image.open(path).convert("RGBA")).astype(np.float32)


def ragged_sides(h, w, side, rng):
    """Alpha falloff toward both ends of a cell; the falloff line wanders per row so no straight vertical edge shows."""
    x = (np.arange(w) + 0.5) / w
    wob = (noise1d(h, 3, rng) - 0.5) * side * 0.5
    d = np.minimum(x[None, :], 1 - x[None, :]) + wob[:, None]
    return smooth(0.0, side, d)


def ragged_length(h, w, lo, hi, soft, rng, cells=10):
    """Fades each column out at its own length (0..1 from the top), like drips that ran different distances."""
    y = (np.arange(h) + 0.5) / h
    L = lo + (hi - lo) * noise1d(w, cells, rng)
    return smooth(0.0, soft, L[None, :] - y[:, None])


def atlas(cells, out, width, height):
    cells = [Image.fromarray(c.clip(0, 255).astype(np.uint8), "RGBA").resize((width, height), Image.LANCZOS) for c in cells]
    sheet = Image.new("RGBA", (width * 4, height), (0, 0, 0, 0))
    for i, c in enumerate(cells):
        sheet.paste(c, (i * width, 0))
    sheet.save(OUT / out)


def wrap_crop(img, x0, w):
    return img[:, (np.arange(w) + x0) % img.shape[1]]


def weathering_atlases(rng):
    """4-variant atlases cut from the CC0 leak/peel scans, every cell faded out on all open sides, so a decal is
    one self-contained stain shown once (never a tiling band with hard ends)."""
    # Rising damp (AmbientCG Leaking009, 2048x194 band): 4 different stretches, soft ragged ends.
    src = load_rgba(AMB / "Leaking009/leaking009_diff.png")
    cells = []
    for _ in range(4):
        c = wrap_crop(src, rng.integers(0, src.shape[1]), 1024).copy()
        if rng.random() < 0.5: c = c[:, ::-1]
        c[..., 3] *= ragged_sides(*c.shape[:2], 0.28, rng)
        cells.append(c)
    atlas(cells, "T_Decal_Damp_diff.png", 512, 194)
    # Grime under roof edges (AmbientCG Leaking012A, a tileable square): half-width crops, ragged ends, and
    # every drip column stops at its own length.
    src = load_rgba(AMB / "Leaking012A/leaking012a_diff.png")
    cells = []
    for _ in range(4):
        c = wrap_crop(src, rng.integers(0, src.shape[1]), 512).copy()
        c = np.roll(c, rng.integers(0, c.shape[0]), axis=0)
        h, w = c.shape[:2]
        c[..., 3] *= ragged_sides(h, w, 0.25, rng) * ragged_length(h, w, 0.35, 0.95, 0.25, rng, 14)
        c[..., 3] *= smooth(0.0, 0.04, (np.arange(h) + 0.5)[:, None] / h)  # no hard line at the top
        cells.append(c)
    atlas(cells, "T_Decal_Grime_diff.png", 512, 1024)
    # Dirt washed off a window sill: narrow strips of the same grey drips (Leaking012A) over a faint dirty wash,
    # widest under the sill and thinning out downward; every variant ends at its own length.
    cells = []
    for k in range(4):
        c = wrap_crop(src, rng.integers(0, src.shape[1]), 256).copy()
        c = np.roll(c, rng.integers(0, c.shape[0]), axis=0)
        h, w = c.shape[:2]
        y = (np.arange(h) + 0.5)[:, None] / h
        x = (np.arange(w) + 0.5)[None, :] / w
        wob = (noise1d(h, 7, rng)[:, None] - 0.5) * 0.16
        width = 0.44 - 0.28 * y + wob                          # the run narrows as it goes down, edge wanders
        core = smooth(0.0, 0.2, width - np.abs(x - 0.5 - wob * 0.5))
        wash = 0.13 * core * smooth(0.0, 0.5, rng.uniform(0.6, 0.95) - y)
        drip = c[..., 3] * core * ragged_length(h, w, 0.4, 1.0, 0.2, rng, 8)
        c[..., :3] = np.where(drip[..., None] > 25, c[..., :3], 72)   # the wash is a flat dull grey
        c[..., 3] = np.maximum(drip, wash * 255)
        c[..., 3] *= smooth(0.0, 0.03, y)
        cells.append(c)
    atlas(cells, "T_Decal_Streak_diff.png", 256, 1024)
    # Peeling whitewash band (derived from white_rough_plaster by lockdown_fetch.py): 4 stretches, soft ends.
    src = load_rgba(OUT / "T_Decal_Peel_diff.png")
    cells = []
    for _ in range(4):
        c = wrap_crop(src, rng.integers(0, src.shape[1]), 512).copy()
        if rng.random() < 0.5: c = c[:, ::-1]
        c[..., 3] *= ragged_sides(*c.shape[:2], 0.3, rng)
        cells.append(c)
    atlas(cells, "T_Decal_PeelAtlas_diff.png", 512, 512)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    weathering_atlases(np.random.default_rng(2207))
    stone = np.asarray(Image.open(STONE).convert("RGB").resize((1024, 1024))).astype(np.float32)
    rng = np.random.default_rng(1971)
    chip_rgb, chip_a = zip(*(chip_cell(rng, stone) for _ in range(4)))
    rgba = np.dstack([np.hstack(chip_rgb), np.hstack(chip_a) * 255]).clip(0, 255).astype(np.uint8)
    Image.fromarray(rgba, "RGBA").save(OUT / "T_Decal_Chip_diff.png")
    crack_rgb, crack_a = zip(*(crack_cell(rng) for _ in range(4)))
    rgba = np.dstack([np.hstack(crack_rgb), np.hstack(crack_a) * 255]).clip(0, 255).astype(np.uint8)
    Image.fromarray(rgba, "RGBA").save(OUT / "T_Decal_Crack_diff.png")
    print("wrote chip and crack atlases to", OUT)


if __name__ == "__main__":
    main()
