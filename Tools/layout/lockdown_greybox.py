"""Greybox for LOCKDOWN (Tasks 8-14): grey layout blocks on the Task 7 play ground, from the layout data.

Run from the project root, after lockdown_playground.py:
    uv run --with numpy --with scipy --with pillow Tools/layout/lockdown_greybox.py

Reads Assets/Lockdown/Data/Layout/lockdown_layout.json ("greybox" block, one section per task) and the play ground
(SourceData/playground/play_513.r16, play_mask.png, play_meta.json), and writes
    SourceData/playground/greybox_meta.json   boxes for Lockdown/Build/Greybox, the placeholders they replace,
                                              walk-test routes
and a review sheet Screenshots/task08/greybox_plan.png (never Assets/).

Every section has its own random stream (its seed), so a later section never changes an earlier one. Within a
section the order is: landmarks -> fills -> street rows -> street walls -> strips and posts. Blocks keep clear of
every lane, connector, pad, the ravine and every placeholder that stays.
Box format = play_meta.json's (world centre, size x = width, y = height, z = length, yaw/pitch in degrees, Unity
convention), plus section, shape (cube | cylinder), collider, walk_top, overhead (sight blocker, walkable under) and
rail (see-through for the sight-line checks).
"""
import json
import math
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont
from scipy.ndimage import binary_dilation, map_coordinates

ROOT = Path(__file__).resolve().parents[2]
LAYOUT = json.loads((ROOT / "Assets" / "Lockdown" / "Data" / "Layout" / "lockdown_layout.json").read_text(encoding="utf-8"))
PG = ROOT / "SourceData" / "playground"
META = json.loads((PG / "play_meta.json").read_text())
SHOTS = ROOT / "Screenshots" / "task08"

GB = LAYOUT["greybox"]
G = LAYOUT["ground"]
KO = GB["keepout"]
STOREY = GB["storey"]

# ---------------------------------------------------------------- the play ground grid

RES, SIZE, X0, Z0 = META["resolution"], META["size"], META["x0"], META["z0"]
STEP = SIZE / (RES - 1)
CX, CZ = META["frame_center"]
ROT = math.radians(META["frame_rotation_deg"])
COS, SIN = math.cos(ROT), math.sin(ROT)
HALF_U, HALF_V = META["frame_size"][0] / 2, META["frame_size"][1] / 2
B = META["flag_b_height"]

raw = np.fromfile(PG / META["file"], dtype="<u2").reshape(RES, RES).astype(np.float64)
height = META["base_height"] + raw / 65535.0 * (META["top_height"] - META["base_height"])
mask_img = np.asarray(Image.open(PG / META["mask"]).convert("L"), dtype=np.float64)[::-1, :] / 255.0  # rows south->north
mask = map_coordinates(mask_img, np.meshgrid(np.arange(RES) * (mask_img.shape[0] - 1) / (RES - 1),
                                             np.arange(RES) * (mask_img.shape[1] - 1) / (RES - 1), indexing="ij"), order=1) > 0.5

XX, ZZ = np.meshgrid(X0 + np.arange(RES) * STEP, Z0 + np.arange(RES) * STEP)


def to_world(u, v):
    return CX + u * COS - v * SIN, CZ + u * SIN + v * COS


def to_frame(x, z):
    dx, dz = x - CX, z - CZ
    return dx * COS + dz * SIN, -dx * SIN + dz * COS


UU, VV = to_frame(XX, ZZ)


def sample_uv(u, v):
    x, z = to_world(np.asarray(u, float), np.asarray(v, float))
    x, z = np.atleast_1d(x), np.atleast_1d(z)
    r = map_coordinates(height, [(z - Z0) / STEP, (x - X0) / STEP], order=1, mode="nearest")
    return float(r[0]) if r.size == 1 else r


def chaikin(pts, n=2):
    pts = np.asarray(pts, float)
    for _ in range(n):
        q = [pts[0]]
        for a, b in zip(pts[:-1], pts[1:]):
            q += [0.75 * a + 0.25 * b, 0.25 * a + 0.75 * b]
        q.append(pts[-1])
        pts = np.array(q)
    return pts


def stations(pts, spacing=0.5):
    seg = np.hypot(*np.diff(pts, axis=0).T)
    cum = np.concatenate([[0], np.cumsum(seg)])
    s = np.arange(0, cum[-1] + 1e-6, spacing)
    return s, np.interp(s, cum, pts[:, 0]), np.interp(s, cum, pts[:, 1])


def polyline_distance(pts):
    pts = np.asarray(pts, float)
    best = np.full(UU.shape, np.inf)
    for a, b in zip(pts[:-1], pts[1:]):
        ab = b - a
        t = np.clip(((UU - a[0]) * ab[0] + (VV - a[1]) * ab[1]) / float(ab @ ab), 0, 1)
        best = np.minimum(best, np.hypot(UU - (a[0] + t * ab[0]), VV - (a[1] + t * ab[1])))
    return best


def connector_path(c):
    return chaikin(c["points"], 3) if len(c["points"]) > 2 else np.asarray(c["points"], float)


def footprint(cu, cv, length, width, du, dv):
    n = math.hypot(du, dv)
    du, dv = du / n, dv / n
    a = (UU - cu) * du + (VV - cv) * dv
    b = -(UU - cu) * dv + (VV - cv) * du
    return (np.abs(a) <= length / 2) & (np.abs(b) <= width / 2)


def world_box_footprint(b, grow=0.0):
    y = math.radians(b["yaw"])
    fx, fz = math.sin(y), math.cos(y)
    a = (XX - b["x"]) * fx + (ZZ - b["z"]) * fz
    c = (XX - b["x"]) * fz - (ZZ - b["z"]) * fx
    return (np.abs(a) <= b["sz"] / 2 + grow) & (np.abs(c) <= b["sx"] / 2 + grow)


