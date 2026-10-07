"""Level-design checks for the LOCKDOWN play ground (Task 7, review 2, brief section 7).

Run from the project root after lockdown_playground.py:
    uv run --with numpy --with scipy --with pillow Tools/review/playground_checks.py

Works on what the generator built: SourceData/playground/play_513.r16 (ground), play_mask.png (lanes, connectors,
pads, ravine floor) and play_meta.json (placeholder blocks, stakes, walk routes). The blocks are burnt into a 2.5D
occluder raster (bridge decks become walkable ground over the ravine). Eye height 1.6 m, sprint 6.5 m/s.
Prints one table; every check states how it is measured.
"""
import heapq
import json
import os
import math
from pathlib import Path

import numpy as np
from PIL import Image
from scipy.ndimage import map_coordinates

ROOT = Path(__file__).resolve().parents[2] if '__file__' in globals() else Path.cwd()
PG = ROOT / "SourceData" / "playground"
LAYOUT = json.loads((ROOT / "Assets" / "Lockdown" / "Data" / "Layout" / "lockdown_layout.json").read_text(encoding="utf-8"))
META = json.loads((PG / "play_meta.json").read_text())

EYE, SPRINT, WALK_DEG = 1.6, 6.5, 36.0
RES, SIZE, X0, Z0 = META["resolution"], META["size"], META["x0"], META["z0"]
STEP = SIZE / (RES - 1)
CX, CZ = META["frame_center"]
ROT = math.radians(META["frame_rotation_deg"])
COS, SIN = math.cos(ROT), math.sin(ROT)
HALF_U, HALF_V = META["frame_size"][0] / 2, META["frame_size"][1] / 2
B = META["flag_b_height"]

raw = np.fromfile(PG / META["file"], dtype="<u2").reshape(RES, RES).astype(np.float64)
ground = META["base_height"] + raw / 65535.0 * (META["top_height"] - META["base_height"])
mask_img = np.asarray(Image.open(PG / META["mask"]).convert("L"), dtype=np.float64)[::-1, :] / 255.0  # rows south->north

xs = X0 + np.arange(RES) * STEP
XX, ZZ = np.meshgrid(xs, Z0 + np.arange(RES) * STEP)
UU = (XX - CX) * COS + (ZZ - CZ) * SIN
VV = -(XX - CX) * SIN + (ZZ - CZ) * COS
inside = (np.abs(UU) <= HALF_U) & (np.abs(VV) <= HALF_V)
surface_mask = map_coordinates(mask_img, [np.arange(RES)[:, None] * (mask_img.shape[0] - 1) / (RES - 1) + 0 * XX,
                                          np.arange(RES)[None, :] * (mask_img.shape[1] - 1) / (RES - 1) + 0 * XX], order=1) > 0.5


def to_world(u, v):
    return CX + u * COS - v * SIN, CZ + u * SIN + v * COS


def to_frame(x, z):
    dx, dz = x - CX, z - CZ
    return dx * COS + dz * SIN, -dx * SIN + dz * COS


def grid_index(x, z):
    return (np.asarray(z) - Z0) / STEP, (np.asarray(x) - X0) / STEP


# ---------------------------------------------------------------- occluder raster and walkable surface

occ = ground.copy()        # what blocks a line of sight
walk_h = ground.copy()     # the surface you stand on
blocked = np.zeros(ground.shape, bool)
deck = np.zeros(ground.shape, bool)
roofs = []
for b in META["boxes"]:
    y = math.radians(b["yaw"])
    fx, fz = math.sin(y), math.cos(y)
    a = (XX - b["x"]) * fx + (ZZ - b["z"]) * fz
    c = (XX - b["x"]) * fz - (ZZ - b["z"]) * fx
    fp = (np.abs(a) <= b["sz"] / 2) & (np.abs(c) <= b["sx"] / 2)
    top = b["y"] + b["sy"] / 2
    occ[fp] = np.maximum(occ[fp], top)
    if b.get("overhead"):
        continue  # an arch: blocks sight lines, walkable underneath
    if (b["group"] == "Bridges" and "deck" in b["name"]) or b.get("walk_top"):
        walk_h[fp] = top
        deck |= fp
        if b.get("walk_top"):
            roofs.append((b["name"] + " (roof)", fp))
    elif b["group"] == "Bridges" and "ramp" not in b["name"].lower():
        blocked |= fp   # parapets
    else:
        blocked |= fp
# PLANNED_ROWS=1: add the brief's assumption, solid two-storey house rows 1 m back from each lane edge, 9 m deep,
# left open at connectors, pads, the ravine and the bridges (the street walls Tasks 8-10 will build).
PLANNED = os.environ.get("PLANNED_ROWS") == "1"

gz, gx = np.gradient(walk_h, STEP)
slope = np.degrees(np.arctan(np.hypot(gx, gz)))
walkable = ((slope <= WALK_DEG) | deck) & ~blocked
surface = (surface_mask | deck) & inside & ~blocked


def chaikin(pts, n=2):
    pts = np.asarray(pts, float)
    for _ in range(n):
        q = [pts[0]]
        for a, b in zip(pts[:-1], pts[1:]):
            q += [0.75 * a + 0.25 * b, 0.25 * a + 0.75 * b]
        q.append(pts[-1])
        pts = np.array(q)
    return pts


