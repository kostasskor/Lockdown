"""Scores straight-on wall captures for visible repetition and mirrored halves, and builds a BEFORE | AFTER sheet.

    uv run --with numpy --with pillow Tools/review/symmetry.py BEFORE_PREFIX AFTER_PREFIX OUT

Reads Screenshots/walls/{prefix}_{wall}.png (LockdownHouseReview.CaptureWallFaces). On the high-passed luminance
(wall texture and stains, not the big shapes) it measures:
  mirror  - correlation between the left half and the mirrored right half (1 = mirror image)
  repeat  - strongest horizontal self-similarity at a shift of 0.8 m or more (1 = the texture repeats exactly)
Openings are dark, so they are masked out before scoring (they really are placed by the reference photos).
"""
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

SHOTS = Path(__file__).resolve().parents[2] / "Screenshots" / "walls"


def highpass(im):
    g = np.asarray(im.convert("L")).astype(np.float32)
    blur = np.asarray(im.convert("L").filter(ImageFilter.GaussianBlur(24))).astype(np.float32)
    mask = (blur > 70) & (g > 40)           # plaster only: skip glass, frames' shadows, sky
    h = g - blur
    # Horizontal bands (plinth edge, formwork joints, the damp line) match themselves at any sideways shift; they
    # are not a repeat, so each row's mean is removed.
    rows = np.where(mask, h, 0).sum(1) / np.maximum(mask.sum(1), 1)
    return h - rows[:, None], mask


def corr(a, b, m):
    if m.sum() < 500:
        return 0.0
    a, b = a[m] - a[m].mean(), b[m] - b[m].mean()
    return float((a * b).sum() / (np.sqrt((a * a).sum() * (b * b).sum()) + 1e-6))


def scores(path, metres):
    im = Image.open(path)
    h, m = highpass(im)
    w = h.shape[1]
    half = w // 2
    left, right = h[:, :half], h[:, w - half:][:, ::-1]
    mirror = corr(left, right, m[:, :half] & m[:, w - half:][:, ::-1])
    px_per_m = w / metres
    best = 0.0
    for shift in range(int(0.8 * px_per_m), int(w * 0.6), 4):
        best = max(best, corr(h[:, :-shift], h[:, shift:], m[:, :-shift] & m[:, shift:]))
    return mirror, best


WALLS = {  # wall name -> length in metres (from the house spec)
    "West": 8.95, "South_Left": 5.2, "South_RightGround": 3.7, "East_QuoinReturn": 0.9, "South_RightUpper": 3.4,
    "East_UnderTerrace": 0.9, "East": 5.8, "North": 8.6, "Parapet": 6.95,
    "RoofRoom_S": 2.6, "RoofRoom_W": 2.5, "RoofRoom_E": 2.5, "RoofRoom_N": 2.6,
}


def main():
    before, after, out = sys.argv[1:4]
    rows, lines = [], []
    for wall, metres in WALLS.items():
        pb, pa = SHOTS / f"{before}_{wall}.png", SHOTS / f"{after}_{wall}.png"
        if not (pb.exists() and pa.exists()) or metres < 2:
            continue
        sb, sa = scores(pb, metres), scores(pa, metres)
        lines.append(f"{wall:18s} mirror {sb[0]:+.2f} -> {sa[0]:+.2f}   repeat {sb[1]:.2f} -> {sa[1]:.2f}")
        ims = [Image.open(p).convert("RGB") for p in (pb, pa)]
        H = 360
        ims = [i.resize((int(i.width * H / i.height), H)) for i in ims]
        row = Image.new("RGB", (ims[0].width * 2 + 10, H + 22), (20, 20, 20))
        row.paste(ims[0], (0, 22)); row.paste(ims[1], (ims[0].width + 10, 22))
        d = ImageDraw.Draw(row)
        d.text((6, 5), f"BEFORE  {wall}  mirror {sb[0]:+.2f} repeat {sb[1]:.2f}", fill=(255, 230, 80))
        d.text((ims[0].width + 16, 5), f"AFTER  mirror {sa[0]:+.2f} repeat {sa[1]:.2f}", fill=(120, 255, 120))
        rows.append(row)
    W = max(r.width for r in rows)
    sheet = Image.new("RGB", (W, sum(r.height + 6 for r in rows)), (0, 0, 0))
    y = 0
    for r in rows:
        sheet.paste(r, (0, y)); y += r.height + 6
    sheet.save(SHOTS.parent / f"{out}.png")
    print("\n".join(lines))


if __name__ == "__main__":
    main()
