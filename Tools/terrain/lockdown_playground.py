"""Play ground for LOCKDOWN (Task 7): the real slope under the play area, graded for play from the layout data.

Run from the project root, after lockdown_dem.py:
    uv run --with numpy --with scipy --with pillow --with contourpy Tools/terrain/lockdown_playground.py

Reads Assets/Lockdown/Data/Layout/lockdown_layout.json (frame coordinates u along the main street, v uphill;
the "ground" block holds every grading parameter) and SourceData/dem/village_513.r16, and writes
SourceData/playground/ for Lockdown/Build/Play Ground:
    play_513.r16      16-bit heights, rows south to north, scaled to [base, top] in play_meta.json
    play_mask.png     512 px surface mask (white = lane, connector or pad), north up
    play_meta.json    tile placement, the village hole, stake and bounds-post positions
and review sheets in Screenshots/ (never Assets/): task07_ground_plan.png, task07_ground_slope.png.

The tile is a whole number of village-terrain cells, so its edge lies on village vertices; its edge ring
keeps the real heights exactly and all grading fades back to them outside the play frame.
Order: smoothed real ground + height balance -> terraces -> connector ramps -> pads -> lane benches.
"""
import json
import math
import os
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont
from scipy.ndimage import binary_dilation, binary_erosion, gaussian_filter, gaussian_filter1d, map_coordinates

ROOT = Path(__file__).resolve().parents[2]
LAYOUT = ROOT / "Assets" / "Lockdown" / "Data" / "Layout" / "lockdown_layout.json"
DEM = ROOT / "SourceData" / "dem"
OUT = ROOT / "SourceData" / "playground"
SHOTS = ROOT / "Screenshots"

WALKABLE_DEG = 36.0      # LockdownWalker slope limit
POST_SPACING = 10.0      # bounds posts along the frame edge
SUN_AZIMUTH, SUN_ELEVATION = 263.9, 19.9


# ---------------------------------------------------------------- data

layout = json.loads(LAYOUT.read_text(encoding="utf-8"))
G = layout["ground"]
frame = layout["frame"]
CX, CZ = frame["center"]
ROT = math.radians(frame["rotation_deg"])
COS, SIN = math.cos(ROT), math.sin(ROT)
HALF_U, HALF_V = frame["size"][0] / 2, frame["size"][1] / 2


def to_world(u, v):
    return CX + u * COS - v * SIN, CZ + u * SIN + v * COS


def to_frame(x, z):
    dx, dz = x - CX, z - CZ
    return dx * COS + dz * SIN, -dx * SIN + dz * COS


dem_meta = json.loads((DEM / "dem_meta.json").read_text())
VRES, VSIZE = dem_meta["village"]["resolution"], dem_meta["village"]["size"]
VCELL = VSIZE / (VRES - 1)
raw = np.fromfile(DEM / dem_meta["village"]["file"], dtype="<u2").reshape(VRES, VRES).astype(np.float64)
village = dem_meta["base_height"] + raw / 65535.0 * (dem_meta["top_height"] - dem_meta["base_height"])

# Tile placement: whole village cells around the frame centre.
CELLS, RES = G["tile_cells"], G["tile_resolution"]
first_x = int(round((CX + VSIZE / 2) / VCELL - CELLS / 2))
first_z = int(round((CZ + VSIZE / 2) / VCELL - CELLS / 2))
X0, Z0 = -VSIZE / 2 + first_x * VCELL, -VSIZE / 2 + first_z * VCELL
SIZE = CELLS * VCELL
STEP = SIZE / (RES - 1)

xs = X0 + np.arange(RES) * STEP
zs = Z0 + np.arange(RES) * STEP
XX, ZZ = np.meshgrid(xs, zs)          # [row = z (south to north), col = x]
UU, VV = to_frame(XX, ZZ)

# Real ground, bilinear from the village heightmap (exactly what the village terrain shows along cell edges).
fx = (XX + VSIZE / 2) / VCELL
fz = (ZZ + VSIZE / 2) / VCELL
real = map_coordinates(village, [fz, fx], order=1, mode="nearest")


def sample(h, x, z):
    """Bilinear sample of a tile-grid field at world points."""
    x, z = np.atleast_1d(np.asarray(x, float)), np.atleast_1d(np.asarray(z, float))
    r = map_coordinates(h, [(z - Z0) / STEP, (x - X0) / STEP], order=1, mode="nearest")
    return r[0] if r.size == 1 else r


def sample_uv(h, u, v):
    x, z = to_world(np.asarray(u, float), np.asarray(v, float))
    return sample(h, x, z)


# ---------------------------------------------------------------- geometry helpers

def chaikin(pts, n=2):
    pts = np.asarray(pts, float)
    for _ in range(n):
        q = [pts[0]]
        for a, b in zip(pts[:-1], pts[1:]):
            q += [0.75 * a + 0.25 * b, 0.25 * a + 0.75 * b]
        q.append(pts[-1])
        pts = np.array(q)
    return pts


def extend(pts, length):
    pts = np.asarray(pts, float).copy()
    d0 = pts[0] - pts[1]
    d1 = pts[-1] - pts[-2]
    return np.vstack([pts[0] + d0 / np.linalg.norm(d0) * length, pts, pts[-1] + d1 / np.linalg.norm(d1) * length])


def grid_cache(key):
    """Memoizes a whole-grid field function on its exact inputs. grade() runs five times and most of its fields
    (lanes, connectors, ravine, pads) do not depend on the balance offsets. Results are made read-only, so a caller
    can never change a cached field in place."""
    def deco(fn):
        cache = {}

        def wrapper(*args):
            k = key(*args)
            r = cache.get(k)
            if r is None:
                r = fn(*args)
                for arr in (r if isinstance(r, tuple) else (r,)):
                    arr.setflags(write=False)
                cache[k] = r
            return r
        return wrapper
    return deco


def _pts_key(pts):
    pts = np.asarray(pts, float)
    return pts.shape, pts.tobytes()


@grid_cache(_pts_key)
def polyline_field(pts):
    """Distance from every grid point to the polyline (frame coords) and the station of the nearest point."""
    best_d = np.full(UU.shape, np.inf)
    best_s = np.zeros(UU.shape)
    # Scratch grids, reused per segment; the in-place steps are the same arithmetic in the same order as
    # t = clip(((UU - a0) * ab0 + (VV - a1) * ab1) / L^2, 0, 1) and d = hypot(UU - (a0 + t*ab0), VV - (a1 + t*ab1)).
    t, du, dv, d = (np.empty(UU.shape) for _ in range(4))
    s0 = 0.0
    for a, b in zip(pts[:-1], pts[1:]):
        ab = b - a
        L = float(np.hypot(*ab))
        np.subtract(UU, a[0], out=t)
        t *= ab[0]
        np.subtract(VV, a[1], out=dv)
        dv *= ab[1]
        t += dv
        t /= L * L
        np.clip(t, 0, 1, out=t)
        np.multiply(t, ab[0], out=du)
        du += a[0]
        np.subtract(UU, du, out=du)
        np.multiply(t, ab[1], out=dv)
        dv += a[1]
        np.subtract(VV, dv, out=dv)
        np.hypot(du, dv, out=d)
        closer = d < best_d
        best_d[closer] = d[closer]
        best_s[closer] = s0 + t[closer] * L
        s0 += L
    return best_d, best_s


def stations(pts, spacing=0.5):
    seg = np.hypot(*np.diff(pts, axis=0).T)
    cum = np.concatenate([[0], np.cumsum(seg)])
    s = np.arange(0, cum[-1] + 1e-6, spacing)
    u = np.interp(s, cum, pts[:, 0])
    v = np.interp(s, cum, pts[:, 1])
    return s, u, v, cum


def limit_grade(h, spacing, grade):
    h = h.copy()
    dmax = grade * spacing
    for _ in range(4):  # forward/backward passes pull the profile under the grade limit
        for i in range(1, len(h)):
            h[i] = np.clip(h[i], h[i - 1] - dmax, h[i - 1] + dmax)
        for i in range(len(h) - 2, -1, -1):
            h[i] = np.clip(h[i], h[i + 1] - dmax, h[i + 1] + dmax)
    return h


@grid_cache(lambda a: json.dumps(a, sort_keys=True))
def area_distance(a):
    if a["shape"] == "circle":
        return np.maximum(np.hypot(UU - a["center"][0], VV - a["center"][1]) - a["radius"], 0)
    du = np.maximum(np.abs(UU - a["center"][0]) - a["size"][0] / 2, 0)
    dv = np.maximum(np.abs(VV - a["center"][1]) - a["size"][1] / 2, 0)
    return np.hypot(du, dv)


def frame_outside_distance():
    du = np.maximum(np.abs(UU) - HALF_U, 0)
    dv = np.maximum(np.abs(VV) - HALF_V, 0)
    return np.hypot(du, dv)


def apply_feature(h, d, target, half_width, bank):
    """Flat core at the target height, banks of slope `bank` back to the existing ground."""
    reach = bank * np.maximum(d - half_width, 0)
    return np.clip(h, target - reach, target + reach)


# ---------------------------------------------------------------- the grading

points = {s["id"]: s["at"] for s in layout["spawns"]}
points.update({f["id"]: f["at"] for f in layout["flags"]})
areas = {a["id"]: a for a in layout["areas"]}
lanes = {l["id"]: l for l in layout["lanes"]}
BANK = G["bank_slope"]
LG, CG = G["lanes"], G["connectors"]

base = gaussian_filter(real, G["smooth_sigma"] / STEP, mode="nearest")


def rbf(centres, values, sigma):
    c = np.asarray(centres, float)
    r2 = ((c[:, None, :] - c[None, :, :]) ** 2).sum(-1)
    w = np.linalg.solve(np.exp(-r2 / (2 * sigma ** 2)), np.asarray(values, float))
    field = np.zeros(UU.shape)
    for (cu, cv), wi in zip(c, w):
        field += wi * np.exp(-((UU - cu) ** 2 + (VV - cv) ** 2) / (2 * sigma ** 2))
    return field