def inside_frame(cu, cv, length, width, du, dv):
    n = math.hypot(du, dv)
    du, dv = du / n, dv / n
    for sa in (-1, 1):
        for sb in (-1, 1):
            if abs(cu + sa * du * length / 2 - sb * dv * width / 2) > HALF_U or abs(cv + sa * dv * length / 2 + sb * du * width / 2) > HALF_V:
                return False
    return True


def world_yaw(du, dv):
    dx, dz = du * COS - dv * SIN, du * SIN + dv * COS
    return math.degrees(math.atan2(dx, dz))


# ---------------------------------------------------------------- what stays: lanes, connectors, ravine, placeholders

lanes = {l["id"]: l for l in LAYOUT["lanes"]}
shoulder = G["lanes"]["shoulder"]
lane_geo = {}
for lid, l in lanes.items():
    pts = chaikin(l["points"])
    s, u, v = stations(pts)
    lane_geo[lid] = (pts, s, u, v, sample_uv(u, v))

all_replaces = [p for sec in GB["sections"].values() for p in sec.get("replaces", [])]


def replaced(name):
    return any(name.startswith(p) for p in all_replaces)


# The ravine section's replacements (the bridge decks) stay in the keepouts of the earlier sections, so a locked
# section regenerates exactly as it was built.
_earlier = [p for sec in GB["sections"].values() if sec.get("kind") != "ravine" for p in sec.get("replaces", [])]
kept_boxes = [b for b in META["boxes"] if not any(b["name"].startswith(p) for p in _earlier)]
# Task 7's terrace-wall strips only mark terrace risers: a greybox block may stand over them, and then replaces them.
SOFT = ("Terrace wall ",)
kept = np.zeros(UU.shape, bool)
for b in kept_boxes:
    if "outside" in b["name"] or b["name"].startswith(SOFT):
        continue
    kept |= world_box_footprint(b, KO["box_clear"])
auto_replaced = []

conn = np.zeros(UU.shape, bool)
for c in LAYOUT["connectors"]:
    conn |= polyline_distance(connector_path(c)) <= c["width"] / 2 + G["connectors"]["shoulder"] + KO["connector_clear"]
rav = polyline_distance(chaikin(LAYOUT["ravine"]["points"])) <= LAYOUT["ravine"]["width"] / 2 + KO["ravine_clear"]


def area_distance(a):
    if a["shape"] == "circle":
        return np.maximum(np.hypot(UU - a["center"][0], VV - a["center"][1]) - a["radius"], 0)
    du = np.maximum(np.abs(UU - a["center"][0]) - a["size"][0] / 2, 0)
    dv = np.maximum(np.abs(VV - a["center"][1]) - a["size"][1] / 2, 0)
    return np.hypot(du, dv)


pads = np.zeros(UU.shape, bool)
for a in LAYOUT["areas"]:
    pads |= area_distance(a) <= KO["lane_clear"]


def own_lane(lid, extra=0.0):
    l = lanes[lid]
    return polyline_distance(lane_geo[lid][0]) <= l["width"] / 2 + shoulder + KO["lane_clear"] + extra


def other_surfaces(lid):
    """Every walkable surface except the lane itself: the other lanes, every pad (play_mask.png also holds the
    lane benches, so the keepouts are the exact layout shapes instead)."""
    other = pads.copy()
    for oid, ol in lanes.items():
        if oid != lid:
            other |= own_lane(oid)
    return other


def lane_keepout(lid):
    return other_surfaces(lid) | own_lane(lid)


# ---------------------------------------------------------------- output helpers

boxes, routes, labels = [], [], []


def box(section, name, group, mat, cu, cv, y0, y1, length, width, du=1.0, dv=0.0, **extra):
    x, z = to_world(cu, cv)
    b = {"name": name, "group": group, "mat": mat, "section": section, "x": float(x), "y": float((y0 + y1) / 2),
         "z": float(z), "sx": float(width), "sy": float(y1 - y0), "sz": float(length), "yaw": float(world_yaw(du, dv)),
         "pitch": 0.0, "u": float(cu), "v": float(cv), "du": float(du), "dv": float(dv)}
    b.update({k: v for k, v in extra.items() if v is not None})
    boxes.append(b)
    return b


def ground_range(fp):
    g = height[fp]
    return float(g.min()), float(g.max()), float(np.median(g))


def lane_height_at(lid, u, v):
    _, s, lu, lv, prof = lane_geo[lid]
    i = int(np.argmin((lu - u) ** 2 + (lv - v) ** 2))
    return float(prof[i])


def route(name, pts):
    pts = np.asarray(pts, float)
    s, u, v = stations(pts, 3.0)
    if s[-1] < (np.hypot(*np.diff(pts, axis=0).T).sum() - 1e-3):
        u, v = np.append(u, pts[-1, 0]), np.append(v, pts[-1, 1])
    x, z = to_world(u, v)
    routes.append({"name": name, "xs": [float(a) for a in x], "zs": [float(a) for a in z]})


# ---------------------------------------------------------------- one section

