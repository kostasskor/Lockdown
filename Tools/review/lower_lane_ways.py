"""Lower Lane ways (Task 10): how many separate through-ways cross each north-south cut of the lower band.

Run from the project root, after Lockdown/Review/Containment Test (it reads the walker's physics graph):
    uv run --with numpy --with scipy Tools/review/lower_lane_ways.py [build|editor]

The lower band is everything walkable south of v = -22 (below the kafeneio and the Skeleton yard) inside the frame.
Only two-way moves count (a drop you cannot climb back is no way). For every cut u = c (1 m apart) a way is a gate
of moves crossing the cut whose west end is still connected to the band's west end (u <= -50) without crossing the
cut, and whose east end to the band's east end (u >= 50): a pocket that straddles the cut but leads nowhere does not
count. Gates are split where the crossing points are more than GAP m apart (along v or in height). Target (Task 10,
"routes, no single chokepoint"): at every cut at least 2 ways, or one open way at least WIDE m across (an open
garden is no chokepoint). Width = the gate's span along the cut plus one grid step. Nothing is written.
"""
import json
import math
import sys
from pathlib import Path

import numpy as np
from scipy.sparse import csr_matrix
from scipy.sparse.csgraph import connected_components

ROOT = Path(__file__).resolve().parents[2]
PG = ROOT / "SourceData" / "playground"
META = json.loads((PG / "play_meta.json").read_text())
CX, CZ = META["frame_center"]
ROT = math.radians(META["frame_rotation_deg"])
COS, SIN = math.cos(ROT), math.sin(ROT)
HALF_U, HALF_V = META["frame_size"][0] / 2, META["frame_size"][1] / 2
B = META["flag_b_height"]

BAND_TOP, ANCHOR, GAP, WIDE = -22.0, 50.0, 1.0, 8.0
world = sys.argv[1] if len(sys.argv) > 1 else "build"

hdr = json.loads((PG / f"reach_physics_{world}.json").read_text())
N, E = hdr["nodes"], hdr["edges"]
buf = (PG / f"reach_physics_{world}.bin").read_bytes()
o = 0
pos = np.frombuffer(buf, "<f4", 3 * N, o).reshape(N, 3); o += 12 * N
o += 4 * N  # grid cell per node
low = np.frombuffer(buf, "u1", N, o); o += N
indptr = np.frombuffer(buf, "<i4", N + 1, o); o += 4 * (N + 1)
to = np.frombuffer(buf, "<i4", E, o)
frm = np.repeat(np.arange(N), np.diff(indptr))
keep = np.ones(E, bool) if world == "build" else (low[to] & 2) == 0   # editor: placeholder tops are no routes
A = csr_matrix((np.ones(int(keep.sum())), (frm[keep], to[keep])), shape=(N, N))
U2 = A.multiply(A.T).tocoo()   # two-way moves only
i2, j2 = U2.row, U2.col
m = i2 < j2
i2, j2 = i2[m], j2[m]

dx, dz = pos[:, 0] - CX, pos[:, 2] - CZ
uu, vv = dx * COS + dz * SIN, -dx * SIN + dz * COS
band = (vv <= BAND_TOP) & (np.abs(uu) <= HALF_U) & (vv >= -HALF_V)


def reached(side_mask, anchor_mask):
    sel = band & side_mask
    e = sel[i2] & sel[j2]
    G = csr_matrix((np.ones(int(e.sum())), (i2[e], j2[e])), shape=(N, N))
    _, lab = connected_components(G, directed=False)
    good = np.unique(lab[sel & anchor_mask])
    return sel & np.isin(lab, good)


print(f"Lower Lane ways ({world} world, physics graph '{hdr['label']}' {hdr['time']}; band v <= {BAND_TOP}, "
      f"anchors |u| >= {ANCHOR}, gates split at {GAP} m)")
rows, worst = [], []
for c in np.arange(-ANCHOR + 2, ANCHOR - 1, 1.0):
    west = reached(uu < c, uu <= -ANCHOR)
    east = reached(uu >= c, uu >= ANCHOR)
    a, b = i2, j2
    cross = ((west[a] & east[b]) | (west[b] & east[a]))
    if not cross.any():
        rows.append((c, 0, []))
        continue
    ia, ib = a[cross], b[cross]
    # crossing point on the cut: v and height, interpolated along the move
    t = np.clip((c - uu[ia]) / np.where(np.abs(uu[ib] - uu[ia]) < 1e-6, 1e-6, uu[ib] - uu[ia]), 0, 1)
    cv = vv[ia] + t * (vv[ib] - vv[ia])
    cy = pos[ia, 1] + t * (pos[ib, 1] - pos[ia, 1]) - B
    order = np.lexsort((cv, np.round(cy / 2.0)))
    gates = []
    for k in order:
        if gates and abs(cv[k] - gates[-1][1]) <= GAP and abs(cy[k] - gates[-1][3]) <= 2.0:
            g = gates[-1]
            gates[-1] = (min(g[0], cv[k]), max(g[1], cv[k]), g[2], cy[k])
        else:
            gates.append((cv[k], cv[k], cy[k], cy[k]))
    rows.append((c, len(gates), gates))
STEP = hdr["step"]
for c, n, gates in rows:
    width = sum(g[1] - g[0] + STEP for g in gates)
    good = n >= 2 or width >= WIDE
    flag = "" if good else "   <-- CHOKEPOINT" if n == 1 else "   <-- NO WAY"
    txt = "  ".join(f"[v {g[0]:.0f}..{g[1]:.0f} @{g[2]:+.1f}]" for g in gates)
    print(f"  u {c:+5.0f}: {n} way(s), {width:4.1f} m {txt}{flag}")
    if not good:
        worst.append((c, width))
ok = not worst
print(f"RESULT {'PASS' if ok else 'FAIL'}: min {min(r[1] for r in rows)} way(s); chokepoint cuts (1 way under {WIDE:.0f} m): "
      + (", ".join(f"{c:+.0f} ({w:.1f} m)" for c, w in worst) if worst else "none"))