_poly_dist_cache = {}


def poly_dist(pts):
    """Distance from every grid point to the polyline. Memoized on the exact points and returned read-only (several
    checks ask for the same path); the in-place steps are the same arithmetic in the same order as
    tt = clip(((UU - a0) * ab0 + (VV - a1) * ab1) / L2, 0, 1), best = minimum(best, hypot(UU - (a0 + tt*ab0), ...))."""
    pts = np.asarray(pts, float)
    key = (pts.shape, pts.tobytes())
    best = _poly_dist_cache.get(key)
    if best is not None:
        return best
    best = np.full(UU.shape, np.inf)
    tt, du, dv = (np.empty(UU.shape) for _ in range(3))
    for a, b in zip(pts[:-1], pts[1:]):
        ab = b - a
        L2 = float(ab @ ab)
        np.subtract(UU, a[0], out=tt)
        tt *= ab[0]
        np.subtract(VV, a[1], out=dv)
        dv *= ab[1]
        tt += dv
        tt /= L2
        np.clip(tt, 0, 1, out=tt)
        np.multiply(tt, ab[0], out=du)
        du += a[0]
        np.subtract(UU, du, out=du)
        np.multiply(tt, ab[1], out=dv)
        dv += a[1]
        np.subtract(VV, dv, out=dv)
        np.hypot(du, dv, out=du)
        np.minimum(best, du, out=best)
    best.setflags(write=False)
    _poly_dist_cache[key] = best
    return best


if PLANNED:
    lane_d = np.full(ground.shape, np.inf)
    lane_h = np.zeros(ground.shape)
    open_ = np.zeros(ground.shape, bool)
    for l in LAYOUT["lanes"]:
        d = poly_dist(chaikin(l["points"]))
        edge = l["width"] / 2 + 0.75 + 1.0
        row = (d >= edge) & (d <= edge + 9.0)
        lane_d = np.where(row & (d < lane_d), d, lane_d)
        open_ |= d < edge  # never build on a lane
    for c in LAYOUT["connectors"]:
        if not c.get("roof"):
            cp = chaikin(c["points"], 3) if len(c["points"]) > 2 else np.asarray(c["points"], float)
            open_ |= poly_dist(cp) <= c["width"] / 2 + 1.5
    for a in LAYOUT["areas"]:
        if a["shape"] == "circle":
            open_ |= np.hypot(UU - a["center"][0], VV - a["center"][1]) <= a["radius"]
        else:
            open_ |= (np.abs(UU - a["center"][0]) <= a["size"][0] / 2) & (np.abs(VV - a["center"][1]) <= a["size"][1] / 2)
    open_ |= poly_dist(chaikin(LAYOUT["ravine"]["points"])) <= LAYOUT["ravine"]["width"] / 2 + 2.0
    open_ |= deck
    rows = np.isfinite(lane_d) & ~open_
    occ = np.where(rows, np.maximum(occ, ground + 6.0), occ)
    blocked |= rows
    walkable &= ~rows


# Which named place every walkable surface cell belongs to (later entries win, like the generator's order).
feature = np.full(ground.shape, -1)
feature_names = []


def add_feature(name, cells):
    feature_names.append(name)
    feature[cells & surface] = len(feature_names) - 1


for a in LAYOUT["areas"]:
    if a["shape"] == "circle":
        cells = np.hypot(UU - a["center"][0], VV - a["center"][1]) <= a["radius"]
    else:
        cells = (np.abs(UU - a["center"][0]) <= a["size"][0] / 2) & (np.abs(VV - a["center"][1]) <= a["size"][1] / 2)
    add_feature(a["name"], cells)
for c in LAYOUT["connectors"]:
    if not c.get("roof") and not c.get("ravine"):
        cp = chaikin(c["points"], 3) if len(c["points"]) > 2 else np.asarray(c["points"], float)
        add_feature(c["name"], poly_dist(cp) <= c["width"] / 2 + 0.5)
add_feature("The ravine floor", poly_dist(chaikin(LAYOUT["ravine"]["points"])) <= LAYOUT["ravine"]["walkable_floor"] / 2 + 0.5)
for l in LAYOUT["lanes"]:
    add_feature(l["name"], poly_dist(chaikin(l["points"])) <= l["width"] / 2 + 0.75)
add_feature("Bridge decks", deck)
for name, fp in roofs:
    add_feature(name, fp)

# The playable ravine runs from the upper culvert down; the gully above the Upper Road is scenery.
up_cross = next(c for c in LAYOUT["ravine"]["crossings"] if c["lane"] == "upper")["at"]
ravine_scenery = (feature == feature_names.index("The ravine floor")) & (VV > up_cross[1])
surface &= ~ravine_scenery
feature[ravine_scenery] = -1


def sample(field, x, z, order=1):
    r, c = grid_index(x, z)
    return map_coordinates(field, [np.atleast_1d(r), np.atleast_1d(c)], order=order, mode="nearest")


_stand_h = {}