bal = G["balance"]
for t in bal["targets"]:   # a target with its own "at" stays put when the stake of that name moves (Task 9: Flag A)
    if "at" in t:
        points[t["id"]] = tuple(t["at"])
anchor_pts = [points["Flag_B"]] + [points[t["id"]] for t in bal["targets"]] + bal["keep"]
offsets = [0.0] * (1 + len(bal["targets"]) + len(bal["keep"]))  # the correction passes below find them


def lane_profile(lane, ground, flag_b=None):
    """Lane height per 0.5 m station: the real ground smoothed, or, for a lane with height targets
    (lanes.targets_above_flag_b: [[u, height], ...] relative to Flag B), those targets interpolated along u."""
    pts = extend(chaikin(lane["points"]), LG["extend"])
    s, u, v, _ = stations(pts)
    targets = LG.get("targets_above_flag_b", {}).get(lane["id"])
    if targets is not None:
        tu, th = zip(*targets)
        if flag_b is None:  # the main street sets Flag B itself: its targets sit on the real ground at B
            flag_b = float(sample_uv(ground, *points["Flag_B"]))
        prof = flag_b + np.interp(u, tu, th)
    else:
        prof = sample_uv(ground, u, v)
    prof = gaussian_filter1d(prof, LG["profile_sigma"] / 0.5, mode="nearest")
    prof = limit_grade(prof, 0.5, LG["max_grade"])
    return pts, s, u, v, prof


def lane_height_at(prof_data, p):
    _, s, u, v, prof = prof_data
    i = int(np.argmin((u - p[0]) ** 2 + (v - p[1]) ** 2))
    return float(prof[i])


RAV = layout["ravine"]
RG = G.get("ravine", {})


def ravine_floor(natural, profiles):
    """Ravine centreline (north to south) and its floor height per 0.5 m station: a full depth under every lane
    crossing (the control points), straight between them, and outside them the real gully a depth below the ground,
    never steeper than ravine.floor_max_grade."""
    rpts = chaikin(RAV["points"])
    s, u, v, _ = stations(rpts)
    nat = sample_uv(natural, u, v) - RAV["depth"]
    cps = []
    for lid, l in lanes.items():
        pts, ls, lu, lv, prof = profiles[lid]
        dist = np.hypot(u[:, None] - lu[None, :], v[:, None] - lv[None, :])
        j = int(dist.min(axis=1).argmin())
        if dist[j].min() <= l["width"]:
            cps.append((j, float(prof[dist[j].argmin()]) - RG.get("depth_at", {}).get(lid, RAV["depth"])))
    cps.sort()
    g = RG.get("floor_max_grade", 0.5) * 0.5
    floor = nat.copy()
    if cps:
        js, hs = zip(*cps)
        inner = (np.arange(len(s)) >= js[0]) & (np.arange(len(s)) <= js[-1])
        floor[inner] = np.interp(np.arange(len(s))[inner], js, hs)
        g_north = 0.0 if LG.get("upper_cliff") else g   # the Upper Road cliff closes the gully: a level pocket
        for i in range(js[0] - 1, -1, -1):          # north of the first crossing: climb no faster than g
            floor[i] = min(nat[i], floor[i + 1] + g_north)
        for i in range(js[-1] + 1, len(s)):         # south of the last: fall no faster than g
            floor[i] = max(min(nat[i], floor[i - 1]), floor[i - 1] - g)
    return rpts, s, floor


def ravine_ramps(rv, profiles):
    """Ramps cut into the east bank: level from the lane (beside the bridge) to a point clear of the deck,
    then down to the floor at no more than ramp_max_grade, running away from the lane along the ravine."""
    rpts, rs, rfloor = rv
    _, ru, rvv, _ = stations(rpts)
    specs = []
    for cr in RAV["crossings"]:
        if cr["lane"] not in ("main", "lower"):
            continue
        lane = lanes[cr["lane"]]
        lp, ls, lu, lv, lprof = profiles[cr["lane"]]
        dist = np.hypot(lu[:, None] - ru[None, :], lv[:, None] - rvv[None, :])
        i = int(dist.min(axis=1).argmin())
        j0 = int(dist[i].argmin())
        away = 1 if cr["lane"] == "main" else -1          # south from the main street, north from the lower lane
        a, b = max(j0 - 4, 0), min(j0 + 4, len(ru) - 1)
        du, dv = ru[b] - ru[a], rvv[b] - rvv[a]
        n = math.hypot(du, dv)
        du, dv = du / n, dv / n
        eu, ev = (-dv, du) if -dv > 0 else (dv, -du)       # normal to the ravine, pointing east
        off = RG["ramp_offset"]
        lane_h = float(lprof[i])
        start = (ru[j0] + eu * off, rvv[j0] + ev * off)
        deck_w = cr.get("deck", [0, lane["width"] + 2 * LG["shoulder"]])[1]
        jt = j0 + away * int(round((deck_w / 2 + 1.5) / 0.5))   # the ramp starts down clear of the deck and its bridgehead
        top = (ru[jt] + eu * off, rvv[jt] + ev * off)
        for steps in range(6, 120):
            jb = int(np.clip(jt + away * steps, 0, len(rfloor) - 1))
            bottom = (ru[jb] + eu * 0.5, rvv[jb] + ev * 0.5)
            run = math.hypot(bottom[0] - top[0], bottom[1] - top[1])
            drop = lane_h - rfloor[jb]
            n_land = max(int(math.ceil(drop / CG.get("landing", {}).get("every_rise", 99.0) - 1e-6)) - 1, 0)
            land = CG.get("landing", {}).get("length", 0.0) + 1.5
            if drop / max(run - n_land * land, 0.1) <= RG["ramp_max_grade"] * 0.9:
                break
        je = int(np.clip(jb + away * 6, 0, len(rfloor) - 1))   # run on 3 m along the floor so the end blends in
        beyond = (ru[je] + eu * 0.5, rvv[je] + ev * 0.5)
        pts = np.array([start, top, bottom, beyond])
        s, u, v, cum = stations(pts)
        known = np.full(len(s), np.nan)
        known[s <= cum[1]] = lane_h
        known[np.argmin(np.abs(s - cum[2]))] = float(rfloor[jb])
        known[-1] = float(rfloor[je])
        prof = flights(s, known, RG["ramp_max_grade"])
        specs.append({"name": cr["name"], "lane": cr["lane"], "pts": pts, "s": s, "prof": prof, "jb": jb,
                      "e": (eu, ev), "away": (du * away, dv * away), "lane_pt": (float(lu[i]), float(lv[i])),
                      "lane_t": ((lu[min(i + 2, len(lu) - 1)] - lu[max(i - 2, 0)]), (lv[min(i + 2, len(lv) - 1)] - lv[max(i - 2, 0)])),
                      "run": run, "drop": lane_h - float(rfloor[jb])})
    return specs


def design_surface(profiles):
    def lane_v_h(lid):
        pts, s, u, v, prof = profiles[lid]
        return np.interp(UU, u, v), np.interp(UU, u, prof)

    (vu, hu), (vm, hm), (vl, hl) = lane_v_h("upper"), lane_v_h("main"), lane_v_h("lower")
    nat_u = map_coordinates(base, [((to_world(UU, vu)[1]) - Z0) / STEP, ((to_world(UU, vu)[0]) - X0) / STEP], order=1, mode="nearest")
    nat_l = map_coordinates(base, [((to_world(UU, vl)[1]) - Z0) / STEP, ((to_world(UU, vl)[0]) - X0) / STEP], order=1, mode="nearest")
    d = np.where(VV >= vu, hu + np.maximum(0, base - nat_u), 0.0)
    a = np.clip((VV - vm) / np.maximum(vu - vm, 1e-3), 0, 1)
    d = np.where((VV >= vm) & (VV < vu), hm + (hu - hm) * a, d)
    a = np.clip((VV - vl) / np.maximum(vm - vl, 1e-3), 0, 1)
    d = np.where((VV >= vl) & (VV < vm), hl + (hm - hl) * a, d)
    d = np.where(VV < vl, hl + np.minimum(0, base - nat_l), d)
    return d


def connector_path(c):
    """Connector centreline with rounded corners (three Chaikin passes), so dog-legs have no sharp inner corner."""
    return chaikin(c["points"], 3) if len(c["points"]) > 2 else np.asarray(c["points"], float)


def flights(s, known, max_grade=None, soft=None, landing=None):
    """Walkable profile through the known (level) stretches: between them, equal flights at no more than
    max_grade with a flat landing (connectors.landing.length) after every landing.every_rise metres of rise.
    Where two known stretches are too close for that, the soft one (a pad edge) gives way, station by station."""
    max_grade = (CG["max_grade"] if max_grade is None else max_grade) * 0.9  # under the limit: rounded corners and the 0.5 m grid add up to ~1 deg
    LD = dict(CG.get("landing", {"every_rise": 99.0, "length": 0.0}))
    LD.update(landing or {})  # per-connector override (connectors.overrides)
    LD["length"] += 1.5 if LD["length"] > 0 else 0.0  # the light smoothing eats ~0.5 m at each end of a landing
    known = known.copy()
    soft = np.zeros(len(known), bool) if soft is None else soft.copy()

    def needed(rise):
        n = max(int(math.ceil(abs(rise) / LD["every_rise"] - 1e-6)) - 1, 0)
        return abs(rise) / max_grade + n * LD["length"]

    changed = True
    while changed:
        changed = False
        ok = np.flatnonzero(~np.isnan(known))
        for a, b in zip(ok[:-1], ok[1:]):
            if abs(known[b] - known[a]) < 1e-3 or s[b] - s[a] >= needed(known[b] - known[a]) - 1e-6:
                continue
            if soft[b]:
                known[b] = np.nan
            elif soft[a]:
                known[a] = np.nan
            else:
                continue
            changed = True
            break
    prof = known.copy()
    ok = np.flatnonzero(~np.isnan(known))
    for a, b in zip(ok[:-1], ok[1:]):
        if b - a <= 1:
            continue
        ha, hb = known[a], known[b]
        rise = hb - ha
        gap = s[b] - s[a]
        n_land = max(int(math.ceil(abs(rise) / LD["every_rise"] - 1e-6)) - 1, 0)
        while n_land > 0 and gap - n_land * LD["length"] < abs(rise) / max_grade:
            n_land -= 1          # not enough room: fewer landings rather than a steeper flight (reported by the checks)
            if REPORT:
                print(f"    WARNING no room for every landing: {abs(rise):.1f} m rise over {gap:.1f} m (from {known[a] - FLAG_B_PRINT:+.1f})")
        run = gap - n_land * LD["length"]
        flight = run / (n_land + 1)
        x = s[a:b + 1] - s[a]
        # position inside the flight/landing pattern -> fraction of the rise done
        period = flight + LD["length"]
        k = np.minimum(np.floor(x / period), n_land)
        within = np.clip(x - k * period, 0, flight) / max(flight, 1e-6)
        prof[a:b + 1] = ha + rise * (k + within) / (n_land + 1)
        if REPORT and abs(rise) / max(flight * (n_land + 1), 1e-6) > max_grade + 1e-3:
            print(f"    WARNING flight steeper than {math.degrees(math.atan(max_grade)):.0f} deg "
                  f"({abs(rise):.1f} m over {gap:.1f} m)")
    return prof


