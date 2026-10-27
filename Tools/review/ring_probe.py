"""Lines from rings around every flag to the 12 start points, reachable ring points only (review 5).

    uv run --with numpy --with scipy --with pillow Tools/review/ring_probe.py

The review2_layout.py 'extras' section samples 24 points per ring without checking that they can be walked on, so
rings that cross the rock face past the frame report lines nobody can stand on. This probe uses reachable cells only
(1 m grid, eye 1.7 m) and prints each hit with the start it sees and the distance.
"""
import math
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from review_lib import load  # noqa: E402

g = load()
M, XX, ZZ, reach, to_frame, los = g["META"], g["XX"], g["ZZ"], g["reach"], g["to_frame"], g["los"]
st = {s["name"]: s for s in M["stakes"]}
starts = [(s["name"], (s["x"], s["z"])) for s in M["stakes"] if s["kind"].startswith("start_")]
for f in ("Flag_A", "Flag_B", "Flag_C"):
    p = (st[f]["x"], st[f]["z"])
    for rad in (5, 7, 8, 10, 15):
        d = np.hypot(XX - p[0], ZZ - p[1])
        idx = np.argwhere(reach & (np.abs(d - rad) <= 0.5))[::2]
        hits = []
        for r, c in idx:
            q = (XX[r, c], ZZ[r, c])
            for nm, s in starts:
                if los(q, s, 1.7, 1.7):
                    hits.append((tuple(round(float(v)) for v in to_frame(*q)), nm, round(math.hypot(q[0] - s[0], q[1] - s[1]))))
        print(f"{f} ring {rad:2d} m: {len(idx):3d} reachable points, {len(hits)} lines to a start", hits[:4])