def stand_height(x, z):
    """walk_h under a world point, memoized: the line-of-sight sweeps test thousands of lines from the same eyes."""
    k = (x, z)
    h = _stand_h.get(k)
    if h is None:
        h = _stand_h[k] = float(sample(walk_h, x, z)[0])
    return h


def los(p, q, eye_p=EYE, eye_q=EYE, step=0.25):
    """Eye-to-eye line of sight over the occluder raster. p, q = world (x, z)."""
    (x0, z0), (x1, z1) = p, q
    d = math.hypot(x1 - x0, z1 - z0)
    n = max(int(d / step), 2)
    t = np.linspace(0, 1, n)[1:-1]
    xs_, zs_ = x0 + (x1 - x0) * t, z0 + (z1 - z0) * t
    h0 = stand_height(x0, z0) + eye_p
    h1 = stand_height(x1, z1) + eye_q
    line = h0 + (h1 - h0) * t
    return bool(np.all(line > sample(occ, xs_, zs_) + 0.05))


stakes = {s["name"]: s for s in META["stakes"]}
cap_r = LAYOUT.get("capture_radius", 7.0)
results = []


def report(no, name, value, target, ok, how):
    results.append((no, name, value, target, "PASS" if ok else "FAIL", how))


# ---------------------------------------------------------------- 1. relief, ramps, landings

no_floor = surface & (feature != feature_names.index("The ravine floor"))
hn = walk_h[no_floor]
report(1, "Walkable relief without the ravine floor", f"{hn.max() - hn.min():.1f} m ({hn.min() - B:+.1f}..{hn.max() - B:+.1f})",
       "<= 15 m", hn.max() - hn.min() <= 15, "lanes, connectors, pads, decks, roofs inside the frame")
hs = walk_h[surface]
relief = float(hs.max() - hs.min())
fmax = feature_names[feature[surface][hs.argmax()]] if feature[surface][hs.argmax()] >= 0 else "?"
fmin = feature_names[feature[surface][hs.argmin()]] if feature[surface][hs.argmin()] >= 0 else "?"
report(1, "Relief incl. the ravine floor (info)", f"{relief:.1f} m ({hs.min() - B:+.1f} {fmin} .. {hs.max() - B:+.1f} {fmax})", "info (user: relief is measured without the floor)", True,
       "range of lane/connector/pad/deck/ravine-floor heights inside the frame (ravine only below the Upper Road)")

worst_grade, worst_rise = 0.0, 0.0
per_rise = {}
worst_names = ["", ""]
for r in META["routes"]:
    xs_, zs_ = np.array(r["xs"]), np.array(r["zs"])
    seg = np.hypot(np.diff(xs_), np.diff(zs_))
    cum = np.concatenate([[0], np.cumsum(seg)])
    s = np.arange(0, cum[-1], 0.5)
    px, pz = np.interp(s, cum, xs_), np.interp(s, cum, zs_)
    h = sample(walk_h, px, pz)
    g = np.abs(np.diff(h)) / 0.5
    g1 = np.convolve(g, np.ones(2) / 2, mode="valid") if len(g) > 2 else g   # 1 m window
    deg = math.degrees(math.atan(g1.max())) if len(g1) else 0.0
    if deg > worst_grade:
        worst_grade, worst_names[0] = deg, r["name"]
    # rise between landings: a landing is >= 3 m with under 5% grade
    flat = np.concatenate([[True], g < 0.05])
    run_len, last_flat_h, max_rise, climb, last_sign = 0, h[0], 0.0, 0.0, 0
    for k in range(len(h)):
        if flat[k]:
            run_len += 1
            if run_len * 0.5 >= 3.0:
                last_flat_h = h[k]
        else:
            run_len = 0
        if k > 0 and g[k - 1] > 0.15:   # only ramp stretches count (walks and the ravine floor under 15% need no landings)
            sign = 1 if h[k] > h[k - 1] else -1
            if sign != last_sign:        # a new flight in the other direction starts its own count
                climb = 0.0
                last_sign = sign
            climb += abs(h[k] - h[k - 1])
        if run_len * 0.5 >= 3.0:
            climb = 0.0
        max_rise = max(max_rise, climb)
    if not r["name"].startswith(("Upper lane", "Main street", "Lower lane")):
        key = r["name"].split(" (")[0]
        per_rise[key] = max(per_rise.get(key, 0.0), max_rise)
    if max_rise > worst_rise and not r["name"].startswith(("Upper lane", "Main street", "Lower lane")):
        worst_rise, worst_names[1] = max_rise, r["name"]
report(1, "Steepest ramp (1 m window, walk routes)", f"{worst_grade:.1f} deg ({worst_names[0]})", "<= 20 deg",
       worst_grade <= 20.0, "surface sampled every 0.5 m along every walk-test route")
report(1, "Rise between landings per connector (info)", ", ".join(f"{k} {v:.1f}" for k, v in sorted(per_rise.items(), key=lambda kv: -kv[1])),
       "<= 3.8 m", max(per_rise.values()) <= 3.8, "same definition, both directions")