def crossing_geom(rv, profiles):
    """Every lane crossing of the ravine: lane station, centre, lane direction, deck size and heights."""
    rpts, rs, rfloor = rv
    _, ru, rvv, _ = stations(rpts)
    out = []
    for cr in RAV["crossings"]:
        lane = lanes[cr["lane"]]
        lp, ls, lu, lv, lprof = profiles[cr["lane"]]
        dist = np.hypot(lu[:, None] - ru[None, :], lv[:, None] - rvv[None, :])
        i = int(dist.min(axis=1).argmin())
        tu = lu[min(i + 2, len(lu) - 1)] - lu[max(i - 2, 0)]
        tv = lv[min(i + 2, len(lv) - 1)] - lv[max(i - 2, 0)]
        n = math.hypot(tu, tv)
        tu, tv = tu / n, tv / n
        span, deck_w = cr.get("deck", [2 * (RAV["width"] / 2 + 1.0), lane["width"] + 2 * LG["shoulder"]])
        k = int(round(span / 2 / 0.5))
        out.append({"cr": cr, "lane": lane, "i": i, "u": float(lu[i]), "v": float(lv[i]), "tu": tu, "tv": tv,
                    "span": span, "deck_w": deck_w, "hc": float(lprof[i]),
                    "h0": float(lprof[max(i - k, 0)]), "h1": float(lprof[min(i + k, len(lprof) - 1)]),
                    "rj": int(dist[i].argmin())})
    return out


@grid_cache(lambda *args: np.array(args, float).tobytes())
def rect_distance(cu, cv, du, dv, length, width):
    """Distance from every grid point to an oriented rectangle (0 inside)."""
    a = (UU - cu) * du + (VV - cv) * dv
    b = -(UU - cu) * dv + (VV - cv) * du
    return np.hypot(np.maximum(np.abs(a) - length / 2, 0), np.maximum(np.abs(b) - width / 2, 0))


REPORT = False
FLAG_B_PRINT = 0.0
ROCK = np.zeros(UU.shape, bool)  # rock faces made by grade() (Upper Road cliff, rock ridges): no houses or terrace walls


@grid_cache(_pts_key)
def polygon_distance(poly):
    """Distance from every grid point to a frame polygon (0 inside)."""
    P = np.asarray(poly, float)
    inside = np.zeros(UU.shape, bool)
    best = np.full(UU.shape, np.inf)
    for a, b in zip(P, np.roll(P, -1, axis=0)):
        if (a[1] > VV).any() or (b[1] > VV).any():
            cross = ((a[1] > VV) != (b[1] > VV)) & (UU < (b[0] - a[0]) * (VV - a[1]) / (b[1] - a[1] + 1e-12) + a[0])
            inside ^= cross
        ab = b - a
        tt = np.clip(((UU - a[0]) * ab[0] + (VV - a[1]) * ab[1]) / float(ab @ ab), 0, 1)
        best = np.minimum(best, np.hypot(UU - (a[0] + tt * ab[0]), VV - (a[1] + tt * ab[1])))
    return np.where(inside, 0.0, best)