def build_section(key, sec):
    rng = np.random.default_rng(sec["seed"])
    lid = sec["lane"]
    lane = lanes[lid]
    pts, s, lu, lv, prof = lane_geo[lid]
    others = other_surfaces(lid)
    keep_lane = others | own_lane(lid)
    occupied = keep_lane | conn | rav | kept
    placed = np.zeros(UU.shape, bool)
    plateia = next((a for a in LAYOUT["areas"] if a["id"] == "plateia"), None)
    plateia_h = sample_uv(*plateia["center"]) if plateia else None

    def ref_height(ref, cu, cv):
        if ref == "plateia":
            return plateia_h
        if ref == "ground":
            return sample_uv(cu, cv)
        return lane_height_at(lid, cu, cv)

    def part(prefix, p, h0, group, du=1.0, dv=0.0):
        cu, cv = p["at"]
        L, W = p["size"]
        fp = footprint(cu, cv, L, W, du, dv)
        if not fp.any():
            fp = footprint(cu, cv, max(L, STEP * 1.5), max(W, STEP * 1.5), du, dv)
        z0, z1 = p["z"]
        bottom = ground_range(fp)[0] - 0.3 if z0 is None else h0 + z0
        top = h0 + z1
        if p.get("rail"):  # a railing: a top rail and posts every 1.5 m, see-through for the sight-line checks
            n = max(int(math.ceil(L / 1.5)), 1)
            box(key, f"{prefix} {p['name']} top rail", group, p["mat"], cu, cv, top - 0.06, top, L, 0.06, du, dv, rail=True)
            for k in range(n + 1):
                t = -L / 2 + L * k / n
                box(key, f"{prefix} {p['name']} post {k}", group, p["mat"], cu + du * t, cv + dv * t, bottom, top, 0.05, 0.05,
                    du, dv, rail=True)
        else:
            box(key, f"{prefix} {p['name']}", group, p["mat"], cu, cv, bottom, top, L, W, du, dv,
                shape=p.get("shape"), collider=p.get("collider"), walk_top=p.get("walk_top"), overhead=p.get("overhead"))
        return fp

    # Landmarks: designed buildings and their parts, at their real-photo sizes.
    for lm in sec.get("landmarks", []):
        if "frame" in lm:
            fr = lm["frame"]
            cu, cv = fr["at"]
            L, W = fr["size"]
            fp = footprint(cu, cv, L, W, 1.0, 0.0)
            gmin, gmax, gmed = ground_range(fp)
            h0 = gmed
            nf = fr["floors"]
            sto = fr.get("storey", STOREY)
            for k, uu in enumerate(np.linspace(cu - L / 2 + 0.15, cu + L / 2 - 0.15, 3)):
                for j, vv in enumerate(np.linspace(cv - W / 2 + 0.15, cv + W / 2 - 0.15, 3)):
                    box(key, f"{lm['name']} column {k}{j}", "Buildings", "wall", uu, vv, gmin - 0.3, h0 + nf * sto + 0.25, 0.3, 0.3)
            for f in range(1, nf + 1):
                box(key, f"{lm['name']} slab {f}", "Buildings", "wall", cu, cv, h0 + f * sto - 0.05, h0 + f * sto + 0.25, L, W)
            for side in fr["infill"]:  # block infill on the named sides of the ground floor (sight blockers)
                if side in "NS":
                    sg = 1 if side == "N" else -1
                    box(key, f"{lm['name']} infill {side}", "Buildings", "wall", cu, cv + sg * (W / 2 - 0.15), gmin - 0.3, h0 + sto - 0.05, L, 0.3)
                else:
                    sg = 1 if side == "E" else -1
                    box(key, f"{lm['name']} infill {side}", "Buildings", "wall", cu + sg * (L / 2 - 0.15), cv, gmin - 0.3, h0 + sto - 0.05, 0.3, W)
            placed |= fp
            top = h0 + nf * sto + 0.25
        else:
            h0 = ref_height(lm.get("ref", "street"), *lm["parts"][0]["at"])
            top = h0
            for p in lm["parts"]:
                group = "Cover" if p["mat"] in ("cover",) else "Buildings"
                if p.get("walk_top") and p["size"][0] * p["size"][1] < 4.0:
                    group = "Stairs"
                fp = part(lm["name"], p, h0, group)
                if p.get("collider", True) is not False and not p.get("overhead"):
                    placed |= fp
                    hit = fp & (keep_lane | conn | rav)
                    if hit.any():
                        print(f"  NOTE {lm['name']} / {p['name']} stands on {hit.sum() * STEP * STEP:.1f} m2 of a lane, connector "
                              f"or ravine keepout (designed: check it leaves the way open)")
                top = max(top, h0 + p["z"][1])
            cu, cv = lm["parts"][0]["at"]
        labels.append({"text": lm["name"], "sub": lm["label"], "u": cu, "v": cv, "y": top + 2.0})
    occupied |= binary_dilation(placed, iterations=1)

    # Fills: frame rectangles packed with blocks, largest free rectangle first.
    FR = sec.get("fill_rules", {"width": [6, 12], "gap": 0.5, "min": 2.5})
    for fl in sec.get("fills", []):
        (fu0, fu1), (fv0, fv1) = fl["u"], fl["v"]
        gu = np.arange(fu0, fu1 + 1e-6, 0.5)
        gv = np.arange(fv0, fv1 + 1e-6, 0.5)
        GU, GV = np.meshgrid(gu, gv)
        x, z = to_world(GU, GV)
        r = np.clip(np.round((z - Z0) / STEP).astype(int), 0, RES - 1)
        c = np.clip(np.round((x - X0) / STEP).astype(int), 0, RES - 1)
        free = ~occupied[r, c]
        gap = int(round(fl.get("gap", FR["gap"]) / 0.5))  # 0 = attached houses (no slot to see through)
        k = 0
        while True:
            area, r0, r1, c0, c1 = largest_free_rect(free)
            nv, nu = r1 - r0, c1 - c0
            if nv * 0.5 < FR["min"] or nu * 0.5 < FR["min"]:
                break
            long_u = nu >= nv
            n_long = nu if long_u else nv
            pieces = max(int(math.ceil(n_long * 0.5 / FR["width"][1])), 1)
            cuts = np.linspace(0, n_long, pieces + 1).round().astype(int)
            for q0, q1 in zip(cuts[:-1], cuts[1:]):
                q1 = q1 - (gap if q1 < n_long else 0)
                if long_u:
                    ua, ub, va, vb = gu[c0 + q0], gu[c0 + q1], gv[r0], gv[r1]
                else:
                    ua, ub, va, vb = gu[c0], gu[c1], gv[r0 + q0], gv[r0 + q1]
                if ub - ua < FR["min"] or vb - va < FR["min"]:
                    continue
                cu, cv = (ua + ub) / 2, (va + vb) / 2
                fp = footprint(cu, cv, ub - ua, vb - va, 1.0, 0.0)
                gmin, gmax, gmed = ground_range(fp)
                street = lane_height_at(lid, cu, cv)
                if "low_roof" in fl:
                    top = street + fl["low_roof"]
                else:
                    top = max(street + fl["storeys"] * STOREY + rng.uniform(-0.3, 0.3), gmax + 2.5)
                k += 1
                box(key, f"{fl['name']} {k}", "Buildings", "low" if "low_roof" in fl else "wall", cu, cv, gmin - 0.3, top, ub - ua, vb - va)
                placed |= fp
                occupied |= fp
            free[max(r0 - max(gap, 1), 0):r1 + max(gap, 1) + 1, max(c0 - max(gap, 1), 0):c1 + max(gap, 1) + 1] = False
        print(f"  fill {fl['name']}: {k} blocks")

    # Street rows: house blocks along each side, fronts on the shoulder edge plus a setback.
    half = lane["width"] / 2 + shoulder
    tang = np.gradient(np.stack([lu, lv], 1), axis=0)
    tang /= np.linalg.norm(tang, axis=1, keepdims=True)
    house_count = 0
    for row in sec.get("rows", []):
        side = 1 if row["side"] == "N" else -1
        st_n, st_p = zip(*row["storeys"])
        idx = np.flatnonzero((lu >= row["u"][0]) & (lu <= row["u"][1]))
        if not len(idx):
            continue
        i, i_end = int(idx[0]), int(idx[-1])
        while i < i_end:
            depth = rng.uniform(*row["depth"])
            placed_here = False
            for w in (rng.uniform(*row["width"]), row["min_width"] + 1.0, row["min_width"]):
                for d in (depth, 6.0, 4.5):
                    j = min(i + int(w / 2 / 0.5), len(s) - 1)
                    tu, tv = tang[j]
                    nu, nv = -tv * side, tu * side
                    off = half + row["setback"] + d / 2
                    cu, cv = lu[j] + nu * off, lv[j] + nv * off
                    fp = footprint(cu, cv, w, d, tu, tv)
                    if inside_frame(cu, cv, w, d, tu, tv) and not (fp & occupied).any():
                        placed_here = True
                        break
                if placed_here:
                    break
            if placed_here:
                storeys = int(rng.choice(st_n, p=st_p))
                street = float(prof[j])
                gmin, gmax, _ = ground_range(fp)
                top = max(street + storeys * STOREY + rng.uniform(-0.3, 0.3), gmax + 2.5)
                house_count += 1
                box(key, f"House {row['side']} {house_count:03d}", "Buildings", "wall", cu, cv, gmin - 0.3, top, w, d, tu, tv)
                placed |= fp
                occupied |= fp
                i += int((w + rng.uniform(*row["gap"])) / 0.5)
            else:
                i += 2
    print(f"  street rows: {house_count} houses")

    # Street walls: wherever no house front stands on the shoulder edge, a retaining wall (north: up to the ground
    # behind it, rail on top when tall) or a low garden wall (south), split at most max_piece long.
    W = sec.get("walls")
    walls = 0
    if W:
        wall_keep = others | conn | rav | kept
        for side in (1, -1):
            ok = np.zeros(len(s), bool)
            hts = np.zeros(len(s))
            for i in range(len(s)):
                if not (W["u"][0] <= lu[i] <= W["u"][1]):
                    continue
                tu, tv = tang[i]
                nu, nv = -tv * side, tu * side
                off = half + W["offset"] + W["thickness"] / 2
                pu, pv = lu[i] + nu * off, lv[i] + nv * off
                if abs(pu) > HALF_U or abs(pv) > HALF_V:
                    continue
                rr, cc = grid_rc(pu, pv)
                fu, fv = lu[i] + nu * (half + 1.6), lv[i] + nv * (half + 1.6)   # a house front just behind?
                fr_, fc_ = grid_rc(fu, fv)
                if wall_keep[rr, cc] or placed[rr, cc] or placed[fr_, fc_]:
                    continue
                if any(g["side"] == ("N" if side > 0 else "S") and g["u"][0] <= lu[i] <= g["u"][1] for g in W.get("gaps", [])):
                    continue
                if side > 0:
                    rise = sample_uv(lu[i] + nu * (half + W["north"]["behind"]), lv[i] + nv * (half + W["north"]["behind"])) - prof[i]
                    hts[i] = float(np.clip(rise + W["north"]["over"], W["north"]["min"], W["north"]["max"]))
                else:
                    hts[i] = W["south"]
                ok[i] = True
            i = 0
            while i < len(s):
                if not ok[i]:
                    i += 1
                    continue
                j = i
                while j + 1 < len(s) and ok[j + 1] and s[j + 1] - s[i] < W["max_piece"]:
                    j += 1
                if s[j] - s[i] >= 0.9:
                    tu, tv = lu[j] - lu[i], lv[j] - lv[i]
                    n = math.hypot(tu, tv)
                    nu, nv = -tv / n * side, tu / n * side
                    off = half + W["offset"] + W["thickness"] / 2
                    cu, cv = (lu[i] + lu[j]) / 2 + nu * off, (lv[i] + lv[j]) / 2 + nv * off
                    street = float(prof[i:j + 1].min())
                    top = float(prof[i:j + 1].max()) + float(hts[i:j + 1].max())
                    fp = footprint(cu, cv, n + 0.5, W["thickness"], tu, tv)
                    gmin = ground_range(fp)[0] if fp.any() else street
                    walls += 1
                    kind = "N" if side > 0 else "S"
                    box(key, f"Street wall {kind} {walls:03d}", "Walls", "wall" if side > 0 and top - street > 1.3 else "cover",
                        cu, cv, min(gmin, street) - 0.3, top, n + 0.5, W["thickness"], tu, tv)
                    if side > 0 and top - street >= W["rail_from"]:  # a steel railing on tall retaining walls
                        L = n + 0.5
                        box(key, f"Street wall {kind} {walls:03d} rail", "Walls", "trim", cu, cv, top + 0.94, top + 1.0, L, 0.06, tu, tv, rail=True)
                        np_ = max(int(math.ceil(L / 1.5)), 1)
                        for q in range(np_ + 1):
                            t = -L / 2 + 0.05 + (L - 0.1) * q / np_
                            box(key, f"Street wall {kind} {walls:03d} rail post {q}", "Walls", "trim", cu + tu / n * t, cv + tv / n * t,
                                top, top + 1.0, 0.05, 0.05, tu, tv, rail=True)
                    placed |= fp
                i = j + 1
    print(f"  street walls: {walls} pieces")
    for b in kept_boxes:
        if b["name"].startswith(SOFT) and (world_box_footprint(b) & placed).any():
            auto_replaced.append(b["name"])

    # Strips (sidewalks, guardrails) and posts (lamps) following the street on one side.
    for st in sec.get("strips", []):
        side = 1 if st["side"] == "N" else -1
        o0, o1 = st["offset"]
        idx = np.flatnonzero((lu >= st["u"][0]) & (lu <= st["u"][1]))
        k = 0
        for a in range(int(idx[0]), int(idx[-1]), 6):
            b2 = min(a + 6, int(idx[-1]))
            tu, tv = lu[b2] - lu[a], lv[b2] - lv[a]
            n = math.hypot(tu, tv)
            if n < 0.5:
                continue
            nu, nv = -tv / n * side, tu / n * side
            mu, mv = (lu[a] + lu[b2]) / 2, (lv[a] + lv[b2]) / 2
            cu, cv = mu + nu * (o0 + o1) / 2, mv + nv * (o0 + o1) / 2
            fp = footprint(cu, cv, n + 0.05, o1 - o0, tu, tv)
            if (fp & ((rav | kept | others | placed) if st.get("cross_connectors") else (conn | rav | kept | others | placed))).any():
                continue
            street = float(prof[a:b2 + 1].mean())
            z0, z1 = st["z"]
            gmin, gmax, _ = ground_range(fp)
            top = street + z1
            if "over_ground" in st:  # where the ground beside the street rises, the strip rides on it
                top = max(top, gmax + st["over_ground"])
            k += 1
            box(key, f"{st['name']} {k:02d}", "Cover" if st["mat"] == "cover" else "Stairs", st["mat"], cu, cv,
                min(gmin, street) - 0.3 if z0 is None else top - (z1 - z0), top, n + 0.05, o1 - o0, tu, tv,
                walk_top=st.get("walk_top"))
            placed |= fp
    for po in sec.get("posts", []):
        side = 1 if po["side"] == "N" else -1
        for k, uu in enumerate(np.arange(po["u"][0], po["u"][1] + 1e-6, po["every"])):
            i = int(np.argmin(np.abs(lu - uu) + 1000 * ~((lu >= po["u"][0] - 1) & (lu <= po["u"][1] + 1))))
            tu, tv = tang[i]
            nu, nv = -tv * side, tu * side
            cu, cv = lu[i] + nu * po["offset"], lv[i] + nv * po["offset"]
            rr, cc = grid_rc(cu, cv)
            if conn[rr, cc] or rav[rr, cc] or placed[rr, cc]:
                continue
            box(key, f"{po['name']} {k + 1:02d}", "Cover", po["mat"], cu, cv, prof[i] + po["z"][0], prof[i] + po["z"][1],
                po["size"], po["size"], tu, tv, shape="cylinder")