report(1, "Most rise without a 3 m landing", f"{worst_rise:.1f} m ({worst_names[1]})", "<= 3.5 m", worst_rise <= 3.5,
       "connector routes; climb counted on stretches over 15% grade (8.5 deg); a landing = 3 m under 5% grade")

# ---------------------------------------------------------------- 2. sightlines

starts = [s for s in META["stakes"] if s["kind"].startswith("start_")]


def circle_points(st, r):
    pts = [(st["x"], st["z"])]
    for k in range(8):
        a = k * math.pi / 4
        pts.append((st["x"] + r * math.cos(a), st["z"] + r * math.sin(a)))
    return pts


bad = []
for fid in ("Flag_A", "Flag_B", "Flag_C"):
    for p in circle_points(stakes[fid], cap_r):
        for s in starts:
            if los(p, (s["x"], s["z"])):
                bad.append(f"{fid}->{s['name']}")
deck_pts = []
for b in META["boxes"]:
    if b["name"] == "Main bridge deck":
        y = math.radians(b["yaw"])
        for a in (-0.4, 0, 0.4):
            for c in (-0.4, 0, 0.4):
                deck_pts.append((b["x"] + math.sin(y) * a * b["sz"] + math.cos(y) * c * b["sx"],
                                 b["z"] + math.cos(y) * a * b["sz"] - math.sin(y) * c * b["sx"]))
for p in deck_pts:
    for s in starts:
        if los(p, (s["x"], s["z"])):
            bad.append(f"bridge->{s['name']}")
bad = sorted(set(bad))
report(2, "Capture circles / bridge -> spawn starts", "none" if not bad else f"{len(bad)}: " + ", ".join(bad[:6]),
       "none", not bad, "eye to eye, circle centre + 8 rim points, 9 deck points, 12 start points")


def lane_points(lane, spacing=2.0):
    pts = np.asarray(lane["points"], float)
    for _ in range(2):  # same chaikin smoothing as the generator
        q = [pts[0]]
        for a, b in zip(pts[:-1], pts[1:]):
            q += [0.75 * a + 0.25 * b, 0.25 * a + 0.75 * b]
        q.append(pts[-1])
        pts = np.array(q)
    seg = np.hypot(*np.diff(pts, axis=0).T)
    cum = np.concatenate([[0], np.cumsum(seg)])
    s = np.arange(0, cum[-1], spacing)
    u, v = np.interp(s, cum, pts[:, 0]), np.interp(s, cum, pts[:, 1])
    keep = (np.abs(u) <= HALF_U) & (np.abs(v) <= HALF_V)
    return u[keep], v[keep]


lane_lines = {}
for lane in LAYOUT["lanes"]:
    u, v = lane_points(lane)
    wx, wz = to_world(u, v)
    best = (0.0, 0, 0)
    for i in range(len(u)):
        for j in range(len(u) - 1, i, -1):
            d = math.hypot(wx[j] - wx[i], wz[j] - wz[i])
            if d <= best[0]:
                break
            if los((wx[i], wz[i]), (wx[j], wz[j])):
                best = (d, i, j)
                break
    lane_lines[lane["id"]] = (best[0], u[best[1]], u[best[2]])
txt = ", ".join(f"{k} {d:.0f} m (u {a:+.0f}->{b:+.0f})" for k, (d, a, b) in lane_lines.items())
long_ones = [k for k, (d, _, _) in lane_lines.items() if d > 60.0]
report(2, "Longest lane lines", txt, "<= 60 m, one Lower Lane vista allowed",
       not long_ones or long_ones == ["lower"], "eye to eye between lane-centre points 2 m apart, inside the frame")

# ---------------------------------------------------------------- 3. Zone B exposure, 4. overwatch