def grade(offsets):
    global ROCK
    rock_ridges = []
    # Lanes first: the main street sets Flag B on the real ground, the other lanes follow their targets relative to it.
    profiles = {"main": lane_profile(lanes["main"], base)}
    b_level = lane_height_at(profiles["main"], points["Flag_B"])
    for lid, l in lanes.items():
        if lid != "main":
            profiles[lid] = lane_profile(l, base, b_level)
    # Design surface: between the lanes the ground runs from one lane height to the next; above the Upper Road
    # and below the Lower Lane it keeps the real slope's rise and fall. Balance offsets go on top.
    ground = design_surface(profiles) + rbf(anchor_pts, offsets, bal["radius"])
    natural = ground.copy()
    h = ground.copy()
    mask = np.zeros(UU.shape, bool)
    surface = np.zeros(UU.shape)  # designed height of every lane/ramp/pad pixel; later banks that cut it make it a wall

    # Terraces: contour-following steps, flat treads and steep risers, on the free ground.
    T = G["terraces"]
    step, r = min(T["step"], T.get("max_riser", T["step"])), T["riser_fraction"]
    f = h / step + (1 - r) / 2
    k = np.floor(f)
    t = np.clip((f - k - (1 - r)) / r, 0, 1)
    terraced = step * (k + t * t * (3 - 2 * t))
    if not T["above_upper_lane"]:
        up = chaikin(lanes["upper"]["points"])
        v_up = np.interp(UU, up[:, 0], up[:, 1])
        w_up = np.clip((v_up + lanes["upper"]["width"] / 2 + 4 - VV) / 4, 0, 1)  # fades out just above the upper lane
        terraced = natural + (terraced - natural) * w_up
    h = terraced

    # Upper Road cliff (lanes.upper_cliff): north of the road a shelf at road height for `offset` metres past the
    # road edge, then a rock face at slope_deg up to at least top_above_flag_b (or the hillside, if higher). The
    # road sits at the foot of the bare rock; nothing north of it is reachable.
    rock = np.zeros(UU.shape, bool)
    cliff_face = np.zeros(UU.shape, bool)
    UC = LG.get("upper_cliff")
    if UC:
        pts_c, s_c, u_c, v_c, prof_c = profiles["upper"]
        d_c, ss_c = polyline_field(pts_c)
        north = VV > np.interp(UU, u_c, v_c)
        dn = d_c - (lanes["upper"]["width"] / 2 + LG["shoulder"])
        road = np.interp(ss_c, s_c, prof_c)
        face = road + math.tan(math.radians(UC["slope_deg"])) * np.maximum(dn - UC["offset"], 0)
        top = np.maximum(h, b_level + UC["top_above_flag_b"])
        cliff = np.where(dn <= UC["offset"], road, np.minimum(face, top))
        zone = north & (dn > 0)
        h = np.where(zone, cliff, h)
        cliff_face = north & (dn > UC["offset"])
        rock |= north & (dn > UC["offset"] - 0.5)

    # Ridges: the ground raised to at least a height inside a frame rectangle (a convex terrace edge that
    # hides one place from another), with banks back down; pads, connectors, lanes and the ravine cut through.
    # A ridge with "face_deg" is bare rock: steep faces, no walkable shoulder, no houses or terrace walls on it.
    # "poly" (frame points) instead of "u"/"v" gives an outcrop of any outline.
    for rg in G.get("ridges", []):
        if "poly" in rg:
            d = polygon_distance(rg["poly"])
        else:
            (u0, u1), (v0, v1) = rg["u"], rg["v"]
            d = np.hypot(np.maximum(np.maximum(u0 - UU, UU - u1), 0), np.maximum(np.maximum(v0 - VV, VV - v1), 0))
        bank = math.tan(math.radians(rg["face_deg"])) if "face_deg" in rg else rg.get("bank", 1.0)
        top = b_level + rg["height_above_flag_b"]
        raised = top - bank * d
        if "face_deg" in rg:
            rock_ridges.append(raised > h + 0.5)
        h = np.maximum(h, raised)
    for rr in rock_ridges:
        rock |= rr

    # Pads: flat at the mean natural height of the area, a gently tilted plane fitted to it ("fit": "plane"),
    # or following the lane they widen ("height": "lane:<id>"). Each pad is a function of frame (u, v).
    pad_fn = {}
    pad_ramp_into = {p["area"]: p.get("ramp_into", 0.0) for p in G["pads"]}
    for p in G["pads"]:
        a = areas[p["area"]]
        inside = area_distance(a) == 0
        if p.get("height", "").startswith("lane:"):
            pts_l, s_l, u_l, v_l, prof_l = profiles[p["height"].split(":")[1]]

            def fn(u, v, pts_l=pts_l, s_l=s_l, prof_l=prof_l):
                i = np.argmin((u_l[None, :] - np.ravel(u)[:, None]) ** 2 + (v_l[None, :] - np.ravel(v)[:, None]) ** 2, axis=1)
                return prof_l[i].reshape(np.shape(u))
        elif p.get("fit") == "plane" and "height_above_flag_b" not in p:
            A = np.column_stack([np.ones(inside.sum()), UU[inside] - a["center"][0], VV[inside] - a["center"][1]])
            c0, cu, cv = np.linalg.lstsq(A, natural[inside], rcond=None)[0]
            tilt = math.hypot(cu, cv)
            if tilt > p.get("max_tilt", 0.12):
                cu, cv = cu * p.get("max_tilt", 0.12) / tilt, cv * p.get("max_tilt", 0.12) / tilt

            def fn(u, v, c0=c0, cu=cu, cv=cv, a=a):
                return c0 + cu * (np.asarray(u) - a["center"][0]) + cv * (np.asarray(v) - a["center"][1])
        else:
            m = float(natural[inside].mean())
            if "height_above_flag_b" in p:  # set by hand (relative to the Flag B deck)
                m = b_level + p["height_above_flag_b"]

            def fn(u, v, m=m):
                return np.full(np.shape(u), m)
        pad_fn[p["area"]] = fn

    for p in G["pads"]:
        a = areas[p["area"]]
        d = area_distance(a)
        near = d < 30
        target = np.zeros(UU.shape)
        target[near] = pad_fn[p["area"]](UU[near], VV[near])
        h = np.where(near, apply_feature(h, d, target, 0, BANK), h)
        mask |= d == 0
        surface = np.where(d == 0, target, surface)

    # Connector ramps: from lane to lane, level with every pad they cross (from "ramp_into" metres inside its
    # edge), as equal flights with landings (flights()). They cut into pads, lanes cut into them.
    keepout = np.zeros(UU.shape, bool)  # where placeholder buildings may not stand
    paths = np.zeros(UU.shape, bool)    # lanes, connectors, ramps, bridgeheads (with shoulders): the rim lip keeps off them
    landing_cells = np.zeros(UU.shape, bool)  # fixed connector landings: the ravine bank cut stops at their edge
    for p in G["pads"]:
        keepout |= area_distance(areas[p["area"]]) == 0
    for c in layout["connectors"]:
        if c.get("roof") or c.get("ravine"):
            continue
        lo, hi = c["pair"].split("-")
        pts = connector_path(c)
        s, u, v, cum = stations(pts)
        known = np.full(len(s), np.nan)
        soft = np.zeros(len(s), bool)
        for a_id, fn in pad_fn.items():
            if not a_id.startswith("plateia"):
                depth = pad_ramp_into.get(a_id, 0.0)
                deep = np.array([area_depth(areas[a_id], (uu, vv)) >= depth for uu, vv in zip(u, v)])
                known[deep] = fn(u[deep], v[deep])
                soft |= deep
        # Level with each lane until the ramp leaves the lane's flat width (lanes are applied last and
        # would otherwise leave a steep lip at their edge).
        lane_hw = lambda lid: lanes[lid]["width"] / 2 + LG["shoulder"] + 1.5

        def lane_zone(lid, from_end):
            """Stations, counted from one end, until the connector leaves the lane's flat width."""
            lp = chaikin(lanes[lid]["points"])
            seg = np.full(len(s), np.inf)
            for a, b in zip(lp[:-1], lp[1:]):
                ab = b - a
                tt = np.clip(((u - a[0]) * ab[0] + (v - a[1]) * ab[1]) / float(ab @ ab), 0, 1)
                seg = np.minimum(seg, np.hypot(u - (a[0] + tt * ab[0]), v - (a[1] + tt * ab[1])))
            near = seg <= lane_hw(lid)
            zone = np.zeros(len(s), bool)
            order = range(len(s) - 1, -1, -1) if from_end else range(len(s))
            for i in order:
                if not near[i]:
                    break
                zone[i] = True
            return zone

        z_lo, z_hi = lane_zone(lo, False), lane_zone(hi, True)
        known[z_lo] = lane_height_at(profiles[lo], pts[0])
        known[z_hi] = lane_height_at(profiles[hi], pts[-1])
        soft[z_lo | z_hi] = False
        if not z_hi.any():  # a connector that stops on a pad short of its lower lane (r9: Skeleton yard entrance)
            for a_id, fn in pad_fn.items():
                if area_depth(areas[a_id], (u[-1], v[-1])) > 0:
                    known[-1], soft[-1] = float(fn(u[-1:], v[-1:])[0]), False
        for ld in c.get("landings", []):  # fixed landings, e.g. the East overwatch on the ravine east bank
            lu_, lv_ = ld["at"]
            on = np.hypot(u - lu_, v - lv_) <= ld["size"][0] / 2 + 0.5  # + smoothing allowance
            known[on] = b_level + ld["height_above_flag_b"]
            soft[on] = False
            landing_cells |= np.hypot(UU - lu_, VV - lv_) <= max(ld["size"]) / 2 + 0.75
        prof = flights(s, known, soft=soft, landing=CG.get("overrides", {}).get(c["name"]))
        if REPORT:
            g = np.abs(np.diff(prof)) / 0.5
            free = s[-1] - (z_lo.sum() + z_hi.sum()) * 0.5
            rise_tot = abs(prof[-1] - prof[0])
            n_l = max(int(math.ceil(rise_tot / CG["landing"]["every_rise"] - 1e-6)) - 1, 0)
            need = rise_tot / (CG["max_grade"] * 0.9) + n_l * (CG["landing"]["length"] + 1.5)
            print(f"  connector {c['name']:28s} length {s[-1]:5.1f} m  rise {prof[-1] - prof[0]:+6.2f} m  "
                  f"steepest {math.degrees(math.atan(g.max())):4.1f} deg  run between lanes {free:4.1f} m, "
                  f"needs {need:4.1f} m ({n_l} landing{'s' if n_l != 1 else ''})")
        d, ss = polyline_field(pts)
        target = np.interp(ss, s, prof)
        hw = c["width"] / 2 + CG["shoulder"]
        h = apply_feature(h, d, target, hw, BANK)
        # Connector lips (ground.connector_lips): a rock lip `height` above the path, `width` wide, on the side of
        # the path that faces a point (e.g. Flag B), inside a frame window; it starts at the shoulder edge.
        for lip in G.get("connector_lips", []):
            if lip["connector"] != c["name"]:
                continue
            pu, pv = np.interp(ss, s, u), np.interp(ss, s, v)
            tu_, tv_ = lip["toward"]
            side = ((UU - pu) * (tu_ - pu) + (VV - pv) * (tv_ - pv)) > 0
            (wu0, wu1), (wv0, wv1) = lip["u"], lip["v"]
            win = (UU >= wu0) & (UU <= wu1) & (VV >= wv0) & (VV <= wv1)
            kf = math.tan(math.radians(lip.get("face_deg", 55.0)))
            lip_h = target + lip["height"] - kf * np.maximum(d - hw - lip["width"], 0)
            zone = win & side & (d > hw) & (d <= hw + lip["width"] + 4.0)
            raised = zone & (lip_h > h + 0.05)
            h = np.where(raised, lip_h, h)
            rock |= raised
            keepout |= raised
        mask |= d <= c["width"] / 2
        surface = np.where(d <= c["width"] / 2, target, surface)
        keepout |= d <= hw
        paths |= d <= hw

    for lid, l in lanes.items():
        pts, s, u, v, prof = profiles[lid]
        d, ss = polyline_field(pts)
        target = np.interp(ss, s, prof)
        # Where the lane is cut into rock (cliff, rock ridges) its bank is a near-vertical rock cut.
        bank = np.where(rock, LG.get("rock_bank", BANK), BANK)
        h = apply_feature(h, d, target, l["width"] / 2 + LG["shoulder"], bank)
        mask |= d <= l["width"] / 2
        surface = np.where(d <= l["width"] / 2, target, surface)
        keepout |= d <= l["width"] / 2 + LG["shoulder"]
        paths |= d <= l["width"] / 2 + LG["shoulder"]

    # The ravine: a cut only, through everything (the lanes cross it on placeholder decks). Its floor runs
    # downhill to the south, always a full depth below the ground and below every lane it passes under.
    rv = None
    if RG.get("cut"):
        rv = ravine_floor(natural, profiles)
        rpts, rs, rfloor = rv
        d, ss = polyline_field(rpts)
        floor = np.interp(ss, rs, rfloor)
        half = RAV["walkable_floor"] / 2
        h = np.where(cliff_face | landing_cells, h, np.minimum(h, floor + RG["bank"] * np.maximum(d - half, 0)))  # the gully ends at the cliff
        mask |= (d <= half) & ~cliff_face
        surface = np.where((d <= half) & ~cliff_face, floor, surface)
        keepout |= d <= RAV["width"] / 2
        for r in ravine_ramps(rv, profiles):
            d, ss = polyline_field(r["pts"])
            target = np.interp(ss, r["s"], r["prof"])
            half = RG["ramp_width"] / 2
            h = apply_feature(h, d, target, half + 0.25, BANK)
            mask |= d <= half
            surface = np.where(d <= half, target, surface)
            keepout |= d <= half + 2.5
            paths |= d <= half + 0.75
        bh = RG.get("bridgehead", 0.0)
        for cg in crossing_geom(rv, profiles) if bh > 0 else []:
            for side, hh in ((-1, cg["h0"]), (1, cg["h1"])):  # level bridgeheads at both deck ends
                cu = cg["u"] + side * cg["tu"] * (cg["span"] / 2 + bh / 2 - 0.25)
                cv = cg["v"] + side * cg["tv"] * (cg["span"] / 2 + bh / 2 - 0.25)
                d = rect_distance(cu, cv, cg["tu"], cg["tv"], bh + 0.5, cg["deck_w"])
                h = apply_feature(h, d, hh, 0, BANK)
                mask |= d == 0
                surface = np.where(d == 0, hh, surface)
                keepout |= d <= 1.0
                paths |= d <= 0.5

        # Rim lip (ravine.rim): where a bank top is less than min_above_floor over the floor (v range), a raised
        # rock crest on the rim (`width` = its flat top, 0 = knife edge, so it is never a walkway), with rock faces
        # into the ravine and outwards; kept off every walked path.
        rim_list = RG.get("rim", [])
        for RIM in (rim_list if isinstance(rim_list, list) else [rim_list]):  # one crest per reach
            d, ss = polyline_field(rpts)
            floor = np.interp(ss, rs, rfloor)
            top = floor + RIM["min_above_floor"]
            kf = math.tan(math.radians(RIM["face_deg"]))
            half_top = RAV["width"] / 2
            crest = half_top + RIM.get("crest", 1.5)   # a rock crest (no walkable top) this far out from the centreline
            lip = np.minimum(floor + kf * np.maximum(d - RAV["walkable_floor"] / 2, 0),
                             top - kf * np.maximum(np.abs(d - crest) - RIM["width"] / 2, 0))
            vwin = (VV >= RIM["v"][0]) & (VV <= RIM["v"][1]) & (d > RAV["walkable_floor"] / 2) & ~cliff_face
            blds = np.zeros(UU.shape, bool)   # and off the landmark footprints (they stand on the bank)
            for bd in G.get("buildings", []):
                if not bd.get("outside") and not bd.get("walk_top"):   # a walkable roof may stand at the rim
                    rr_ = math.radians(bd.get("rot", 0.0))
                    blds |= rect_distance(bd["at"][0], bd["at"][1], math.cos(rr_), math.sin(rr_), *bd["size"]) == 0
            free = ~binary_dilation(paths | blds, iterations=int(round(RIM.get("clear", 1.0) / STEP)))
            raise_ = vwin & free & (lip > h + 0.05)
            h = np.where(raise_, lip, h)
            rock |= raise_ & (d >= half_top - 1.0)
            keepout |= raise_ & (d >= half_top - 1.0)
            if REPORT:
                print(f"  ravine rim lip v {RIM['v']}: {raise_.sum() * STEP * STEP:.0f} m2 raised")

    # Only the flat steps are smoothed; risers and banks stay hard edges (they get wall placeholders).
    gz_, gx_ = np.gradient(h, STEP)
    flat_w = np.clip((math.tan(math.radians(30)) - np.hypot(gx_, gz_)) / 0.2, 0, 1)
    flat_w = gaussian_filter(flat_w, 1.0)
    designed = binary_dilation(mask, iterations=2)
    flat_w[designed] = 0.0  # designed surfaces (lanes, ramps, landings, pads) only get the light pass below
    h = h + (gaussian_filter(h, G["final_smooth"] / STEP, mode="nearest") - h) * flat_w
    h = np.where(designed, gaussian_filter(h, 0.35 / STEP, mode="nearest"), h)  # removes 0.5 m raster jitter
    mask &= np.abs(h - surface) < 0.08

    # Back to the real ground outside the frame; the edge ring is the real ground exactly.
    w = np.clip(1 - frame_outside_distance() / G["frame_fade"], 0, 1)
    w = w * w * (3 - 2 * w)
    h = real + (h - real) * w
    mask &= w > 0.5
    ROCK = rock
    return h, mask, profiles, keepout, rv


