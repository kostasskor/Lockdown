"""Flicker report for jittered capture sequences (Lockdown.EditorTools.LockdownHouseReview.CaptureFlicker).

    uv run --with pillow --with numpy Tools/review/flicker_report.py <prefix>

For each sequence in Screenshots/flicker/<prefix>_<view>_<i>.png it writes an animated GIF and a heat map
of flickering pixels, and prints a flicker score. A pixel flickers when its brightness reverses direction
by more than THRESH at least twice across the frames: smooth camera motion changes pixels one way, while
z-fighting and decal sort swaps toggle them back and forth.
"""
import glob
import re
import sys
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
SHOTS = ROOT / "Screenshots" / "flicker"
THRESH = 10.0
BUSY = 9.0


def main(prefix):
    frame = re.compile(rf"{prefix}_(\d+)_\d+\.png$")
    views = sorted({m.group(1) for p in glob.glob(str(SHOTS / f"{prefix}_*_*.png")) if (m := frame.match(Path(p).name))})
    for v in views:
        files = sorted([p for p in glob.glob(str(SHOTS / f"{prefix}_{v}_*.png")) if frame.match(Path(p).name)], key=lambda p: int(Path(p).stem.split("_")[-1]))
        frames = [Image.open(f).convert("RGB") for f in files]
        lum = np.stack([np.asarray(f.convert("L"), dtype=np.float32) for f in frames])
        d = np.diff(lum, axis=0)
        sig = np.where(np.abs(d) > THRESH, np.sign(d), 0)
        flips = np.zeros(lum.shape[1:], dtype=np.int32)
        last = np.zeros(lum.shape[1:], dtype=np.float32)
        for s in sig:
            flips += ((s != 0) & (last != 0) & (s != last)).astype(np.int32)
            last = np.where(s != 0, s, last)
        # Ignore pixels that are busy in the first frame (texture grain, foliage, hard edges): their shimmer
        # under motion is ordinary aliasing. Z-fighting and decal sort swaps happen on flat surfaces.
        l0 = lum[0]
        pad = np.pad(l0, 1, mode="edge")
        win = np.stack([pad[y:y + l0.shape[0], x:x + l0.shape[1]] for y in range(3) for x in range(3)])
        busy = win.std(axis=0) > BUSY
        mask = (flips >= 2) & ~busy
        score = mask.mean() * 100
        base = np.asarray(frames[0], dtype=np.float32) * 0.45
        base[mask] = [255, 40, 40]
        Image.fromarray(base.astype(np.uint8)).save(SHOTS / f"{prefix}_{v}_heat.png")
        frames[0].save(SHOTS / f"{prefix}_{v}.gif", save_all=True, append_images=frames[1:], duration=120, loop=0)
        print(f"view {v}: flicker {score:.3f}% of pixels")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "before")