def exposure(fid, r_min=8.0, r_max=60.0, elev=2.5):
    st = stakes[fid]
    p = (st["x"], st["z"])
    hb = float(sample(walk_h, *p)[0])
    near = surface & walkable & (np.hypot(XX - p[0], ZZ - p[1]) >= r_min) & (np.hypot(XX - p[0], ZZ - p[1]) <= r_max)
    idx = np.argwhere(near)[::3]  # every 3rd cell is plenty for bearings
    seen = np.zeros(ground.shape, bool)
    bins = np.zeros(72, bool)
    for rr, cc in idx:
        q = (XX[rr, cc], ZZ[rr, cc])
        if los(p, q, step=0.5):
            seen[rr, cc] = True
            ang = math.degrees(math.atan2(q[0] - p[0], q[1] - p[1])) % 360
            bins[int(ang // 5)] = True
    sectors = 0
    for k in range(72):
        if bins[k] and not bins[k - 1]:
            sectors += 1
    if bins.all():
        sectors = 1
    high = seen & (walk_h >= hb + elev)
    groups = []
    for k in np.unique(feature[high]):
        cells = high & (feature == k)
        if k < 0 or cells.sum() * STEP * STEP < 2.0:
            continue
        gu, gv = to_frame(XX[cells].mean(), ZZ[cells].mean())
        groups.append((gu, gv, float(walk_h[cells].max() - hb), feature_names[k]))
    return sectors, groups, hb, seen


secB, groupsB, hB, _ = exposure("Flag_B")
uB = to_frame(stakes["Flag_B"]["x"], stakes["Flag_B"]["z"])[0]
sides = {"west": sum(1 for g in groupsB if g[0] < uB), "east": sum(1 for g in groupsB if g[0] >= uB)}
report(3, "Zone B exposure angles", f"{secB}", "<= 4", secB <= 4, "5 deg bearings from the centre (eye) with a visible walkable spot 8-60 m out, grouped into sectors")
report(3, "Zone B elevated positions", f"{len(groupsB)} (west {sides['west']}, east {sides['east']}): " +
       "; ".join(f"{g[3]} +{g[2]:.1f} m" for g in groupsB),
       "<= 2, one per side", len(groupsB) <= 2 and sides["west"] <= 1 and sides["east"] <= 1,
       "named places (lane, connector, pad, ravine floor) with a visible spot >= 2.5 m above the zone centre")

for fid in ("Flag_A", "Flag_C"):
    _, groups, hf, seen = exposure(fid, r_min=cap_r, r_max=60.0, elev=0.0)
    groups.sort(key=lambda g: -g[2])
    top = groups[0][2] if groups else 0.0
    report(4, f"Overwatch onto {fid[-1]}", f"+{top:.1f} m" + (f" ({groups[0][3]}; next: " + ", ".join(f"{g[3]} +{g[2]:.1f}" for g in groups[1:4]) + ")" if groups else ""),
           "<= 5 m", top <= 5.0, "highest visible walkable spot (7-60 m) above the flag, by place")

# ---------------------------------------------------------------- 5. sprint times (Dijkstra on the walkable grid)

def dijkstra(src_xz):
    r0, c0 = (int(round(v)) for v in grid_index(*src_xz))
    # Plain nested lists: the same float64 values as the arrays, without numpy's per-element indexing cost.
    dist = np.full(ground.shape, np.inf).tolist()
    dist[r0][c0] = 0.0
    pq = [(0.0, r0, c0)]
    nbrs = [(-1, 0, 1), (1, 0, 1), (0, -1, 1), (0, 1, 1), (-1, -1, 1.4142), (-1, 1, 1.4142), (1, -1, 1.4142), (1, 1, 1.4142)]
    lim = STEP * math.tan(math.radians(WALK_DEG)) * 1.5 + 0.4
    ok = (walkable & (np.abs(UU) <= HALF_U + 15) & (np.abs(VV) <= HALF_V + 15)).tolist()
    wh = walk_h.tolist()
    while pq:
        d, r, c = heapq.heappop(pq)
        if d > dist[r][c]:
            continue
        h = wh[r][c]
        for dr, dc, w in nbrs:
            rr, cc = r + dr, c + dc
            if 0 <= rr < RES and 0 <= cc < RES and ok[rr][cc] and abs(wh[rr][cc] - h) <= lim * w:
                nd = d + w * STEP
                if nd < dist[rr][cc]:
                    dist[rr][cc] = nd
                    heapq.heappush(pq, (nd, rr, cc))
    return np.array(dist)


def at(dist, x, z):
    r, c = (int(round(v)) for v in grid_index(x, z))
    window = dist[max(r - 2, 0):r + 3, max(c - 2, 0):c + 3]
    return float(window.min())


dW = dijkstra((stakes["Spawn_West"]["x"], stakes["Spawn_West"]["z"]))
dE = dijkstra((stakes["Spawn_East"]["x"], stakes["Spawn_East"]["z"]))
bx, bz = stakes["Flag_B"]["x"], stakes["Flag_B"]["z"]
tW, tE = at(dW, bx, bz) / SPRINT, at(dE, bx, bz) / SPRINT
report(5, "Sprint spawn -> B", f"West {tW:.1f} s, East {tE:.1f} s (diff {abs(tW - tE):.1f})", "within 1 s",
       abs(tW - tE) <= 1.0, "shortest walkable path on the 0.5 m grid at 6.5 m/s")
tS = at(dW, stakes["Spawn_East"]["x"], stakes["Spawn_East"]["z"]) / SPRINT
report(5, "Sprint spawn -> spawn", f"{tS:.1f} s", "20-24 s", 20.0 <= tS <= 24.0, "same")
for fid in ("Flag_A", "Flag_C"):
    report(5, f"Sprint spawns -> {fid[-1]}", f"West {at(dW, stakes[fid]['x'], stakes[fid]['z']) / SPRINT:.1f} s, "
           f"East {at(dE, stakes[fid]['x'], stakes[fid]['z']) / SPRINT:.1f} s", "info", True, "same")
rr_area = next(a for a in LAYOUT["areas"] if a["id"] == "rock_roofs")
rr_cells = (np.abs(UU - rr_area["center"][0]) <= rr_area["size"][0] / 2) & (np.abs(VV - rr_area["center"][1]) <= rr_area["size"][1] / 2)
t_rr = float(dW[rr_cells].min()) / SPRINT
ov = next((l for c in LAYOUT["connectors"] for l in c.get("landings", []) if l.get("name") == "East overwatch"), None)
ov_at = ov["at"] if ov else [10.5, 12.0]
t_ov = at(dE, *to_world(*ov_at)) / SPRINT
report(5, "West -> Rock Roofs vs East -> east-bank landing", f"{t_rr:.1f} s vs {t_ov:.1f} s", "within ~2 s",
       abs(t_rr - t_ov) <= 2.0, f"landing at frame {ov_at}")

# ---------------------------------------------------------------- 6. the B circle

circle = np.hypot(XX - bx, ZZ - bz) <= cap_r
flat = (deck | ((slope <= 5.0) & surface)) & circle & ~blocked
share = flat.sum() / circle.sum()
dev = float(np.abs(walk_h[circle & ~blocked] - hB).max())
report(6, "B circle on flat walkable ground", f"{share * 100:.0f}% flat, max height step {dev:.1f} m", "100%, ~0 m",
       share >= 0.97 and dev <= 0.6, f"cells within {cap_r} m: deck or surface under 5 deg")

# ---------------------------------------------------------------- geometric mode (the review-3 brief's definitions)
# Every reachable cell instead of named places: flood fill from the spawns over ground <= 33 deg (steps <= 0.4 m),
# decks and walk-top roofs walkable, inside the frame; 1 m grid, eye 1.7 m; garden/terrace walls and the arch block
# sight only, not paths. Bearings within 15 deg merge into one angle.

G_EYE, G_DEG, G_ELEV = 1.7, 33.0, 2.5
geo = []


def greport(no, name, value, target, ok, how):
    geo.append((no, name, value, target, "PASS" if ok else "FAIL", how))


wall_cells = np.zeros(ground.shape, bool)
for b in META["boxes"]:
    if b["group"] == "Walls":
        y = math.radians(b["yaw"])
        fx, fz = math.sin(y), math.cos(y)
        a = (XX - b["x"]) * fx + (ZZ - b["z"]) * fz
        c = (XX - b["x"]) * fz - (ZZ - b["z"]) * fx
        wall_cells |= (np.abs(a) <= b["sz"] / 2) & (np.abs(c) <= b["sx"] / 2)
g_open = ((slope <= G_DEG) | deck) & ~(blocked & ~wall_cells) & inside


def flood(seeds):
    seen = np.zeros(ground.shape, bool).tolist()  # nested lists, as in dijkstra()
    stack = []
    for x, z in seeds:
        r, c = (int(round(v)) for v in grid_index(x, z))
        win = np.argwhere(g_open[r - 2:r + 3, c - 2:c + 3])
        for dr, dc in win:
            stack.append((r - 2 + int(dr), c - 2 + int(dc)))
    lim = 0.4 + STEP * math.tan(math.radians(G_DEG))
    is_open, wh = g_open.tolist(), walk_h.tolist()
    while stack:
        r, c = stack.pop()
        if seen[r][c]:
            continue
        seen[r][c] = True
        h = wh[r][c]
        for dr in (-1, 0, 1):
            for dc in (-1, 0, 1):
                rr, cc = r + dr, c + dc
                if 0 <= rr < RES and 0 <= cc < RES and is_open[rr][cc] and not seen[rr][cc] \
                        and abs(wh[rr][cc] - h) <= lim:
                    stack.append((rr, cc))
    return np.array(seen)


reach = flood([(stakes[s]["x"], stakes[s]["z"]) for s in ("Spawn_West", "Spawn_East")])
grid1 = np.zeros(ground.shape, bool)
grid1[::2, ::2] = True
reach1 = reach & grid1

hr = walk_h[reach]
high = reach & (walk_h > B + 8.5)
greport(1, "Reachable ground above +8.5", f"{high.sum() * STEP * STEP:.0f} m2" + (f" (max {walk_h[high].max() - B:+.1f})" if high.any() else ""),
        "0 m2", not high.any(), "flood fill from the spawns, <= 33 deg, inside the frame")
greport(1, "Walkable relief (reachable)", f"{hr.max() - hr.min():.1f} m ({hr.min() - B:+.1f}..{hr.max() - B:+.1f})", "<= 15.7 m",
        hr.max() - hr.min() <= 15.7, "all reachable cells incl. the ravine floor")


def bearing(p, q):
    return math.degrees(math.atan2(q[0] - p[0], q[1] - p[1])) % 360


def merge_angles(angs, gap=15.0):
    if not angs:
        return []
    a = sorted(angs)
    groups = [[a[0]]]
    for x in a[1:]:
        if x - groups[-1][-1] <= gap:
            groups[-1].append(x)
        else:
            groups.append([x])
    if len(groups) > 1 and a[0] + 360 - a[-1] <= gap:
        groups[0] = groups[-1] + groups[0]
        groups.pop()
    return groups


def visible_cells(p, r_min, r_max, eye=G_EYE):
    d = np.hypot(XX - p[0], ZZ - p[1])
    idx = np.argwhere(reach1 & (d >= r_min) & (d <= r_max))
    out = []
    for r, c in idx:
        q = (XX[r, c], ZZ[r, c])
        if los(p, q, eye, eye, step=0.5):
            out.append((r, c))
    return out


pB = (stakes["Flag_B"]["x"], stakes["Flag_B"]["z"])
hb_g = float(sample(walk_h, *pB)[0])
visB = visible_cells(pB, cap_r, 80.0)
angs = [bearing(pB, (XX[r, c], ZZ[r, c])) for r, c in visB]
elev = [bearing(pB, (XX[r, c], ZZ[r, c])) for r, c in visB if walk_h[r, c] >= hb_g + G_ELEV]
ga, ge = merge_angles(angs), merge_angles(elev)
fmt = lambda gs: ", ".join(f"{g[0]:.0f}-{g[-1]:.0f}" for g in gs)
def sec(cells):  # 15 deg sectors with >= 10 m2 of visible reachable ground 8-60 m out
    from collections import Counter
    cnt = Counter()
    for r, c in cells:
        if 8.0 <= math.hypot(XX[r, c] - pB[0], ZZ[r, c] - pB[1]) <= 60.0:
            cnt[int(bearing(pB, (XX[r, c], ZZ[r, c])) // 15) * 15] += 1
    return sorted(k for k, n in cnt.items() if n >= 10)


sa = sec(visB)
se = sec([(r, c) for r, c in visB if walk_h[r, c] >= hb_g + G_ELEV])
greport(2, "Zone B exposure angles (geometric)", f"{len(sa)} sectors of 15 deg {sa} (merged runs: {fmt(ga)})", "<= 4", len(sa) <= 4,
        "15 deg bearing sectors with >= 10 m2 of visible reachable ground 8-60 m from the disc centre")
greport(2, "Zone B elevated positions (geometric)", f"{len(se)} sectors {se}", "<= 2", len(se) <= 2,
        f"same, cells >= {G_ELEV} m above the disc centre")

# East perch: the highest East-side (u > disc) reachable cell that sees the disc centre, and any cell of the
# east-bank path above +4.5 within 35 m that sees it.
uBc = to_frame(*pB)[0]
east_vis = [(r, c) for r, c in visB if to_frame(XX[r, c], ZZ[r, c])[0] > uBc + 3.0]
eb = feature_names.index("Ravine east-bank path") if "Ravine east-bank path" in feature_names else -1
perch = max((walk_h[r, c] - hb_g for r, c in east_vis if feature[r, c] == eb), default=0.0)
peeks = [(r, c) for r, c in east_vis if feature[r, c] == eb and walk_h[r, c] - hb_g > 4.5
         and math.hypot(XX[r, c] - pB[0], ZZ[r, c] - pB[1]) <= 35.0]
greport(2, "East perch over the disc", f"+{perch:.1f} m; east-bank path cells above +4.5 within 35 m: {len(peeks)}" +
        (f" (up to +{max(walk_h[r, c] - hb_g for r, c in peeks):.1f})" if peeks else ""),
        "<= +4.5, none", perch <= 4.5 and not peeks, "east-bank path and its landing cells that see the disc centre")

# Upper Road u -10..25 -> capture disc (centre, 2.5 m and 5 m rings).
disc = [pB] + [(pB[0] + r * math.cos(k * math.pi / 6), pB[1] + r * math.sin(k * math.pi / 6)) for r in (2.5, cap_r) for k in range(12)]
up = next(l for l in LAYOUT["lanes"] if l["id"] == "upper")
uu, vv = lane_points(up, 1.0)
hits = set()
for u0, v0 in zip(uu, vv):
    if -10 <= u0 <= 25:
        for dv in (-up["width"] / 2, 0, up["width"] / 2):
            q = tuple(float(a) for a in to_world(u0, v0 + dv))
            if any(los(q, d) for d in disc):
                hits.add(int(round(u0)))
greport(2, "Upper Road (u -10..25) -> capture disc", "none" if not hits else f"{len(hits)} points at u " + ",".join(map(str, sorted(hits))),
        "none", not hits, "lane centre and both edges every 1 m, to the disc centre and 24 ring points")

# Rays from the disc centre clear for >= 40 m, except along the main street axes.
mn = next(l for l in LAYOUT["lanes"] if l["id"] == "main")
mu, mv = lane_points(mn, 1.0)
mx, mz = to_world(mu, mv)
dm = np.hypot(mx - pB[0], mz - pB[1])
k0 = int(dm.argmin())
axes = []
for k in (max(k0 - 25, 0), min(k0 + 25, len(mu) - 1)):
    axes.append(bearing(pB, (mx[k], mz[k])))
far = []
for r, c in visB:
    q = (XX[r, c], ZZ[r, c])
    if math.hypot(q[0] - pB[0], q[1] - pB[1]) >= 40.0:
        b_ = bearing(pB, q)
        if min(min(abs(b_ - a), 360 - abs(b_ - a)) for a in axes) > 12.0:
            far.append(b_)
gf = merge_angles(far)
greport(2, "Disc-centre lines >= 40 m off the street axes", "none" if not gf else f"{len(gf)} [{fmt(gf)}]", "none", not gf,
        f"street axes {axes[0]:.0f}/{axes[1]:.0f} deg +-12")

# Capture ring of B -> start points.
ring = [(pB[0] + cap_r * math.cos(k * math.pi / 6), pB[1] + cap_r * math.sin(k * math.pi / 6)) for k in range(12)] + [pB]
pairs = {"west": 0, "east": 0}
for p in ring:
    for s in starts:
        if los(p, (s["x"], s["z"]), G_EYE, G_EYE):
            pairs["west" if "west" in s["kind"] else "east"] += 1
greport(3, "B capture ring -> start points", f"West {pairs['west']}/78, East {pairs['east']}/78", "0", pairs["west"] + pairs["east"] == 0,
        "12 ring points + centre to 6 starts per side, eye to eye")

# Ravine rim minus floor.
rv_pts = chaikin(LAYOUT["ravine"]["points"], 2)
rseg = np.hypot(*np.diff(rv_pts, axis=0).T)
rcum = np.concatenate([[0], np.cumsum(rseg)])
rs = np.arange(0, rcum[-1], 0.5)
ru_, rv_ = np.interp(rs, rcum, rv_pts[:, 0]), np.interp(rs, rcum, rv_pts[:, 1])
half = LAYOUT["ravine"]["width"] / 2
def rim_check(stations_v):
    out, low = [], 99.0
    for vq in stations_v:
        j = int(np.abs(rv_ - vq).argmin())
        tu, tv = np.gradient(ru_)[j], np.gradient(rv_)[j]
        n = math.hypot(tu, tv)
        nu, nv = -tv / n, tu / n   # to the east bank (the centreline runs north to south)
        fl = min(float(sample(ground, *to_world(ru_[j] + nu * o, rv_[j] + nv * o))[0]) for o in (-1.5, -0.75, 0, 0.75, 1.5))
        if vq > up_cross[1] and not float(sample(surface_mask.astype(float), *to_world(ru_[j], rv_[j]))[0]) > 0.5:
            out.append(f"v{vq} closed by the Upper Road cliff")
            continue
        sides_ = []
        for sgn in (-1, 1):
            rim = max(float(sample(ground, *to_world(ru_[j] + sgn * nu * o, rv_[j] + sgn * nv * o))[0]) for o in (half + 0.5, half + 1.5, half + 2.5))
            sides_.append(rim - fl)
        low = min(low, *sides_)
        out.append(f"v{vq} W {sides_[0]:.1f} E {sides_[1]:.1f}")
    return out, low


rims, worst = rim_check((10, 22, 35, 40))
greport(4, "Ravine rim minus floor, north (v 10..35)", "; ".join(rims), ">= 4.4 m", worst >= 4.4,
        f"highest ground {half + 0.5:.0f}-{half + 2.5:.0f} m from the centreline vs the floor")
rims_s, worst_s = rim_check((-12, -24, -35, -46))
greport(4, "Ravine rim minus floor, south (v -12..-46)", "; ".join(rims_s), ">= 3.5 m", worst_s >= 3.5, "same")

# Overwatch onto A and C: highest visible reachable cell (5-60 m) above the flag.
for fid in ("Flag_A", "Flag_C"):
    pf = (stakes[fid]["x"], stakes[fid]["z"])
    hf = float(sample(walk_h, *pf)[0])
    vis = visible_cells(pf, cap_r, 60.0)
    top = max((walk_h[r, c] - hf for r, c in vis), default=0.0)
    greport(6, f"Overwatch onto {fid[-1]} (geometric)", f"+{top:.1f} m", "<= 5 m", top <= 5.0, "highest visible reachable cell 5-60 m")
    if fid == "Flag_C" and "Terrace steps" in feature_names:
        ts = feature_names.index("Terrace steps")
        top_ts = max((walk_h[r, c] - hf for r, c in vis if feature[r, c] == ts), default=0.0)
        greport(6, "Overwatch onto C from the Terrace steps", f"+{top_ts:.1f} m", "<= 5 m", top_ts <= 5.0, "same, Terrace steps cells only")

# East overwatch landing -> flags.
if ov:
    pl = tuple(float(a) for a in to_world(*ov_at))
    txt, ok_ = [], True
    for fid in ("Flag_A", "Flag_B", "Flag_C"):
        pf = (stakes[fid]["x"], stakes[fid]["z"])
        dd = math.hypot(pf[0] - pl[0], pf[1] - pl[1])
        seen_ = los(pl, pf, G_EYE, G_EYE)
        txt.append(f"{fid[-1]} {dd:.0f} m {'seen' if seen_ else 'hidden'}")
        if seen_ and dd >= 35.0:
            ok_ = False
        if fid == "Flag_B" and not seen_:
            ok_ = False
    greport(6, "East overwatch landing -> flags", ", ".join(txt), "B seen, no flag >= 35 m", ok_, "eye to eye")

# ---------------------------------------------------------------- print

print(f"\nPlay ground checks (Flag B at {B:.2f} m, capture radius {cap_r} m, frame {META['frame_size']})")
for no, name, value, target, verdict, how in results:
    print(f"  [{no}] {verdict:4s}  {name}: {value}   (target {target}; {how})")
print("\nGeometric mode (review-3 brief: reachable cells, 1 m grid, eye 1.7 m, 15 deg merge)")
for no, name, value, target, verdict, how in geo:
    print(f"  [{no}] {verdict:4s}  {name}: {value}   (target {target}; {how})")