def area_depth(a, p):
    """How far a frame point lies inside an area (negative outside)."""
    if a["shape"] == "circle":
        return a["radius"] - math.hypot(p[0] - a["center"][0], p[1] - a["center"][1])
    return min(a["size"][0] / 2 - abs(p[0] - a["center"][0]), a["size"][1] / 2 - abs(p[1] - a["center"][1]))


def flag_b_level(h, profiles=None):
    """Flag B stands on the main bridge: its height is the main street's, not the ravine floor under it."""
    if profiles is None:
        return float(sample_uv(h, *points["Flag_B"]))
    return lane_height_at(profiles["main"], points["Flag_B"])


def rel(h, pid, profiles=None):
    return float(sample_uv(h, *points[pid])) - flag_b_level(h, profiles)


# Correction passes so the balanced spawns and flags land on their targets after grading.
for _ in range(4):
    height, mask, profiles, keepout, rv = grade(offsets)
    for i, t in enumerate(bal["targets"], start=1):
        offsets[i] += t["height"] - rel(height, t["id"], profiles)
REPORT = True
FLAG_B_PRINT = lane_height_at(profiles["main"], points["Flag_B"])
height, mask, profiles, keepout, rv = grade(offsets)

# ---------------------------------------------------------------- checks

for a in G["pads"]:
    c = areas[a["area"]]["center"]
    print(f"  pad {a['area']:15s} {float(sample_uv(height, *c)) - flag_b_level(height, profiles):+6.2f} m at its centre")

gz, gx = np.gradient(height, STEP)
slope = np.degrees(np.arctan(np.hypot(gx, gz)))
edge = np.concatenate([height[0] - real[0], height[-1] - real[-1], height[:, 0] - real[:, 0], height[:, -1] - real[:, -1]])
inside = frame_outside_distance() == 0
print(f"tile x {X0:.2f}..{X0 + SIZE:.2f}  z {Z0:.2f}..{Z0 + SIZE:.2f}  size {SIZE:.2f} m  step {STEP:.3f} m")
print(f"village hole cells x {first_x}..{first_x + CELLS}  z {first_z}..{first_z + CELLS}")
print(f"edge vs real ground: max |diff| {np.abs(edge).max():.4f} m")
B = flag_b_level(height, profiles)
print(f"Flag B at {B:.2f} m")
for pid in ["Spawn_West", "Spawn_East", "Flag_A", "Flag_C"]:
    print(f"  {pid:11s} {rel(height, pid, profiles):+6.2f} m (real {rel(base, pid):+6.2f})")
for lid in lanes:
    pts = chaikin(lanes[lid]["points"])
    s, u, v, _ = stations(pts)
    print(f"  {lid:6s} lane at frame ends {float(sample_uv(height, u[0], v[0])) - B:+6.2f} .. {float(sample_uv(height, u[-1], v[-1])) - B:+6.2f} m")
core = binary_erosion(mask, iterations=2) & inside
print(f"walkable surfaces (lanes, connectors, pads): max slope {slope[core].max():.1f} deg, "
      f"{(slope[core] > WALKABLE_DEG).mean() * 100:.2f}% over {WALKABLE_DEG:.0f} deg")
print(f"whole frame: {(slope[inside] <= WALKABLE_DEG).mean() * 100:.0f}% walkable, max |graded - real| {np.abs(height - real)[inside].max():.1f} m")

# ---------------------------------------------------------------- output

OUT.mkdir(parents=True, exist_ok=True)
lo_h = math.floor(height.min()) - 10.0
hi_h = math.ceil(height.max()) + 10.0
q = np.round((height - lo_h) / (hi_h - lo_h) * 65535).astype("<u2")
q.tofile(OUT / "play_513.r16")
mask_img = Image.fromarray((mask[::-1, :] * 255).astype(np.uint8)).resize((RES - 1, RES - 1), Image.BILINEAR)
mask_img.save(OUT / "play_mask.png")


def stake(name, kind, u, v):
    x, z = to_world(u, v)
    return {"name": name, "kind": kind, "x": float(x), "y": float(sample(height, x, z)), "z": float(z)}


stakes = [stake(s["id"], "spawn_west" if "West" in s["id"] else "spawn_east", *s["at"]) for s in layout["spawns"]]
stakes += [stake(f["id"], "flag", *f["at"]) for f in layout["flags"]]
for st in stakes:
    if st["name"] == "Flag_B":
        st["y"] = B  # on the bridge deck
for side, pts in layout["initial_spawns"].items():
    stakes += [stake(f"Start_{side}_{i + 1}", "start_" + side, *p) for i, p in enumerate(pts)]

posts = []
corners = [(-HALF_U, -HALF_V), (HALF_U, -HALF_V), (HALF_U, HALF_V), (-HALF_U, HALF_V)]
for (au, av), (bu, bv) in zip(corners, corners[1:] + corners[:1]):
    L = math.hypot(bu - au, bv - av)
    n = int(round(L / POST_SPACING))
    for i in range(n):
        posts.append(stake(f"Post_{len(posts):03d}", "corner" if i == 0 else "post",
                           au + (bu - au) * i / n, av + (bv - av) * i / n))

# ---------------------------------------------------------------- review placeholders (EditorOnly in Unity)
# Oriented boxes in world space: centre, size (x = width, y = height, z = length along yaw) and yaw/pitch in
# degrees, Unity convention (yaw from +Z towards +X, positive pitch tips the forward end down).

boxes, labels = [], []
occupied = keepout | ROCK
inside_tile_frame = frame_outside_distance() == 0
B_TOP = {}  # top height of placeholders by name, for the labels


def world_yaw(du, dv):
    dx, dz = du * COS - dv * SIN, du * SIN + dv * COS
    return math.degrees(math.atan2(dx, dz))


def box(name, group, mat, cu, cv, y_bottom, y_top, length, width, du, dv, pitch=0.0):
    x, z = to_world(cu, cv)
    boxes.append({"name": name, "group": group, "mat": mat, "x": float(x), "y": float((y_bottom + y_top) / 2), "z": float(z),
                  "sx": float(width), "sy": float(y_top - y_bottom), "sz": float(length),
                  "yaw": float(world_yaw(du, dv)), "pitch": float(pitch)})


def footprint(cu, cv, length, width, du, dv):
    n = math.hypot(du, dv)
    du, dv = du / n, dv / n
    # Only a window around the rectangle can be inside it (its half-diagonal plus a 2-cell margin); every cell in the
    # window gets exactly the same test as before, every cell outside stays False.
    x, z = to_world(cu, cv)
    r = math.hypot(length, width) / 2 + 2 * STEP
    c0, c1 = max(int(math.floor((x - r - X0) / STEP)), 0), min(int(math.ceil((x + r - X0) / STEP)) + 1, RES)
    r0, r1 = max(int(math.floor((z - r - Z0) / STEP)), 0), min(int(math.ceil((z + r - Z0) / STEP)) + 1, RES)
    out = np.zeros(UU.shape, bool)
    if c0 < c1 and r0 < r1:
        U, V = UU[r0:r1, c0:c1], VV[r0:r1, c0:c1]
        a = (U - cu) * du + (V - cv) * dv
        b = -(U - cu) * dv + (V - cv) * du
        out[r0:r1, c0:c1] = (np.abs(a) <= length / 2) & (np.abs(b) <= width / 2)
    return out


def inside_frame(cu, cv, length, width, du, dv):
    n = math.hypot(du, dv)
    du, dv = du / n, dv / n
    for sa in (-1, 1):
        for sb in (-1, 1):
            pu = cu + sa * du * length / 2 - sb * dv * width / 2
            pv = cv + sa * dv * length / 2 + sb * du * width / 2
            if abs(pu) > HALF_U or abs(pv) > HALF_V:
                return False
    return True