def deck_local(db, lx, ly, lz):
    """World position of a point in a placeholder deck's local frame (x across the lane, y up, z along the lane;
    Unity Euler order: pitch about x, then yaw about y)."""
    p, w = math.radians(db["pitch"]), math.radians(db["yaw"])
    y1, z1 = ly * math.cos(p) - lz * math.sin(p), ly * math.sin(p) + lz * math.cos(p)
    return (db["x"] + lx * math.cos(w) + z1 * math.sin(w), db["y"] + y1, db["z"] - lx * math.sin(w) + z1 * math.cos(w))


def build_ravine(key, sec):
    """Task 8b: the ravine and its three crossings. Crossings are rebuilt on the Task 7 placeholder decks (same
    position, slope and top height), rim walls follow the ravine edge, boulders and trunks dress the floor and rim,
    screens close sight lines from the east-bank path onto the capture disc."""
    rng = np.random.default_rng(sec["seed"])
    decks = {b["name"]: b for b in META["boxes"] if b["name"].endswith(" deck") and b["group"] == "Bridges"}
    n_boxes = len(boxes)

    def local_box(name, group, mat, db, lc, size, **extra):
        x, y, z = deck_local(db, *lc)
        b = {"name": name, "group": group, "mat": mat, "section": key, "x": float(x), "y": float(y), "z": float(z),
             "sx": float(size[0]), "sy": float(size[1]), "sz": float(size[2]), "yaw": float(db["yaw"]), "pitch": float(db["pitch"])}
        b.update({k: v for k, v in extra.items() if v is not None})
        boxes.append(b)

    zone = np.zeros(UU.shape, bool)   # the crossings and their approaches stay free of rim walls, boulders, trunks
    for cr in sec["crossings"]:
        db = decks[cr["deck"]]
        width = cr.get("width", db["sx"])
        span = db["sz"]
        thick = cr["thick"]
        top = db["sy"] / 2                                  # the placeholder's walking surface, local y
        local_box(f"{cr['name']} deck", "Bridges", "bridge", db, (0, top - thick / 2, 0), (width, thick, span))
        zone |= world_box_footprint({"x": db["x"], "z": db["z"], "yaw": db["yaw"], "sx": width, "sz": span}, cr.get("zone", 3.0))
        if cr["kind"] in ("bridge", "culvert"):             # solid parapets: stone, 1.05 m (bridge) or a low kerb wall (culvert)
            h, t = cr["parapet"], cr.get("parapet_t", 0.4)
            for tag, sg in (("S", -1), ("N", 1)):
                local_box(f"{cr['name']} parapet {tag}", "Bridges", cr.get("parapet_mat", "bridge"), db,
                          (sg * (width / 2 - t / 2), top + h / 2, 0), (t, h, span - 2.0))   # ends 1 m short: the ramps run past the deck corners
            for tag, sg in (("S", -1), ("N", 1)):           # end posts, a little taller than the parapet
                for end in (-1, 1):
                    local_box(f"{cr['name']} parapet post {tag}{'+' if end > 0 else '-'}", "Bridges", cr.get("parapet_mat", "bridge"), db,
                              (sg * (width / 2 - 0.3), top + (h + 0.2) / 2, end * (span / 2 - 1.3)), (0.6, h + 0.2, 0.6))
        else:                                               # footbridge: pipe railing (top rail, mid rail, posts), see-through
            h = cr["rail"]
            rl = span - 2.0                                 # rails end 1 m short of the deck ends (ramps)
            n = max(int(math.ceil(rl / 1.5)), 1)
            for tag, sg in (("S", -1), ("N", 1)):
                lx = sg * (width / 2 - 0.05)
                local_box(f"{cr['name']} rail {tag} top", "Bridges", "trim", db, (lx, top + h - 0.03, 0), (0.06, 0.06, rl), rail=True)
                local_box(f"{cr['name']} rail {tag} mid", "Bridges", "trim", db, (lx, top + h * 0.5, 0), (0.04, 0.04, rl), rail=True)
                for k in range(n + 1):
                    local_box(f"{cr['name']} rail {tag} post {k}", "Bridges", "trim", db,
                              (lx, top + h / 2, -rl / 2 + 0.05 + (rl - 0.1) * k / n), (0.05, h, 0.05), rail=True)
        print(f"  crossing {cr['name']}: {cr['kind']}, deck {width:.1f} x {span:.1f} m, {thick} m thick")

    pts = chaikin(LAYOUT["ravine"]["points"])
    s, ru, rv = stations(pts, 0.5)
    tang = np.gradient(np.stack([ru, rv], 1), axis=0)
    tang /= np.linalg.norm(tang, axis=1, keepdims=True)
    others = pads.copy()
    for oid in lanes:
        others |= own_lane(oid)
    existing = np.zeros(UU.shape, bool)
    for b in boxes[:n_boxes]:
        if not b.get("rail") and b.get("collider", True):
            existing |= world_box_footprint(b, 0.3)
    conn_floor = np.zeros(UU.shape, bool)
    for c in LAYOUT["connectors"]:
        conn_floor |= polyline_distance(connector_path(c)) <= c["width"] / 2 + 0.5
    # The Task 7 floor route (ramps down from the lanes and the floor itself) stays open.
    floor_route = np.zeros(UU.shape, bool)
    for r in META["routes"]:
        if r["name"].startswith("Ravine floor"):
            ru_, rv_ = to_frame(np.array(r["xs"]), np.array(r["zs"]))
            floor_route |= polyline_distance(np.stack([ru_, rv_], 1)) <= 2.4
    zone |= floor_route
    avoid = zone | others | conn_floor | kept | existing

    def rim_t(i, side):
        """Distance from the ravine centreline to the rim: where the bank has risen to within 0.4 m of its crest."""
        ts = np.arange(3.5, 9.0, 0.25)
        n = np.array([-tang[i][1], tang[i][0]]) * side
        h = np.array([sample_uv(ru[i] + n[0] * t, rv[i] + n[1] * t) for t in ts])
        return float(ts[np.argmax(h >= h.max() - 0.4)])

    # Rim walls: dry-stone, 1.0 m above the rim, pieces of at most 3 m, skipped at crossings, connectors and lanes.
    RW = sec["rim_walls"]
    pieces = 0
    for side in (-1, 1):
        i = 0
        while i < len(s) - 1:
            j = i
            while j + 1 < len(s) and s[j + 1] - s[i] < RW["max_piece"]:
                j += 1
            if not (RW["v"][0] <= rv[i] <= RW["v"][1]) or j - i < 3:
                i = j
                continue
            tu, tv = ru[j] - ru[i], rv[j] - rv[i]
            n = math.hypot(tu, tv)
            nu, nv = -tv / n * side, tu / n * side
            t = np.mean([rim_t(k, side) for k in (i, (i + j) // 2, j)]) + RW["outset"]
            cu, cv = (ru[i] + ru[j]) / 2 + nu * t, (rv[i] + rv[j]) / 2 + nv * t
            fp = footprint(cu, cv, n + 0.3, RW["thickness"], tu, tv)
            if fp.any() and not (fp & avoid).any() and inside_frame(cu, cv, n + 0.3, RW["thickness"], tu, tv):
                gmin, gmax, gmed = ground_range(fp)
                pieces += 1
                box(key, f"Ravine rim wall {pieces:03d}", "Walls", "rock", cu, cv, gmin - 0.3, gmax + RW["height"], n + 0.3,
                    RW["thickness"], tu, tv)
            i = j
    print(f"  ravine rim walls: {pieces} pieces")
    taken = np.zeros(UU.shape, bool)
    for b in boxes[n_boxes:]:
        if b["group"] == "Walls":
            taken |= world_box_footprint(b, 0.3)
    avoid |= taken

    # Floor cover: boulders along the floor, off the walking line (the floor stays >= 2.4 m clear).
    BO = sec["boulders"]
    count = 0
    idx_all = np.arange(len(s))
    for lo, hi in BO["v"]:
        sel = idx_all[(rv >= lo) & (rv <= hi)]
        if not len(sel):
            continue
        step = int(BO["every"] / 0.5)
        for k, i in enumerate(sel[::step]):
            side = 1 if (k + int(lo)) % 2 == 0 else -1
            n = np.array([-tang[i][1], tang[i][0]]) * side
            off = BO["lateral"] + rng.uniform(-0.3, 0.3)
            cu, cv = ru[i] + n[0] * off + rng.uniform(-1.0, 1.0), rv[i] + n[1] * off
            w, d, h = rng.uniform(*BO["w"]), rng.uniform(*BO["d"]), rng.uniform(*BO["h"])
            yaw_u, yaw_v = math.cos(rng.uniform(0, math.pi)), math.sin(rng.uniform(0, math.pi))
            fp = footprint(cu, cv, w, d, yaw_u, yaw_v)
            if not fp.any() or (fp & (zone | others | existing | kept)).any():
                continue
            gmin, gmax, gmed = ground_range(fp)
            count += 1
            box(key, f"Floor boulder {count:02d}", "Cover", "rock", cu, cv, gmin - 0.3, gmed + h, w, d, yaw_u, yaw_v)
    print(f"  floor boulders: {count}")

    # Trunks and canopies: the tall tree band along the gully, outside the rim walls.
    TR = sec["trees"]
    count = 0
    occupied_now = avoid | placed_all()
    for side in (-1, 1):
        sel = idx_all[(rv >= TR["v"][0]) & (rv <= TR["v"][1])]
        for k, i in enumerate(sel[::int(TR["every"] / 0.5)]):
            n = np.array([-tang[i][1], tang[i][0]]) * side
            off = rim_t(i, side) + TR["outset"] + rng.uniform(-0.8, 1.2)
            cu, cv = ru[i] + n[0] * off + rng.uniform(-1.5, 1.5), rv[i] + n[1] * off
            fp = footprint(cu, cv, TR["canopy"] * 0.4, TR["canopy"] * 0.4, 1.0, 0.0)
            if not fp.any() or (fp & occupied_now).any() or not inside_frame(cu, cv, 3, 3, 1.0, 0.0):
                continue
            gmin, gmax, gmed = ground_range(fp)
            occupied_now |= binary_dilation(fp, iterations=6)
            count += 1
            hgt = rng.uniform(*TR["height"])
            box(key, f"Tree trunk {count:02d}", "Cover", "bark", cu, cv, gmin - 0.3, gmed + hgt, 0.7, 0.7, shape="cylinder")
            c = TR["canopy"] * rng.uniform(0.85, 1.15)
            box(key, f"Tree canopy {count:02d}", "Cover", "foliage", cu, cv, gmed + hgt - 2.5, gmed + hgt + 2.5, c, c, shape="sphere",
                collider=False)
    print(f"  trees: {count}")

    # Screens: walls beside a connector that close its views onto the capture disc (the East perch fix).
    for sc in sec.get("screens", []):
        c = next(c for c in LAYOUT["connectors"] if c["name"] == sc["along"])
        cp = connector_path(c)
        cs, cu_, cv_ = stations(cp, 0.5)
        ctang = np.gradient(np.stack([cu_, cv_], 1), axis=0)
        ctang /= np.linalg.norm(ctang, axis=1, keepdims=True)
        other_conn = np.zeros(UU.shape, bool)
        for c2 in LAYOUT["connectors"]:
            if c2["name"] != c["name"]:
                other_conn |= polyline_distance(connector_path(c2)) <= c2["width"] / 2 + 0.5
        keep = other_conn | others | zone
        ok = np.array([(sc["u"][0] <= cu_[i] <= sc["u"][1]) and (sc["v"][0] <= cv_[i] <= sc["v"][1]) for i in range(len(cs))])
        k, i = 0, 0
        while i < len(cs) - 1:
            if not ok[i]:
                i += 1
                continue
            j = i
            while j + 1 < len(cs) and ok[j + 1] and cs[j + 1] - cs[i] < 3.0:
                j += 1
            tu, tv = cu_[j] - cu_[i], cv_[j] - cv_[i]
            n = math.hypot(tu, tv)
            if n >= 0.9:
                nu, nv = -tv / n * sc["side"], tu / n * sc["side"]
                mu, mv = (cu_[i] + cu_[j]) / 2 + nu * sc["offset"], (cv_[i] + cv_[j]) / 2 + nv * sc["offset"]
                fp = footprint(mu, mv, n + 0.3, sc["thickness"], tu, tv)
                if not (fp & keep).any():
                    path_h = [sample_uv(cu_[q], cv_[q]) for q in range(i, j + 1)]
                    gmin = ground_range(fp)[0]
                    k += 1
                    box(key, f"{sc['name']} {k:02d}", "Walls", sc.get("mat", "rock"), mu, mv, min(gmin, min(path_h)) - 0.3,
                        max(path_h) + sc["height"], n + 0.3, sc["thickness"], tu, tv)
            i = j + 1
        print(f"  screen {sc['name']}: {k} pieces")


def placed_all():
    m = np.zeros(UU.shape, bool)
    for b in boxes:
        if not b.get("rail") and b.get("collider", True) and b["group"] != "Bridges":
            m |= world_box_footprint(b, 0.3)
    return m


def grid_rc(u, v):
    x, z = to_world(u, v)
    return int(np.clip(round((z - Z0) / STEP), 0, RES - 1)), int(np.clip(round((x - X0) / STEP), 0, RES - 1))


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


def cameras(sec):
    """Review cameras (EditorOnly in Unity): eye metres above the ground at "at", looking at [u, v, metres above
    the ground there]."""
    out = []
    for c in sec.get("cameras", []):
        x, z = to_world(*c["at"])
        y = sample_uv(*c["at"]) + c["eye"]
        lx, lz = to_world(c["look"][0], c["look"][1])
        ly = sample_uv(c["look"][0], c["look"][1]) + c["look"][2]
        out.append({"name": c["name"], "x": float(x), "y": float(y), "z": float(z),
                    "lx": float(lx), "ly": float(ly), "lz": float(lz), "fov": float(c.get("fov", 64.0))})
    return out


# ---------------------------------------------------------------- run

cams = []
for key, sec in GB["sections"].items():
    print(f"section {key} (Task {sec['task']}){' - locked' if sec.get('locked') else ''}")
    (build_ravine if sec.get("kind") == "ravine" else build_section)(key, sec)
    cams += cameras(sec)

# Walk-test routes through the new geometry (frame coords).
route("Memorial terrace (up and over)", [[-18, 0], [-16.5, 1.5], [-16.5, 7.1], [-13.25, 7.1], [-13.25, 11.0], [-18.5, 11.5]])
route("Plateia crossing (memorial to kafeneio)", [[-16.5, 4.5], [-12, -4], [-5, -12.5]])
route("Into the south unfinished frame (from the west)", [[-55.5, 1.1], [-46.4, 1.1], [-46.4, -3.1]])
route("East entry (south kerb)", [[45, -9.0], [52, -9.6], [59, -9.6]])

OUT = PG / "greybox_meta.json"
OUT.write_text(json.dumps({"sections": {k: {"task": v["task"], "locked": bool(v.get("locked"))} for k, v in GB["sections"].items()},
                           "replaces": all_replaces + auto_replaced, "boxes": boxes, "routes": routes, "cameras": cams,
                           "labels": [{**l, "x": float(to_world(l["u"], l["v"])[0]), "z": float(to_world(l["u"], l["v"])[1])} for l in labels]},
                          indent=1))
print(f"wrote {OUT.relative_to(ROOT)}: {len(boxes)} boxes, {len(routes)} routes; replaces "
      f"{sum(replaced(b['name']) for b in META['boxes'])} placeholders + {len(auto_replaced)} terrace-wall strips")

# ---------------------------------------------------------------- review sheet (frame view, u right, v up)

PX = 7
W_, H_ = int(HALF_U * 2 * PX) + 80, int(HALF_V * 2 * PX) + 80


def to_px(u, v):
    return 40 + (u + HALF_U) * PX, 40 + (HALF_V - v) * PX


img = Image.new("RGB", (W_, H_), (236, 232, 222))
d = ImageDraw.Draw(img)
fu, fv = UU[::2, ::2], VV[::2, ::2]
inside = (np.abs(fu) <= HALF_U) & (np.abs(fv) <= HALF_V)
for m, col in ((mask[::2, ::2], (205, 185, 150)), (rav[::2, ::2], (170, 200, 215))):
    for uu, vv in zip(fu[m & inside], fv[m & inside]):
        x, y = to_px(uu, vv)
        d.rectangle([x - PX * 0.55, y - PX * 0.55, x + PX * 0.55, y + PX * 0.55], fill=col)


def draw_box(b, fill, outline):
    du, dv = b.get("du"), b.get("dv")
    if du is None:
        y = math.radians(b["yaw"])
        fx, fz = math.sin(y), math.cos(y)
        du, dv = fx * COS + fz * SIN, -fx * SIN + fz * COS
        cu, cv = to_frame(b["x"], b["z"])
    else:
        cu, cv = b["u"], b["v"]
    n = math.hypot(du, dv)
    du, dv = du / n, dv / n
    L, Wd = b["sz"], b["sx"]
    corners = [(cu + sa * du * L / 2 - sb * dv * Wd / 2, cv + sa * dv * L / 2 + sb * du * Wd / 2) for sa, sb in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
    d.polygon([to_px(*c) for c in corners], fill=fill, outline=outline)


for b in kept_boxes:
    if b["group"] != "Walls" and "outside" not in b["name"]:
        draw_box(b, (200, 200, 200), (150, 150, 150))
cols = {"wall": (120, 120, 125), "low": (160, 150, 120), "walk": (190, 140, 80), "cover": (110, 140, 100),
        "trim": (40, 40, 40), "foliage": (90, 150, 90), "bark": (100, 80, 60)}
for b in sorted(boxes, key=lambda b: b["y"] + b["sy"] / 2):
    if b.get("overhead"):
        continue
    draw_box(b, cols.get(b["mat"], (255, 0, 255)), (30, 30, 30))
for b in boxes:
    if b.get("overhead"):
        draw_box(b, None, (40, 120, 40))
for l in LAYOUT["lanes"]:
    p = chaikin(l["points"])
    d.line([to_px(*q) for q in p], fill=(150, 90, 40), width=1)
for r in routes:
    uu, vv = to_frame(np.array(r["xs"]), np.array(r["zs"]))
    d.line([to_px(a, b) for a, b in zip(uu, vv)], fill=(220, 30, 30), width=2)
d.rectangle([*to_px(-HALF_U, HALF_V), *to_px(HALF_U, -HALF_V)], outline=(0, 0, 0))
try:
    f = ImageFont.truetype("arial.ttf", 14)
except OSError:
    f = ImageFont.load_default()
for pid, at in [(s["id"], s["at"]) for s in LAYOUT["spawns"]] + [(fl["id"], fl["at"]) for fl in LAYOUT["flags"]]:
    x, y = to_px(*at)
    d.ellipse([x - 5, y - 5, x + 5, y + 5], outline=(200, 0, 0), width=2)
    d.text((x + 7, y - 7), pid, fill=(0, 0, 0), font=f)
for u in range(-70, 71, 10):
    x, y = to_px(u, -HALF_V)
    d.text((x - 8, y + 4), str(u), fill=(0, 0, 0), font=f)
for v in range(-50, 51, 10):
    x, y = to_px(-HALF_U, v)
    d.text((x - 32, y - 7), str(v), fill=(0, 0, 0), font=f)
d.text((40, 10), "Task 8 greybox - frame view (u east along the main street, v uphill). Grey = placeholders kept; "
       "dark = blocks, tan = low roofs, orange = walkable, green = cover, black = trim/rails, red = walk routes",
       fill=(0, 0, 0), font=f)
SHOTS.mkdir(parents=True, exist_ok=True)
img.save(SHOTS / "greybox_plan.png")
print(f"sheet {SHOTS / 'greybox_plan.png'}")