def label(text, sub, kind, u, v, y, size):
    x, z = to_world(u, v)
    labels.append({"text": text, "sub": sub, "kind": kind, "x": float(x), "y": float(y), "z": float(z), "size": float(size)})


def ground_stats(fp):
    g = height[fp]
    return float(g.min()), float(g.max()), float(np.median(g))


# Bridge decks over the ravine at each lane crossing, following the lane's slope, with low parapets.
crossings, ramps = [], []
if rv is not None:
    rpts, rs, rfloor = rv
    _, ru, rvv, _ = stations(rpts)
    top_half = RAV["width"] / 2
    for cg in crossing_geom(rv, profiles):
        cr, tu, tv, span, deck_w, hc = cg["cr"], cg["tu"], cg["tv"], cg["span"], cg["deck_w"], cg["hc"]
        cu, cv = cg["u"], cg["v"]
        pitch = math.degrees(math.atan2(cg["h0"] - cg["h1"], span))
        box(cr["name"] + " deck", "Bridges", "bridge", cu, cv, hc - 0.4, hc, span, deck_w, tu, tv, pitch)
        occupied |= footprint(cu, cv, span + 4 + 2 * RG.get("bridgehead", 0.0), deck_w + 4, tu, tv)
        for side in (-1, 1):
            pu, pv = cu - side * tv * (deck_w / 2 - 0.125), cv + side * tu * (deck_w / 2 - 0.125)
            box(cr["name"] + (" parapet N" if side > 0 else " parapet S"), "Bridges", "bridge",
                pu, pv, hc - 0.4, hc + 1.0, RAV["width"], 0.25, tu, tv, pitch)
        crossings.append({"name": cr["name"], "lane": cr["lane"], "u": cu, "v": cv, "h": hc,
                          "tu": tu, "tv": tv, "rj": cg["rj"], "deck_w": deck_w})
        if cr["lane"] == "main":  # Flag B stands at the centre of the main bridge deck, so its circle sits on the deck
            fbx, fbz = to_world(cu, cv)
            fb_off = math.hypot(cu - points["Flag_B"][0], cv - points["Flag_B"][1])
            for st in stakes:
                if st["name"] == "Flag_B":
                    st["x"], st["z"], st["y"] = float(fbx), float(fbz), hc
            print(f"  Flag B moved {fb_off:.1f} m onto the main deck centre (frame {cu:.1f}, {cv:.1f})")

    # Ramps down to the floor are cut into the terrain (ravine_ramps); keep their approaches open.
    for r in ravine_ramps(rv, profiles):
        (su, sv), (tu_, tv_), (bu, bv) = r["pts"][:3]
        occupied |= footprint((tu_ + bu) / 2, (tv_ + bv) / 2, r["run"] + 6, RG["ramp_width"] + 6, bu - tu_, bv - tv_)
        ramps.append(r)
        print("    ramp profile:", " ".join(f"{x - B:+.1f}" for x in r["prof"][::2]))
        _s, _u, _v, _ = stations(r["pts"])
        print("    ramp terrain:", " ".join(f"{x - B:+.1f}" for x in sample_uv(height, _u, _v)[::2]))
        print(f"  ramp from {r['name']}: {r['run']:.1f} m run, {r['drop']:.2f} m drop, "
              f"{math.degrees(math.atan2(r['drop'], r['run'])):.1f} deg")
    # Rock arch: the ravine passes under the B ridge (an overhead block with headroom), closing the view
    # straight down the channel from the Upper Road onto Zone B.
    arch = RG.get("arch")
    if arch:
        sel = np.flatnonzero((rvv >= arch["v"][0]) & (rvv <= arch["v"][1]))
        j0, j1 = int(sel.min()), int(sel.max())
        jm = (j0 + j1) // 2
        du, dv = ru[j1] - ru[j0], rvv[j1] - rvv[j0]
        length = math.hypot(du, dv)
        bottom = float(rfloor[j0:j1 + 1].max()) + arch["headroom"]
        box("Rock arch over the ravine", "Rocks", "rock", ru[jm], rvv[jm], bottom, B + arch["top_above_flag_b"],
            length, arch.get("width", RAV["width"] + 2.0), du, dv)
        boxes[-1]["overhead"] = True  # walkable underneath
        occupied |= footprint(ru[jm], rvv[jm], length + 2, arch.get("width", RAV["width"] + 2.0) + 2, du, dv)
        print(f"  rock arch: {length:.1f} m over the ravine, headroom {arch['headroom']} m, top {arch['top_above_flag_b']:+.1f} m")
    print(f"  ravine floor: {rfloor[0] - B:+.1f} .. {rfloor[-1] - B:+.1f} m vs Flag B, steepest "
          f"{math.degrees(math.atan(np.abs(np.diff(rfloor)).max() / 0.5)):.1f} deg")

# Garden walls (ground.garden_walls): EditorOnly wall strips along a polyline, a set height above the ground at
# their foot, with gaps wherever a connector or lane passes.
for gw in G.get("garden_walls", []):
    pts = np.asarray(gw["points"], float)
    s, u, v, _ = stations(pts, 0.5)
    open_ = np.zeros(len(s), bool)
    for c in layout["connectors"]:
        if c.get("roof") or c.get("ravine"):
            continue
        cp = connector_path(c)
        for a, b in zip(cp[:-1], cp[1:]):
            ab = b - a
            tt = np.clip(((u - a[0]) * ab[0] + (v - a[1]) * ab[1]) / float(ab @ ab), 0, 1)
            open_ |= np.hypot(u - (a[0] + tt * ab[0]), v - (a[1] + tt * ab[1])) <= c["width"] / 2 + CG["shoulder"] + gw.get("gap_clear", 0.5)
    k = 0
    i = 0
    while i < len(s):
        if open_[i]:
            i += 1
            continue
        j = i
        while j + 1 < len(s) and not open_[j + 1]:
            j += 1
        if s[j] - s[i] >= 1.0:
            gh = sample_uv(height, u[i:j + 1], v[i:j + 1])
            k += 1
            box(f"{gw['name']} {k}", "Walls", "wall", (u[i] + u[j]) / 2, (v[i] + v[j]) / 2, float(gh.min()) - 0.3,
                float(gh.min()) + gw["height"], s[j] - s[i] + 0.5, gw.get("thickness", 0.4), u[j] - u[i], v[j] - v[i])
        i = j + 1
    print(f"  garden wall {gw['name']}: {k} pieces, {gw['height']} m high")

# Landing walls (connectors[].landings[].wall): a low EditorOnly wall on the landing edge that faces a point,
# e.g. the East overwatch's south-east edge toward Flag C (cover and a sight blocker, the landing keeps its view of B).
for c in layout["connectors"]:
    for ld in c.get("landings", []):
        wl = ld.get("wall")
        if not wl:
            continue
        lu_, lv_ = ld["at"]
        du, dv = wl["toward"][0] - lu_, wl["toward"][1] - lv_
        n = math.hypot(du, dv)
        du, dv = du / n, dv / n
        off = wl.get("offset", max(ld["size"]) / 2 + 0.2)
        cu, cv = lu_ + du * off, lv_ + dv * off
        top = B + ld["height_above_flag_b"]
        box(f"{ld['name']} wall", "Walls", "wall", cu, cv, top - 0.6, top + wl["height"], wl.get("thickness", 0.4),
            wl.get("length", max(ld["size"])), du, dv)
        print(f"  landing wall {ld['name']}: {wl['height']} m toward {wl['toward']}")

# Landmark buildings (ground.buildings).
for b in G["buildings"]:
    cu, cv = b["at"]
    rot = math.radians(b.get("rot", 0.0))
    du, dv = math.cos(rot), math.sin(rot)
    L, W = b["size"]
    fp = footprint(cu, cv, L, W, du, dv)
    allowed = np.zeros(UU.shape, bool)
    if "pad" in b:
        allowed = area_distance(areas[b["pad"]]) == 0
    clash = fp & occupied & ~allowed
    if clash.any() and not b.get("outside"):
        print(f"  WARNING {b['name']}: overlaps a lane, ramp, pad or the ravine ({clash.sum() * STEP * STEP:.1f} m2)")
    gmin, gmax, gmed = ground_stats(fp)
    top = B + b["top_above_flag_b"] if "top_above_flag_b" in b else max(gmed + b["height"], gmax + 1.0)
    box(b["name"], "Buildings", "landmark_" + b["label"].lower(), cu, cv, gmin - 0.3, top, L, W, du, dv)
    if b.get("walk_top"):
        boxes[-1]["walk_top"] = True  # a roof players stand on (the checks count it as walkable)
    occupied |= fp
    B_TOP[b["name"]] = top
    label(b["name"], b["label"], b["label"], cu, cv, top + 2.5, 1.3)

# Rooftop route: a chain of blocks whose roofs step down from the main street to the lower lane.
for c in layout["connectors"]:
    if not c.get("roof"):
        continue
    pts = np.asarray(c["points"], float)
    s, u, v, cum = stations(pts)
    lo, hi = c["pair"].split("-")
    h0, h1 = lane_height_at(profiles[lo], pts[0]), lane_height_at(profiles[hi], pts[-1])
    k = 0
    for st in np.arange(6.0, s[-1] - 4.0, 6.0):
        j = int(st / 0.5)
        a, b2 = max(j - 4, 0), min(j + 4, len(s) - 1)
        fp = footprint(u[j], v[j], 6.0, c["width"] + 3, u[b2] - u[a], v[b2] - v[a])
        if (fp & occupied).any():
            continue
        gmin, gmax, _ = ground_stats(fp)
        roof = max(h0 + (h1 - h0) * st / s[-1] + 3.0, gmax + 2.5)
        k += 1
        box(f"{c['name']} block {k}", "Houses", "house", u[j], v[j], gmin - 0.3, roof,
            6.0, c["width"] + 3, u[b2] - u[a], v[b2] - v[a])
        B_TOP[f"{c['name']} block {k}"] = roof
        occupied |= fp

# House rows along the lanes, both sides: a street wall of plain blocks, seeded, leaving every connector,
# pad and the ravine open.
HR = G["house_rows"]
rng = np.random.default_rng(HR["seed"])
st_n, st_p = zip(*HR["storeys"])
houses = 0


def grid_at(field, u, v):
    x, z = to_world(np.asarray(u, float), np.asarray(v, float))
    r = np.clip(np.round((z - Z0) / STEP).astype(int), 0, RES - 1)
    c = np.clip(np.round((x - X0) / STEP).astype(int), 0, RES - 1)
    return field[r, c]


# Fill blocks (house_rows.fills): a frame rectangle filled with plain house blocks, each clipped 1 m clear of
# every lane, connector, pad, rock face and block already placed, or right against their shoulders
# (house_rows.fill_clear, default 0 m; they close sight lines between the lanes).
# Greedy: the largest free rectangle first (at least 2.5 m each way), at most house_rows.width[1] long, repeated.
def largest_free_rect(free):
    """Largest all-True rectangle in a 2D bool array: (area, r0, r1, c0, c1), inclusive."""
    best = (0, 0, -1, 0, -1)
    hist = np.zeros(free.shape[1], int)
    for r in range(free.shape[0]):
        hist = np.where(free[r], hist + 1, 0)
        stack = []
        for c in range(len(hist) + 1):
            hc = hist[c] if c < len(hist) else 0
            start = c
            while stack and stack[-1][1] >= hc:
                start, hh = stack.pop()
                area = hh * (c - start)
                if area > best[0]:
                    best = (area, r - hh + 1, r, start, c - 1)
            stack.append((start, hc))
    return best


for fl in HR.get("fills", []):
    (fu0, fu1), (fv0, fv1) = fl["u"], fl["v"]
    n_clear = int(round(fl.get("clear", HR.get("fill_clear", 0.0)) / STEP))
    clear = ~binary_dilation(occupied, iterations=n_clear) if n_clear > 0 else ~occupied  # keepout already holds the shoulders
    gu = np.arange(fu0, fu1 + 1e-6, 0.5)
    gv = np.arange(fv0, fv1 + 1e-6, 0.5)
    GU, GV = np.meshgrid(gu, gv)
    free = grid_at(clear, GU, GV)
    if os.environ.get("DEBUG_FILL") == fl["name"]:
        for name_, fld in (("keepout", keepout), ("rock", ROCK), ("occupied", occupied)):
            print(name_)
            for row in grid_at(fld, GU, GV)[::-2]:
                print("   " + "".join("#" if x else "." for x in row[::2]))
    gap = int(round(np.mean(HR["gap"]) / 0.5))
    k = 0
    while True:
        area, r0, r1, c0, c1 = largest_free_rect(free)
        nv, nu = r1 - r0, c1 - c0
        if nv * 0.5 < 2.5 or nu * 0.5 < 2.5:
            break
        long_u = nu >= nv
        n_long = nu if long_u else nv
        pieces = max(int(math.ceil(n_long * 0.5 / HR["width"][1])), 1)
        cuts = np.linspace(0, n_long, pieces + 1).round().astype(int)
        for q0, q1 in zip(cuts[:-1], cuts[1:]):
            q1 = q1 - (gap if q1 < n_long else 0)
            if long_u:
                ua, ub, va, vb = gu[c0 + q0], gu[c0 + q1], gv[r0], gv[r1]
            else:
                ua, ub, va, vb = gu[c0], gu[c1], gv[r0 + q0], gv[r0 + q1]
            if ub - ua < 2.5 or vb - va < 2.5:
                continue
            cu, cv = (ua + ub) / 2, (va + vb) / 2
            fp = footprint(cu, cv, vb - va, ub - ua, 0.0, 1.0)
            gmin, gmax, gmed = ground_stats(fp)
            top = max(gmed + fl["height"], gmax + 2.5)
            k += 1
            houses += 1
            box(f"{fl['name']} {k}", "Houses", "house", cu, cv, gmin - 0.3, top, vb - va, ub - ua, 0.0, 1.0)
            occupied |= fp
        free[max(r0 - gap, 0):r1 + gap + 1, max(c0 - gap, 0):c1 + gap + 1] = False
    print(f"  fill {fl['name']}: {k} blocks")
for lid in HR["lanes"]:
    l = lanes[lid]
    pts = chaikin(l["points"])
    s, u, v, _ = stations(pts)
    prof = profiles[lid]
    for side in (1, -1):
        i = 0
        while i < len(s) - 2:
            a, b2 = max(i - 2, 0), min(i + 2, len(s) - 1)
            tu, tv = u[b2] - u[a], v[b2] - v[a]
            n = math.hypot(tu, tv)
            tu, tv = tu / n, tv / n
            nu, nv = -tv * side, tu * side                 # side +1 = uphill (north) of the lane
            dpt = rng.uniform(*HR["depth"])
            off = l["width"] / 2 + LG["shoulder"] + HR["setback"] + dpt / 2
            placed = False
            for w in (rng.uniform(*HR["width"]), HR.get("min_width", HR["width"][0]) + 1.0, HR.get("min_width", HR["width"][0])):
                j = min(i + int(w / 2 / 0.5), len(s) - 1)
                cu, cv = u[j] + nu * off, v[j] + nv * off
                fp = footprint(cu, cv, w + 2 * HR["margin"], dpt + 2 * HR["margin"], tu, tv)
                if inside_frame(cu, cv, w, dpt, tu, tv) and not (fp & occupied).any():
                    placed = True
                    break
            if placed:
                fp = footprint(cu, cv, w, dpt, tu, tv)
                storeys = int(rng.choice(st_n, p=st_p))
                street = lane_height_at(prof, (u[j], v[j]))
                gmin, gmax, _ = ground_stats(fp)
                top = max(street + storeys * HR["storey"], gmax + 2.5)
                houses += 1
                box(f"House {lid} {'N' if side > 0 else 'S'} {houses:03d}", "Houses", "house", cu, cv,
                    gmin - 0.3, top, w, dpt, tu, tv)
                occupied |= fp
                i += int((w + rng.uniform(*HR["gap"])) / 0.5)
            else:
                i += 2
print(f"  placeholders: {len(G['buildings'])} landmark blocks, {houses} houses, {len(crossings)} bridges, {len(ramps)} ramps")


# Retaining-wall strips along the terrace risers (1 m or taller), so the steps read as walls.
T = G["terraces"]
wall_step = min(T["step"], T.get("max_riser", T["step"]))
if wall_step >= 1.0:
    import contourpy
    gzz, gxx = np.gradient(height, STEP)
    steep = np.degrees(np.arctan(np.hypot(gxx, gzz))) > 35.0
    near_feature = binary_dilation(keepout | mask | ROCK, iterations=4)
    cg = contourpy.contour_generator(xs, zs, height)
    walls = 0
    for k in range(int(math.floor(height[inside_tile_frame].min() / wall_step)), int(math.ceil(height[inside_tile_frame].max() / wall_step)) + 1):
        level = (k + 0.5) * wall_step
        for line in cg.lines(level):
            seg_len = np.hypot(*np.diff(line, axis=0).T)
            cum = np.concatenate([[0], np.cumsum(seg_len)])
            if cum[-1] < 2.0:
                continue
            cuts = np.arange(0, cum[-1], 3.0)
            for a0, a1 in zip(cuts, np.append(cuts[1:], cum[-1])):
                if a1 - a0 < 1.0:
                    continue
                pa = np.array([np.interp(a0, cum, line[:, 0]), np.interp(a0, cum, line[:, 1])])
                pb = np.array([np.interp(a1, cum, line[:, 0]), np.interp(a1, cum, line[:, 1])])
                mx, mz = (pa + pb) / 2
                r_, c_ = int(round((mz - Z0) / STEP)), int(round((mx - X0) / STEP))
                if not (0 <= r_ < RES and 0 <= c_ < RES) or not steep[r_, c_] or near_feature[r_, c_] or not inside_tile_frame[r_, c_]:
                    continue
                ua, va = to_frame(*pa)
                ub, vb = to_frame(*pb)
                cu_, cv_ = (ua + ub) / 2, (va + vb) / 2
                box(f"Terrace wall {walls + 1:03d}", "Walls", "wall", cu_, cv_, k * wall_step - 0.3,
                    (k + 1) * wall_step + 0.2, float(np.hypot(ub - ua, vb - va)) + 0.3, 0.4, ub - ua, vb - va)
                walls += 1
    print(f"  terrace walls: {walls} strips")


# Name labels over every place (deduplicated), coloured by kind.
def ground_y(u, v):
    return float(sample_uv(height, u, v))


def add_label(text, sub, kind, u, v, size, lift=3.5, y=None):
    lt = text.lower()
    for o in labels:
        ou, ov = to_frame(o["x"], o["z"])
        if lt in o["text"].lower() or o["text"].lower() in lt or math.hypot(ou - u, ov - v) < 9:
            return
    label(text, sub, kind, u, v, (ground_y(u, v) + lift) if y is None else y, size)


for f in layout["flags"]:
    y = (B + 6.0) if f["id"] == "Flag_B" else ground_y(*f["at"]) + 6.5
    label(f"{f['id'].replace('_', ' ')} - {f['callout']}", "", "flag", *f["at"], y, 1.6)
for sp in layout["spawns"]:
    label(f"{sp['id'].replace('_', ' ')} - {sp['callout']}", "", "west" if "West" in sp["id"] else "east",
          *sp["at"], ground_y(*sp["at"]) + 5.5, 1.6)
for cr in crossings:
    label(cr["name"], "temporary bridge", "bridge", cr["u"], cr["v"], cr["h"] + 3.0, 1.1)
for lm in layout["landmarks"]:
    add_label(lm["name"], lm["label"], lm["label"], *lm["at"], 1.3)
for c in layout["connectors"]:
    pts = np.asarray(c["points"], float)
    s, u, v, _ = stations(pts)
    j = len(s) // 2
    roof_y = max([B_TOP[k] for k in B_TOP if k.startswith(c["name"] + " block")], default=None)
    add_label(c["name"], "roof route" if c.get("roof") else "connector", "connector", u[j], v[j], 0.9,
              y=(roof_y + 2.5) if (c.get("roof") and roof_y is not None) else None)
for a in layout["areas"]:
    add_label(a["name"], "area", "area", *a["center"], 1.1)
for c in layout["callouts"]:
    if c.get("kind") != "lane":
        add_label(c["name"].title() if c["name"].isupper() else c["name"], "", "area", *c["at"], 1.1)
def free_spots(u, v, count, spacing, clear=12.0):
    """Up to `count` stations along a line, `spacing` apart, at least `clear` from every label so far."""
    taken = [to_frame(o["x"], o["z"]) for o in labels]
    picked = []
    for j in range(len(u)):
        if abs(u[j]) > HALF_U - 5 or abs(v[j]) > HALF_V - 3:
            continue
        if any(math.hypot(u[j] - a, v[j] - b) < clear for a, b in taken):
            continue
        if any(math.hypot(u[j] - u[k], v[j] - v[k]) < spacing for k in picked):
            continue
        picked.append(j)
        if len(picked) == count:
            break
    return picked


for l in layout["lanes"]:
    pts = chaikin(l["points"])
    s, u, v, _ = stations(pts)
    name, _, sub = l["name"].partition(" - ")
    for j in free_spots(u, v, 3, 40.0):
        label(name.upper(), sub, "lane", u[j], v[j], ground_y(u[j], v[j]) + 4.5, 2.0)
if rv is not None:
    for j in free_spots(ru, rvv, 3, 30.0):
        label("THE RAVINE", "", "ravine", ru[j], rvv[j], float(rfloor[j]) + RAV["depth"] + 2.5, 2.0)
for c in layout["cordon"]:
    label("Cordon: " + c["name"], "police roadblock (outside)", "cordon", *c["at"], ground_y(*c["at"]) + 4.0, 1.3)
print(f"  labels: {len(labels)}")


def route(name, pts, spacing=3.0):
    s, u, v, _ = stations(np.asarray(pts, float), spacing)
    if s[-1] < stations(np.asarray(pts, float), 0.5)[0][-1]:
        u, v = np.append(u, pts[-1][0]), np.append(v, pts[-1][1])
    x, z = to_world(u, v)
    return {"name": name, "xs": [round(float(a), 3) for a in x], "zs": [round(float(b), 3) for b in z]}


# Walk-test routes: every lane inside the frame and every ground connector, end to end.
routes = [route(l["name"], [p for p in chaikin(l["points"])]) for l in layout["lanes"]]
routes += [route(c["name"], connector_path(c), 1.0) for c in layout["connectors"] if not (c.get("roof") or c.get("ravine"))]
if len(ramps) == 2:
    # Ravine floor: main street east of the bridge -> ramp -> along the floor -> ramp -> lower lane.
    rm, rl = (ramps[0], ramps[1]) if ramps[0]["lane"] == "main" else (ramps[1], ramps[0])
    def entry(r):
        """From the lane centre beside the bridge, along the level shelf, then down the ramp."""
        (lu_, lv_), (tu, tv) = r["lane_pt"], r["lane_t"]
        n = math.hypot(tu, tv)
        east = 1 if tu > 0 else -1
        lane_east = (lu_ + east * tu / n * (RAV["width"] / 2 + 3), lv_ + east * tv / n * (RAV["width"] / 2 + 3))
        return [lane_east] + [tuple(p) for p in r["pts"][:3]]

    pts = entry(rm)
    for j in range(rm["jb"] + 6, rl["jb"] - 5, 6):
        pts.append((ru[j], rvv[j]))
    pts += entry(rl)[::-1]
    routes.append(route("Ravine floor (ramps and floor)", [list(map(float, p)) for p in pts]))

meta = {
    "file": "play_513.r16", "mask": "play_mask.png",
    "resolution": RES, "size": SIZE, "x0": X0, "z0": Z0, "base_height": lo_h, "top_height": hi_h,
    "village_first_x": first_x, "village_first_z": first_z, "village_cells": CELLS,
    "frame_center": [CX, CZ], "frame_rotation_deg": frame["rotation_deg"], "frame_size": frame["size"],
    "flag_b_height": B, "stakes": stakes, "posts": posts, "routes": routes, "boxes": boxes, "labels": labels,
}
(OUT / "play_meta.json").write_text(json.dumps(meta, indent=1))
print(f"wrote {OUT / 'play_513.r16'}, mask, meta ({len(stakes)} stakes, {len(posts)} posts), heights {lo_h}..{hi_h} m")

# ---------------------------------------------------------------- review sheets (frame view, uphill up)

PPM = 5
VU, VV_ = (-HALF_U - 20, HALF_U + 20), (-HALF_V - 20, HALF_V + 20)
W, H = int((VU[1] - VU[0]) * PPM), int((VV_[1] - VV_[0]) * PPM)
us = VU[0] + (np.arange(W) + 0.5) / PPM
vs = VV_[1] - (np.arange(H) + 0.5) / PPM
pu, pv = np.meshgrid(us, vs)
hv = sample_uv(height, pu, pv)
sv = sample_uv(slope, pu, pv)
mv = sample_uv(mask.astype(float), pu, pv)

gv, gu = np.gradient(hv, -1.0 / PPM, 1.0 / PPM)
dx_w = gu * COS - gv * SIN
dz_w = gu * SIN + gv * COS
az, el = math.radians(SUN_AZIMUTH), math.radians(SUN_ELEVATION)
lx, lz, ly = math.sin(az) * math.cos(el), math.cos(az) * math.cos(el), math.sin(el)
shade = np.clip((-dx_w * lx - dz_w * lz + ly) / np.sqrt(dx_w ** 2 + dz_w ** 2 + 1), 0, 1)
ambient = np.clip(1 - 0.5 * np.hypot(gu, gv), 0.3, 1)


def contour(h, stepm):
    b = np.floor(h / stepm)
    e = np.zeros(h.shape, bool)
    e[:-1] |= b[:-1] != b[1:]
    e[:, :-1] |= b[:, :-1] != b[:, 1:]
    return e


def to_px(u, v):
    return (u - VU[0]) * PPM, (VV_[1] - v) * PPM


def font(size, bold=False):
    try:
        return ImageFont.truetype("C:/Windows/Fonts/" + ("arialbd.ttf" if bold else "arial.ttf"), size)
    except OSError:
        return ImageFont.load_default()


BOX_COLORS = {"house": (235, 235, 235), "bridge": (80, 220, 255), "landmark_real": (70, 200, 90),
              "landmark_adapted": (245, 180, 40), "landmark_invented": (190, 110, 255)}


def finish(img, title):
    d = ImageDraw.Draw(img)
    for b in boxes:  # placeholder footprints
        y = math.radians(b["yaw"])
        fx, fz = math.sin(y), math.cos(y)
        rx, rz = math.cos(y), -math.sin(y)
        corner = []
        for sf, sr in ((1, 1), (1, -1), (-1, -1), (-1, 1)):
            wx = b["x"] + fx * b["sz"] / 2 * sf + rx * b["sx"] / 2 * sr
            wz = b["z"] + fz * b["sz"] / 2 * sf + rz * b["sx"] / 2 * sr
            corner.append(to_px(*to_frame(wx, wz)))
        d.polygon(corner, outline=BOX_COLORS.get(b["mat"], (255, 255, 255)), width=2)
    fu = [to_px(*c) for c in corners]
    d.line(fu + fu[:1], fill=(255, 255, 255), width=3)
    for pid, col in [("Spawn_West", (60, 200, 255)), ("Spawn_East", (255, 90, 170)),
                     ("Flag_A", (255, 220, 60)), ("Flag_B", (255, 220, 60)), ("Flag_C", (255, 220, 60))]:
        x, y = to_px(*points[pid])
        d.ellipse([x - 9, y - 9, x + 9, y + 9], outline=col, width=4)
        label = pid.replace("_", " ") + (f"  {rel(height, pid, profiles):+.1f} m" if pid != "Flag_B" else f"  {B:.1f} m asl")
        d.text((x + 13, y - 11), label, fill=(255, 255, 255), font=font(20, True), stroke_width=3, stroke_fill=(0, 0, 0))
    d.text((14, 10), title, fill=(255, 255, 255), font=font(26, True), stroke_width=3, stroke_fill=(0, 0, 0))
    return img


light = 0.35 + 0.65 * shade * 0.8 + 0.2 * ambient
hn = (hv - hv.min()) / (hv.max() - hv.min())
col = np.stack([150 + 70 * hn, 140 + 50 * hn, 120 + 20 * hn], -1) * light[..., None]
col = np.where(mv[..., None] > 0.5, col * 0.72 + np.array([40, 34, 26]) * 0.28, col)
col[contour(hv, 1.0)] *= 0.8
col[contour(hv, 5.0)] = (40, 30, 20)
SHOTS.mkdir(exist_ok=True)
finish(Image.fromarray(np.clip(col, 0, 255).astype(np.uint8)),
       "Task 7 play ground - hillshade (sun from the west), 1 m / 5 m contours, darker = lanes, ramps, pads").save(SHOTS / "task07_ground_plan.png")

sc = np.zeros((H, W, 3))
sc[...] = (90, 170, 90)
sc[sv > 20] = (220, 200, 80)
sc[sv > WALKABLE_DEG] = (210, 70, 60)
sc = sc * (0.55 + 0.45 * shade[..., None])
sc[(mv > 0.5) & (sv <= WALKABLE_DEG)] = sc[(mv > 0.5) & (sv <= WALKABLE_DEG)] * 0.6 + np.array([255, 255, 255]) * 0.4
sc[(mv > 0.5) & (sv > WALKABLE_DEG)] = (255, 0, 255)  # a walkable surface that is too steep: must not happen
finish(Image.fromarray(np.clip(sc, 0, 255).astype(np.uint8)),
       "Task 7 slope: green <= 20 deg, yellow <= 36 deg (walkable), red steeper; light = lanes/ramps/pads, magenta = too steep on them").save(SHOTS / "task07_ground_slope.png")
print("wrote Screenshots/task07_ground_plan.png, task07_ground_slope.png")
