"""Greybox for LOCKDOWN (Tasks 8-14): grey layout blocks on the Task 7 play ground, from the layout data.

Run from the project root, after lockdown_playground.py:
    uv run --with numpy --with scipy --with pillow Tools/layout/lockdown_greybox.py

Reads Assets/Lockdown/Data/Layout/lockdown_layout.json ("greybox" block, one section per task) and the play ground
(SourceData/playground/play_513.r16, play_mask.png, play_meta.json), and writes
    SourceData/playground/greybox_meta.json   boxes for Lockdown/Build/Greybox, the placeholders they replace,
                                              walk-test routes
and a review sheet Screenshots/task<NN>/greybox_plan.png (never Assets/).

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
TASK = max(sec["task"] for sec in LAYOUT["greybox"]["sections"].values())
SHOTS = ROOT / "Screenshots" / f"task{TASK:02d}"

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


def name_matches(name, p):
    """A `replaces` entry is a name prefix; one ending in "$" is an exact name ("Olive press$" is the press block only,
    not the "Olive press terrace wall" strips)."""
    return name == p[:-1] if p.endswith("$") else name.startswith(p)


def replaced(name):
    return any(name_matches(name, p) for p in all_replaces)


# Task 7's terrace-wall strips only mark terrace risers: a greybox block may stand over them, and then replaces them.
SOFT = ("Terrace wall ",)
kept_boxes, kept = [], None


def set_kept(task):
    """The placeholders a section keeps clear of: those not replaced by a section of its own task or an earlier one.
    The ravine section's replacements (the bridge decks) stay in the keepouts, and a later task's replacements never
    reach an earlier section, so a locked section regenerates exactly as it was built."""
    global kept_boxes, kept
    earlier = [p for sec in GB["sections"].values() if sec.get("kind") != "ravine" and sec["task"] <= task
               for p in sec.get("replaces", [])]
    kept_boxes = [b for b in META["boxes"] if not any(name_matches(b["name"], p) for p in earlier)]
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
    if a.get("greybox_keepout", True):   # an area added after its neighbours were built opts out, so they stay put
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
ribbons = []   # draped meshes (stone paths): rows of vertices across the path, world coordinates
stone_chunks = []   # stone path flagstones: chunked meshes (LOD0 + LOD1), world coordinates, built by LockdownGreybox.cs


def stone_path(c, cs, cu_, cv_, n_start, n, width, ST, lift, key):
    """A kalderimi of individual rounded cobbles (lockdown_stones.py packs them in plan, so a stone has the same size on
    flat ground, ramp and mouths). Each stone is a 3D domed/pillowed cobble draped on the ground: a raised centre
    apex, a domed top surface with a 0.5-2 degree tilt, and a tight ~1.1 cm steep outer bevel into the ground joint.
    No kerbs. The Task 7 ramp stays the walking surface (everything here is visual)."""
    import lockdown_stones as vs
    base_lift = ST.get("base", 0.028)
    path = vs.Path(cs[n_start:n], cu_[n_start:n], cv_[n_start:n], width - 0.1, ST["seed"], ST.get("wobble", 0.15))
    s0, s1 = float(cs[n_start]), float(cs[n - 1])
    stones = vs.pack(path, ST, ST["seed"], s0, s1)
    bevel = 0.011
    chunk_len = ST.get("chunk", 6.0)
    chunks = {}
    for st in stones:
        P = np.array(st["poly"])
        n_ = len(P)
        cen = np.array(vs._centroid(st["poly"]))
        e = np.roll(P, -1, axis=0) - P
        el = np.hypot(e[:, 0], e[:, 1])
        dist = np.abs(e[:, 0] * (cen[1] - P[:, 1]) - e[:, 1] * (cen[0] - P[:, 0])) / np.maximum(el, 1e-9)
        f = float(np.clip(1.0 - bevel / max(float(dist.min()), 0.022), 0.78, 0.90))
        inner = cen + (P - cen) * f
        g = sample_uv(P[:, 0], P[:, 1]) + base_lift          # the ground under the outer ring
        gi = sample_uv(inner[:, 0], inner[:, 1]) + base_lift
        gc = float(sample_uv(cen[0], cen[1])) + base_lift
        tilt = np.array(st["tilt"])
        crown = st.get("crown", 0.016)
        top = gi + st["proud"] + (inner - cen) @ tilt
        top = np.maximum(top, gi + 0.004)
        top_c = max(gc + st["proud"] + crown, float(top.mean()) + 0.85 * crown)
        cx_w, cz_w = to_world(cen[0], cen[1])
        ox, oz = to_world(P[:, 0], P[:, 1])
        ix, iz = to_world(inner[:, 0], inner[:, 1])
        k = int(min(max((st["s"] - s0) // chunk_len, 0), max(int((s1 - s0) // chunk_len), 0)))
        ch = chunks.setdefault(k, {"v0": [], "t0": [], "u0": [], "v1": [], "t1": [], "u1": []})
        uv = [(st["tone"] + 0.5) / 32.0, 0.25]
        uv_rim = [(st["tone"] + 0.5) / 32.0, 0.75]
        b0 = len(ch["v0"])
        # LOD0: 0 = centre dome apex, 1..n_ = inner shoulder ring, n_+1..2*n_ = outer base ring
        ch["v0"].append((float(cx_w), float(top_c), float(cz_w)))
        for i in range(n_):
            ch["v0"].append((float(ix[i]), float(top[i]), float(iz[i])))
        for i in range(n_):
            ch["v0"].append((float(ox[i]), float(g[i]), float(oz[i])))
        ch["u0"] += [uv] * (1 + n_) + [uv_rim] * n_
        for i in range(n_):
            j = (i + 1) % n_
            ch["t0"] += [b0, b0 + 1 + i, b0 + 1 + j]
            ch["t0"] += [b0 + 1 + i, b0 + 1 + n_ + i, b0 + 1 + j, b0 + 1 + j, b0 + 1 + n_ + i, b0 + 1 + n_ + j]
        b1 = len(ch["v1"])                                     # LOD1: the stone's rounded outline as one flat fan
        for i in range(n_):
            ch["v1"].append((float(ox[i]), float(g[i] + st["proud"] * 0.7 + crown * 0.4), float(oz[i])))
        ch["u1"] += [uv] * n_
        for i in range(1, n_ - 1):
            ch["t1"] += [b1, b1 + i, b1 + i + 1]
    nv0 = nv1 = 0
    for k in sorted(chunks):
        ch = chunks[k]
        o = ch["v0"][0]
        nv0 += len(ch["v0"])
        nv1 += len(ch["v1"])
        rnd = lambda vs_: [round(a - b, 4) for v in vs_ for a, b in zip(v, o)]
        stone_chunks.append({"name": f"{c['name']} stones {k:02d}", "section": key, "origin": [round(a, 4) for a in o],
                             "v0": rnd(ch["v0"]), "uv0": [round(a, 4) for u in ch["u0"] for a in u], "t0": ch["t0"],
                             "v1": rnd(ch["v1"]), "uv1": [round(a, 4) for u in ch["u1"] for a in u], "t1": ch["t1"]})
    print(f"  stone path {c['name']}: {len(stones)} flagstones in {len(chunks)} chunks, {nv0} / {nv1} vertices LOD0 / LOD1")


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
    prev = placed_all()   # blocks of the sections before this one
    occupied = keep_lane | conn | rav | kept | prev
    placed = np.zeros(UU.shape, bool)
    plateia = next((a for a in LAYOUT["areas"] if a["id"] == "plateia"), None)
    plateia_h = sample_uv(*plateia["center"]) if plateia else None

    def ref_height(ref, cu, cv):
        if ref == "plateia":
            return plateia_h
        if ref == "ground":
            return sample_uv(cu, cv)
        if ref == "b":   # metres above the Flag B height, as in the layout's height_above_flag_b
            return B
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
                fp = part(lm["name"], p, h0, group, *p.get("dir", (1.0, 0.0)))   # "dir": the part's length runs along (du, dv)
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
                name = f"House {row['side']} {house_count:03d}"
                if name not in sec.get("drop_houses", []):   # a later decision clears this house; its plot stays reserved,
                    box(key, name, "Buildings", "wall", cu, cv, gmin - 0.3, top, w, d, tu, tv)   # so every other house
                    placed |= fp                                                               # stays where it was
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
        wall_keep = others | conn | rav | kept | prev
        for side in (1, -1):
            if side > 0 and not W.get("north"):   # no north walls (the upper lane: the cliff is the north side)
                continue
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
            if (fp & ((rav | kept | others | placed | prev) if st.get("cross_connectors") else (conn | rav | kept | others | placed | prev))).any():
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
            if po.get("on_ground"):
                # Stand on open, level ground: the first offset whose 1.2 m footprint is clear of every lane, pad,
                # connector, the ravine, kept placeholders and blocks, with under 0.35 m of ground relief.
                spot = None
                for sd in [side] + ([-side] if po.get("either_side") else []):   # the other road edge if this one has no spot
                    nu, nv = -tv * sd, tu * sd
                    for o in (po["offset"], po["offset"] + 0.7, po["offset"] + 1.4):
                        cu, cv = lu[i] + nu * o, lv[i] + nv * o
                        fp = footprint(cu, cv, 1.0, 1.0, tu, tv)
                        if not fp.any() or (fp & (conn | rav | kept | prev | placed | keep_lane)).any():
                            continue
                        gmin, gmax, _ = ground_range(fp)
                        if gmax - gmin <= 0.5:
                            spot = (cu, cv, gmin, gmax)
                            break
                    if spot:
                        break
                if spot is None:
                    continue
                cu, cv, gmin, gmax = spot
                if f"{po['name']} {k + 1:02d}" in sec.get("drop_posts", []):   # a later decision keeps this spot clear
                    continue
                box(key, f"{po['name']} {k + 1:02d}", "Cover", po["mat"], cu, cv, gmin - 0.3, gmax + po["z"][1],
                    po["size"], po["size"], tu, tv, shape="cylinder")
                placed |= footprint(cu, cv, 0.6, 0.6, tu, tv)
                continue
            cu, cv = lu[i] + nu * po["offset"], lv[i] + nv * po["offset"]
            rr, cc = grid_rc(cu, cv)
            if conn[rr, cc] or rav[rr, cc] or placed[rr, cc] or prev[rr, cc]:
                continue
            box(key, f"{po['name']} {k + 1:02d}", "Cover", po["mat"], cu, cv, prof[i] + po["z"][0], prof[i] + po["z"][1],
                po["size"], po["size"], tu, tv, shape="cylinder")

    # Stairs: treads on a connector's Task 7 terrain ramp (the terrain stays as approved). Each tread runs until the
    # ground under its centreline has risen one riser, and its top is the highest ground along it, so every riser is
    # at most one riser tall and the tread never dips into the ramp. Flat stretches (landings) stay ground. Alley walls
    # line both sides where nothing else stands; a sloped pipe handrail runs along one side of each flight.
    # A stair with its own "points" (an outside house stair) climbs straight from z[0] (null = the ground at its foot)
    # to z[1], both above Flag B, instead of following a connector's ramp.
    for stc in sec.get("stairs", []):
        if "points" in stc:
            c = {"name": stc["name"], "width": stc["width"], "points": stc["points"]}
            cp = np.asarray(stc["points"], float)
            cs, cu_, cv_ = stations(cp, 0.02 if stc.get("even") else 0.1)
            z0 = sample_uv(*cp[0]) if stc["z"][0] is None else B + stc["z"][0]
            ch = z0 + (B + stc["z"][1] - z0) * cs / cs[-1]
        else:
            c = next(c for c in LAYOUT["connectors"] if c["name"] == stc["along"])
            cp = connector_path(c)
            cs, cu_, cv_ = stations(cp, 0.1)
            ch = sample_uv(cu_, cv_)
        width, riser = stc.get("width", c["width"]), stc.get("riser", 0.18)
        own = polyline_distance(cp) <= width / 2 + 0.15
        PV = stc.get("paving")
        if PV:
            # A stone path (kalderimi) instead of treads: the Task 7 ramp stays the walking surface; pitched stone slabs
            # lie a few cm above it, with low kerb stones along both edges and a stone rib across every few metres.
            # All visual (no colliders), so the ramp, the walk test and the checks stay exactly as approved.
            lift, piece = PV.get("lift", 0.05), PV.get("piece", 1.6)
            skip_start = PV.get("skip_start", 0.0)
            skip_end = PV.get("skip_end", 0.0)
            n_start = 0
            n = len(cs)
            if skip_start > 0.0:
                n_start = min(next((idx for idx, s in enumerate(cs) if s >= skip_start), 0), n - 2)
            if skip_end > 0.0:
                s_max = cs[-1] - skip_end
                n = max(next((idx for idx, s in enumerate(cs) if s >= s_max), n), n_start + 2)
            # tight turns (more than turn_deg within 3 m, e.g. a hairpin): short slabs there so long chords don't fan
            # out across each other, and no kerbs or ribs (they would fold on the inside of the bend)
            tg = np.gradient(np.stack([cu_, cv_], 1), axis=0)
            hd = np.unwrap(np.arctan2(tg[:, 1], tg[:, 0]))
            w15 = 15
            tight = np.abs(hd[np.minimum(np.arange(n) + w15, n - 1)] - hd[np.maximum(np.arange(n) - w15, 0)]) > math.radians(PV.get("turn_deg", 40))

            def runs(step, short=None):
                i = n_start
                while i < n - 1:
                    j = i
                    lim = short if (short and tight[i]) else step
                    while j + 1 < n and cs[j + 1] - cs[i] < lim and not (short and tight[j + 1] != tight[i]):
                        j += 1
                    if j == i:
                        j = i + 1
                    yield i, j
                    i = j

            # station normals (tangent smoothed over +-0.5 m): every strip follows its own offset curve, so slabs meet
            # end to end through a bend instead of fanning out, and each sits on the ground under its own line
            st = np.array([tg[max(k - 5, 0):k + 6].mean(0) for k in range(n)])
            st /= np.linalg.norm(st, axis=1, keepdims=True)
            nu_, nv_ = -st[:, 1], st[:, 0]

            def lay(name, mat, i, j, off, w, top, depth, length=None):
                au, av = cu_[i] + nu_[i] * off, cv_[i] + nv_[i] * off
                bu, bv = cu_[j] + nu_[j] * off, cv_[j] + nv_[j] * off
                tu, tv = bu - au, bv - av
                L = math.hypot(tu, tv)
                if L < 0.05:
                    return
                cu0, cv0 = cu_[j] - cu_[i], cv_[j] - cv_[i]   # inside a bend tighter than the offset the strip folds back:
                if (tu * cu0 + tv * cv0) < 0.5 * math.hypot(cu0, cv0) ** 2:   # leave that bit of the inner corner bare
                    return
                ha, hb = sample_uv(au, av), sample_uv(bu, bv)
                dh = hb - ha
                box(key, name, "Paths", mat, (au + bu) / 2, (av + bv) / 2, (ha + hb) / 2 + top - depth, (ha + hb) / 2 + top,
                    (length or math.hypot(L, dh)) + 0.03, w, tu, tv, pitch=-math.degrees(math.atan2(dh, L)), collider=False)

            ST = PV.get("stones")
            if ST is not None:
                stone_path(c, cs, cu_, cv_, n_start, n, width, ST, PV.get("lift", 0.04), key)
            else:
                # Paving: one draped ribbon mesh, vertices every ~0.25 m along and 5 across, each on the ground under it plus
                # lift, so it follows the ramp both along and across (hairpins on a slope included). Inside a bend tighter
                # than the half-width the inner edge is pulled in (0.8 x the bend radius) so the mesh never folds over.
                W = width - 0.1
                cols = 5
                sel = list(range(n_start, n, 2)) + ([n - 1] if (n - 1 - n_start) % 2 else [])
                xs_, ys_, zs_ = [], [], []
                for kk in sel:
                    a0, a1 = max(kk - 5, 0), min(kk + 5, n - 1)
                    dth = float(hd[a1] - hd[a0])
                    R = (cs[a1] - cs[a0]) / max(abs(dth), 1e-6)
                    for q in range(cols):
                        o = -W / 2 + W * q / (cols - 1)
                        if o * dth > 0 and abs(o) > 0.8 * R:
                            o = math.copysign(0.8 * R, o)
                        pu, pv = cu_[kk] + nu_[kk] * o, cv_[kk] + nv_[kk] * o
                        x, z = to_world(pu, pv)
                        xs_.append(float(x))
                        zs_.append(float(z))
                        ys_.append(float(sample_uv(pu, pv) + lift))
                ribbons.append({"name": f"{c['name']} paving", "mat": "stone", "section": key, "cols": cols, "xs": xs_, "ys": ys_, "zs": zs_})
                # Natural starting threshold rib across the upper mouth
                if skip_start > 0.0 and (n - n_start) > 2:
                    lay(f"{c['name']} threshold upper", "stone_trim", n_start, min(n_start + 4, n - 1), 0.0, width - 0.1, lift + 0.02, 0.22, length=0.35)
                k = 0
                for i, j in runs(piece * 2, short=PV.get("turn_piece", 0.4)):
                    k += 1
                    for side in (1, -1):
                        lay(f"{c['name']} kerb {k:03d}{'L' if side > 0 else 'R'}", "stone_trim", i, j, side * (width / 2 - 0.13), 0.24,
                            lift + PV.get("kerb", 0.1), 0.3)
                k = 0
                for i, j in runs(PV.get("rib", 4.5)):
                    jj = min(i + 3, n - 1)
                    if tight[i:jj + 1].any():
                        continue
                    k += 1
                    lay(f"{c['name']} rib {k:03d}", "stone_trim", i, jj, 0.0, width - 0.4, lift + 0.03, 0.2, length=0.3)
                # Natural ending threshold rib across the lower mouth
                if skip_end > 0.0 and (n - n_start) > 2:
                    lay(f"{c['name']} threshold lower", "stone_trim", max(n - 4, n_start), n - 1, 0.0, width - 0.1, lift + 0.02, 0.22, length=0.35)
                print(f"  stone path {c['name']}: paving, kerbs and ribs on the ramp (no treads)")
        other_conn = np.zeros(UU.shape, bool)
        for c2 in LAYOUT["connectors"]:
            if c2["name"] != c["name"]:
                other_conn |= polyline_distance(connector_path(c2)) <= c2["width"] / 2 + 0.5
        treads = []   # (i, j, top)
        i, n = 0, len(cs)
        if PV:
            i = n   # a stone path has no treads
        if stc.get("even") and "points" in stc:   # equal risers: round(rise / riser) steps, none shorter than the rest
            m = max(int(round(abs(ch[-1] - ch[0]) / riser)), 1)
            cut = [int(np.searchsorted(cs, cs[-1] * k / m)) for k in range(m + 1)]
            treads = [(cut[k], min(cut[k + 1], n - 1), float(ch[0] + (ch[-1] - ch[0]) * (k + 1) / m)) for k in range(m)]
            i = n
        while i < n - 1:
            j = min(i + 10, n - 1)
            if abs(ch[j] - ch[i]) / max(cs[j] - cs[i], 1e-6) < stc["min_grade"]:
                i += 1
                continue
            j = i + 1
            while j < n - 1 and abs(ch[j] - ch[i]) < riser and cs[j] - cs[i] < stc["max_tread"]:
                j += 1
            treads.append((i, j, float(ch[i:j + 1].max())))
            i = j
        if stc.get("exclude"):   # no treads inside these frame circles [u, v, r]: the ground stays a ramp there (e.g. the last
            # 5 m before a wall, so no stair ends at a wall)
            ex = stc["exclude"]
            treads = [t for t in treads
                      if not any(math.hypot((cu_[t[0]] + cu_[t[1]]) / 2 - e[0], (cv_[t[0]] + cv_[t[1]]) / 2 - e[1]) <= e[2] for e in ex)]
        if stc.get("clear_of_boxes"):   # no tread under a bridge deck or another block already standing (the ground stays)
            def covered(t):
                i, j, _ = t
                return (footprint((cu_[i] + cu_[j]) / 2, (cv_[i] + cv_[j]) / 2, cs[j] - cs[i] + 0.04, width,
                                  cu_[j] - cu_[i], cv_[j] - cv_[i]) & (prev | kept)).any()
            treads = [t for t in treads if not covered(t)]
        for k, (i, j, top) in enumerate(treads):
            tu, tv = cu_[j] - cu_[i], cv_[j] - cv_[i]
            L = math.hypot(tu, tv)
            cu, cv = (cu_[i] + cu_[j]) / 2, (cv_[i] + cv_[j]) / 2
            fp = footprint(cu, cv, L + 0.04, width, tu, tv)
            gmin = ground_range(fp)[0] if fp.any() else top
            box(key, f"{c['name']} tread {k + 1:03d}", "Stairs", "walk", cu, cv, min(gmin, top) - 0.3, top, L + 0.04, width, tu, tv,
                walk_top=True)
            placed |= fp
        # flights: runs of treads that touch each other
        flights, run = [], []
        for t in treads:
            if run and t[0] != run[-1][1]:
                flights.append(run)
                run = []
            run.append(t)
        if run:
            flights.append(run)
        RL = None if PV else stc.get("rail")
        if RL:
            # One continuous pipe rail per flight, the way a real one is bent and welded: it follows the stair side
            # through the nosings, straight runs become single long pipes (Douglas-Peucker, 5 cm), pipes meet at the
            # bends, and posts stand at every bend and at most max_post apart. Flights split by a short landing
            # (< bridge m) keep one rail across it.
            sg, off, hgt = RL["side"], width / 2 - 0.1, RL["height"]
            top_at = ch.copy()
            for i, j, top in treads:
                top_at[i:j + 1] = top
            ctang = np.gradient(np.stack([cu_, cv_], 1), axis=0)
            ctang = np.array([ctang[max(k - 5, 0):k + 6].mean(0) for k in range(n)])
            ctang /= np.linalg.norm(ctang, axis=1, keepdims=True)
            groups = []
            for fl in flights:
                if groups and cs[fl[0][0]] - cs[groups[-1][-1][1]] < RL.get("bridge", 1.5):
                    groups[-1] = groups[-1] + fl
                else:
                    groups.append(list(fl))
            if RL.get("skip"):   # the rail starts this far up the stair, so the foot can be stepped onto from the side
                groups = [[t for t in g if cs[t[0]] - cs[g[0][0]] >= RL["skip"]] for g in groups]
            # Tight turns (more than 90 deg within 3 m, a radius under ~1.9 m, tighter than the rail's offset can
            # follow): the rail stops at a post before the turn and starts again after it, instead of an offset curve
            # that folds back on itself on the inside of a hairpin. No rail shorter than min_rail.
            hd = np.unwrap(np.arctan2(ctang[:, 1], ctang[:, 0]))
            turn = np.abs(hd[np.minimum(np.arange(n) + 15, n - 1)] - hd[np.maximum(np.arange(n) - 15, 0)])
            split = []
            for g in groups:
                run = []
                for t in g:
                    if turn[t[0]:t[1] + 1].max() > math.radians(RL.get("max_turn", 90)):
                        if run:
                            split.append(run)
                        run = []
                    else:
                        run.append(t)
                if run:
                    split.append(run)
            groups = [g for g in split if cs[g[-1][1]] - cs[g[0][0]] >= RL.get("min_rail", 2.5)]

            def rail_point(i, hh):
                return np.array([cu_[i] - ctang[i][1] * sg * off, cv_[i] + ctang[i][0] * sg * off, hh, i], float)

            def simplify(pts, tol=RL.get("tolerance", 0.15)):
                if len(pts) < 3:
                    return pts
                a, b = pts[0][:3], pts[-1][:3]
                ab = b - a
                L2 = float(ab @ ab) or 1e-9
                d = [np.linalg.norm(p[:3] - (a + np.clip((p[:3] - a) @ ab / L2, 0, 1) * ab)) for p in pts[1:-1]]
                k = int(np.argmax(d)) + 1
                if d[k - 1] <= tol:
                    return [pts[0], pts[-1]]
                return simplify(pts[:k + 1], tol)[:-1] + simplify(pts[k:], tol)

            n_rail = 0
            for f, g in enumerate(groups):
                pts = [rail_point(i, top + hgt) for i, j, top in g] + [rail_point(g[-1][1], g[-1][2] + hgt)]
                vs = simplify(pts)
                while len(vs) > 2:   # no pipe shorter than min_pipe: drop the bend that makes the shortest one
                    seg = [math.hypot(b[0] - a[0], b[1] - a[1]) for a, b in zip(vs[:-1], vs[1:])]
                    k = int(np.argmin(seg))
                    if seg[k] >= RL.get("min_pipe", 0.8):
                        break
                    del vs[k + 1 if k + 1 < len(vs) - 1 else k]
                posts = []
                for a, b in zip(vs[:-1], vs[1:]):
                    du, dv, dh = b[0] - a[0], b[1] - a[1], b[2] - a[2]
                    L = math.hypot(du, dv)
                    if L < 0.05:
                        continue
                    n_rail += 1
                    box(key, f"{c['name']} handrail {f + 1}-{n_rail:02d}", "Stairs", "trim", (a[0] + b[0]) / 2, (a[1] + b[1]) / 2,
                        (a[2] + b[2]) / 2 - 0.03, (a[2] + b[2]) / 2 + 0.03, math.hypot(L, dh) + 0.06, 0.06, du, dv, rail=True,
                        pitch=-math.degrees(math.atan2(dh, L)))
                    m = max(int(math.ceil(L / RL.get("max_post", 1.8))), 1)
                    posts += [a + (b - a) * q / m for q in range(m)]
                posts.append(vs[-1])
                kept_posts = [posts[0]]   # no two posts closer than min_post (a bend right after a post gets none)
                for p in posts[1:]:
                    if math.hypot(p[0] - kept_posts[-1][0], p[1] - kept_posts[-1][1]) >= RL.get("min_post", 1.2):
                        kept_posts.append(p)
                    elif p is posts[-1]:
                        kept_posts[-1] = p
                posts = kept_posts
                for q, p in enumerate(posts):
                    base = float(top_at[int(round(p[3]))])
                    box(key, f"{c['name']} handrail {f + 1} post {q + 1:02d}", "Stairs", "trim", p[0], p[1], base, p[2] + 0.03, 0.05, 0.05,
                        rail=True)
        AW = stc.get("walls")
        k_w = 0
        if AW:
            keep = own | other_conn | others | own_lane(lid) | rav | kept | prev | placed
            for side in (1, -1):
                i = 0
                while i < n - 1:
                    j = i
                    while j + 1 < n and cs[j + 1] - cs[i] < AW["max_piece"]:
                        j += 1
                    tu, tv = cu_[j] - cu_[i], cv_[j] - cv_[i]
                    L = math.hypot(tu, tv)
                    if L >= 0.9:
                        nu, nv = -tv / L * side, tu / L * side
                        off = width / 2 + AW["gap"] + AW["thickness"] / 2
                        mu, mv = (cu_[i] + cu_[j]) / 2 + nu * off, (cv_[i] + cv_[j]) / 2 + nv * off
                        fp = footprint(mu, mv, L + 0.2, AW["thickness"], tu, tv)
                        if fp.any() and not (fp & keep).any() and inside_frame(mu, mv, L + 0.2, AW["thickness"], tu, tv):
                            gmin = ground_range(fp)[0]
                            k_w += 1
                            box(key, f"{c['name']} alley wall {k_w:03d}", "Walls", "wall", mu, mv, min(gmin, float(ch[i:j + 1].min())) - 0.3,
                                float(ch[i:j + 1].max()) + AW["height"], L + 0.2, AW["thickness"], tu, tv)
                    i = j
        print(f"  stairs {c['name']}: {len(treads)} treads in {len(flights)} flights, {k_w} alley-wall pieces")


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


# ---------------------------------------------------------------- retaining walls and closures

extensions = []   # frame extensions (boundary jogs), frame coordinates; the Containment Test counts them as inside
closures = []   # lines no walker move may cross (checked by Lockdown/Review/Containment Test), world coordinates


def retaining_wall(key, w, rng, group="Walls"):
    """A rubble terrace retaining wall along a polyline (frame u, v): overlapping courses 2-3 m long that follow it.
    Every course's top stands `above` metres (plus a seeded 0..jitter) over the HIGHEST ground on either face out to
    `reach` metres (a sprint jump's run-up), so it can't be jumped, climbed or mantled from either side and dropping
    off it is no shortcut; thickness is seeded per course,
    and a wider footing on the low side gives the batter. `closure: true` registers the line for the physics check."""
    pts = np.asarray(w["points"], float)
    seg = np.hypot(*np.diff(pts, axis=0).T)
    cum = np.concatenate([[0], np.cumsum(seg)])
    t0, t1 = w.get("thickness", [0.7, 0.9])
    above, jit, lap = w.get("above", 1.5), w.get("jitter", 0.15), w.get("overlap", 0.35)
    s0, k = 0.0, 0
    while s0 < cum[-1] - 0.05:
        L = float(rng.uniform(*w.get("course", [2.0, 3.0])))
        s1 = min(s0 + L, cum[-1])
        if cum[-1] - s1 < 0.8:   # no stub at the end
            s1 = cum[-1]
        ua, va = np.interp(s0, cum, pts[:, 0]), np.interp(s0, cum, pts[:, 1])
        ub, vb = np.interp(s1, cum, pts[:, 0]), np.interp(s1, cum, pts[:, 1])
        du, dv = ub - ua, vb - va
        n = math.hypot(du, dv)
        du, dv = du / n, dv / n
        cu, cv = (ua + ub) / 2, (va + vb) / 2
        th = float(rng.uniform(t0, t1))
        side, near = [], []
        ext = max(w.get("reach", [1.5, 2.5, 4.0]))   # also along the wall: a run-up from higher ground beside it
        for f in np.linspace(-0.5 * n - ext, 0.5 * n + ext, 9):   # ground on both faces, out to a sprint jump's run-up
            pu, pv = cu + du * f, cv + dv * f
            row = []
            for o in [0.6] + list(w.get("reach", [1.5, 2.5, 4.0])):
                row.append([sample_uv(pu - dv * (th / 2 + o), pv + du * (th / 2 + o)),
                            sample_uv(pu + dv * (th / 2 + o), pv - du * (th / 2 + o))])
            row = np.array(row)
            if abs(f) <= 0.5 * n + 1e-6:
                near.append(row[0])
            side.append(row.max(axis=0))
        side, near = np.array(side), np.array(near)
        hi, lo = float(side.max()), float(near.min())
        top = hi + above + float(rng.uniform(0.0, jit))
        box(key, f"{w['name']} course {k:02d}", group, w.get("mat", "rubble"), cu, cv, lo - 0.4, top, n + 2 * lap, th, du, dv)
        if hi - lo > 0.8:   # footing on the low face (battered base)
            low_face = 1.0 if near[:, 1].mean() < near[:, 0].mean() else -1.0
            fu, fv = cu + dv * low_face * (th / 2 + 0.06), cv - du * low_face * (th / 2 + 0.06)
            box(key, f"{w['name']} footing {k:02d}", group, w.get("mat", "rubble"), fu, fv, lo - 0.4, lo + 0.45,
                n + 2 * lap, 0.24, du, dv)
        s0, k = s1, k + 1
    if w.get("closure"):
        x, z = to_world(pts[:, 0], pts[:, 1])
        closures.append({"name": w["name"], "xs": [float(a) for a in x], "zs": [float(a) for a in z]})


# ---------------------------------------------------------------- boundary (Task 16, pulled forward in Task 9 r10)

def _foot(cu, cv, du, dv, L, w, n=5):
    """Ground under a footprint (L along (du, dv), w across): lowest and highest of an n x 3 sample."""
    g = [sample_uv(cu + du * s + dv * a, cv + dv * s - du * a)
         for s in np.linspace(-L / 2, L / 2, n) for a in (-w / 2, 0.0, w / 2)]
    return float(min(g)), float(max(g))


def _row_ground(u, vc, sgn, half_row):
    """Ground across a lane at frame-u `u`: its range over the row's span (how far from flat a barrier row would sit)."""
    g = sample_uv(np.full(9, u), vc + np.linspace(-half_row, half_row, 9))
    return float(np.max(g) - np.min(g))


def roadblock(key, rb, sec, rr, sgn, ub, vc, lane_w, base, H, flat, sc):
    """A police roadblock across a lane end: 2-3 tall precast jersey barriers (scale `sc`) in one loose row across the
    lane floor, a few centimetres apart, each turned and nudged a little. Each barrier lies along the ground: tilted
    (`pitch`) with the slope across the lane, its base on the highest ground under it (buried a few cm at most).
    Returns the barriers (for the wall on their outer face) and the row's two ends (v offsets from vc)."""
    BR = sec["barrier"]
    nm = rb["name"]
    span = lane_w + 2 * float(BR.get("span_margin", 0.3))
    lo_len, hi_len = BR["length"]
    n = max(2, int(math.ceil(span / hi_len)))
    # a narrow lane gets shorter barriers (precast ones come down to ~2 m), never longer ones running up the banks
    lens = [max(float(BR.get("min_len", 2.0)), span / n) * float(rr.uniform(0.95, 1.05)) for _ in range(n)]
    joints = [float(rr.uniform(*BR.get("joint", [0.04, 0.1]))) for _ in range(n - 1)]
    total = sum(lens) + sum(joints)
    pmax = math.tan(math.radians(float(BR.get("pitch_max", 8.0))))
    nud = BR.get("row_nudge", [0.04, 0.12])
    side = 1.0 if rr.uniform() < 0.5 else -1.0
    slot = [v * sc for v in BR["slot"]]
    cells, t = [], -total / 2
    for k, L in enumerate(lens):
        ang = math.radians(float(rr.uniform(-BR["yaw_jitter"], BR["yaw_jitter"])))
        du, dv = math.sin(ang) * sgn, math.cos(ang)
        cu = ub + side * float(rr.uniform(*nud))
        side = -side
        cv = vc + t + L / 2
        # the ground under it: highest across the base at 7 stations along its length
        st = np.linspace(-L / 2, L / 2, 7)
        g = np.array([max(sample_uv(cu + du * q + dv * w, cv + dv * q - du * w) for w in (-base / 2, 0.0, base / 2)) for q in st])
        k_ = float(np.clip(np.polyfit(st, g, 1)[0], -pmax, pmax))     # rise per metre along the barrier
        a0 = float(np.max(g - k_ * st)) - 0.03                       # base line at the centre: touches the highest point
        float_ = float(np.max(a0 + k_ * st - g))
        box(key, f"{nm} barrier {k}", "Boundary", "concrete_dark", cu, cv, a0, a0 + H, L, base, du, dv,
            shape="jersey", profile=flat, slot_w=slot[0], slot_h=slot[1], slot_in=slot[2], pitch=-math.degrees(math.atan(k_)))
        print(f"  {nm} barrier {k}: L {L:.2f} m, tilt {math.degrees(math.atan(k_)):+.1f} deg, bury 0.03 m, float {float_:.2f} m")
        cells.append((cu, cv, du, dv, L, float(g.min())))
        t += L + (joints[k] if k < n - 1 else 0.0)
    return cells, -total / 2, total / 2


def build_boundary(key, sec):
    """The map's edge (the 150 x 104 frame is the boundary line, everything outside is scenery):
    - roadblocks: every lane end gets 2-3 tall precast jersey barriers in one loose row across the lane floor (see
      `roadblock`), an invisible wall on their outer face lapped into both flanks, the old clip behind it as backup;
      every clip reaches `clip_top_b` over Flag B (a sky clip: a clip top must never be a walkway);
    - edges: a wall along each frame side just inside the line (courtyard / garden wall by data, shut gates every so
      often), each course `above` metres over the highest ground just inside it; where the inside ground is cliff top
      (`clip_above_b` over Flag B) the course is an invisible clip instead;
    - oob: a ring of OutOfBounds trigger volumes outside the frame line (backup: they put the walker back).
    The Containment Test (physics) decides whether it holds; nothing here moves an earlier section."""
    rng = np.random.default_rng(sec["seed"])
    inset = sec.get("inset", 0.45)
    BR = sec["barrier"]
    sc = float(BR.get("scale", 1.0))
    flat = [float(a) * sc for yh in BR["profile"] for a in yh]      # the profile as data, scaled (a tall jersey)
    H, base = BR["profile"][-1][0] * sc, 2 * BR["profile"][0][1] * sc
    gaps = {"N": [], "S": [], "E": [], "W": []}
    sec.setdefault("cameras", [])

    for ri, rb in enumerate(sec.get("roadblocks", [])):
        lid = rb["lane"]
        pts = lane_geo[lid][0]
        sgn = -1.0 if rb["end"] == "west" else 1.0
        ub = sgn * (HALF_U - rb.get("set_in", 2.4))     # the outer barrier row
        _, lu, lv = stations(pts, 0.25)
        vc = float(lv[np.argmin(np.abs(lu - ub))]) + rb.get("shift", 0.0)
        half = lanes[lid]["width"] / 2 + shoulder + rb.get("extra", 0.8)
        # the earlier single-row layout drew its numbers from `rng`; the walls below still need the same draws so
        # that their data (gates, jitter) stays exactly where it was
        t = -half + float(rng.uniform(-0.15, 0.1))
        while t < half - 0.4:
            L = float(rng.uniform(*BR["length"]))
            rng.uniform(-BR["yaw_jitter"], BR["yaw_jitter"]); rng.uniform(-BR["nudge"], BR["nudge"])
            t += L + float(rng.uniform(*BR["gap"]))
        # the row: on the flattest cross-section of the lane floor near its end (data may pin it with `set_in`)
        lane_w = lanes[lid]["width"]
        hr = lane_w / 2 + float(BR.get("span_margin", 0.3))
        if "set_in" in rb:
            si = float(rb["set_in"])
        else:
            lo_si, hi_si = BR.get("set_in_search", [2.0, 4.0])
            cand = [(_row_ground(sgn * (HALF_U - x), float(lv[np.argmin(np.abs(lu - sgn * (HALF_U - x)))]), sgn, hr), x)
                    for x in np.arange(lo_si, hi_si + 1e-6, 0.25)]
            si = min(cand)[1]
        ur = sgn * (HALF_U - si)
        vr = float(lv[np.argmin(np.abs(lu - ur))]) + rb.get("shift", 0.0)
        rb["_set_in"] = si
        print(f"  {rb['name']}: row at set_in {si:.2f} m (cross-lane range {_row_ground(ur, vr, sgn, hr):.2f} m)")
        n0 = len(boxes)
        cells, r0, r1 = roadblock(key, rb, sec, np.random.default_rng(sec["seed"] * 100 + 7 + ri), sgn, ur, vr, lane_w, base, H, flat, sc)
        assert all(bx.get("shape") == "jersey" for bx in boxes[n0:]), f"{rb['name']}: a cube in a roadblock"
        # the wall on the roadblock: an invisible clip on the OUTER face of every barrier (over the joints too), then
        # laps carrying the same line 3.5 m on into both flanks; the barrier is the last thing a player can touch
        top_clip = B + sec.get("clip_top_b", 30.0)
        for i, (cu, cv, du, dv, L, gmin) in enumerate(cells):
            nxt = cells[i + 1] if i + 1 < len(cells) else None
            ext = max((nxt[1] - nxt[4] / 2 - (cv + L / 2)) if nxt else 0.0, 0.0)
            box(key, f"{rb['name']} wall {i}", "Boundary", "trim", cu + sgn * (base / 2 + 0.1), cv + ext / 2, gmin - 0.6,
                top_clip, L + ext + 0.3, 0.3, du, dv, invisible=True)
        lapL = float(BR.get("lap", 3.5))
        for sd, end in ((1, r1), (-1, r0)):
            cvl = vr + end + sd * (lapL / 2 - 0.2)
            gl = [sample_uv(ur, cvl + z) for z in (-lapL / 2, 0.0, lapL / 2)]
            box(key, f"{rb['name']} wall lap {'N' if sd > 0 else 'S'}", "Boundary", "trim", ur + sgn * (base / 2 + 0.1), cvl, min(gl) - 0.6,
                top_clip, lapL, 0.3, 0.0, 1.0, invisible=True)
        uc = sgn * (HALF_U - inset)
        lap = rb.get("clip_lap", 1.2)
        span = np.linspace(vc - half - lap, vc + half + lap, 9)
        gs = [sample_uv(uc, v) for v in span]
        box(key, f"{rb['name']} clip", "Boundary", "trim", uc, vc, min(gs) - 0.5, max(max(gs) + rb.get("clip", 4.0), B + sec.get("clip_top_b", 30.0)),
            2 * (half + lap), 0.4, 0.0, 1.0, invisible=True)
        gaps["E" if sgn > 0 else "W"].append((vc - half, vc + half))
        nm = rb['name'].split(' roadblock')[0].replace(' ', '')
        sec["cameras"].append({"name": f"Cam_Grey_r10_Roadblock_{nm}",
                               "at": [ur - sgn * 10.0, vr - 0.8], "eye": 1.7, "look": [ur, vr, 0.6]})
        sec["cameras"].append({"name": f"Cam_Grey_r11_RoadblockBank_{nm}",
                               "at": [ur - sgn * 6.5, vr + r1 + 2.5], "eye": 1.7, "look": [ur, vr + r1, 0.6]})

    def courses(ed, label, runs, line, inward, du, dv, rng, gate_every):
        """Wall courses along `line(t)` over each run; returns (k, t0, t1, first box index, end box index) per course."""
        t0, t1 = ed.get("thickness", [0.5, 0.62])
        next_gate = None
        k, made = 0, []
        for r0, r1 in runs:
            s = r0
            next_gate = r0 + gate_every * float(rng.uniform(0.4, 0.9)) if gate_every else None
            while s < r1 - 0.05:
                n0 = len(boxes)
                gate = next_gate is not None and s >= next_gate and r1 - s > 3.0
                L = 1.15 if gate else float(rng.uniform(*ed.get("course", [2.4, 3.4])))
                e = min(s + L, r1)
                if r1 - e < 0.8:
                    e = r1
                cu, cv = line((s + e) / 2)
                reach = ed.get("reach", [0.6, 1.5, 2.5, 4.0])
                ext = max(reach)   # a run-up from higher ground along the wall counts too
                inside = [sample_uv(cu + inward[0] * o + du * f, cv + inward[1] * o + dv * f)
                          for o in reach for f in np.linspace(-(e - s) / 2 - ext, (e - s) / 2 + ext, 8)]
                foot = [sample_uv(cu + inward[0] * 0.4 + du * f, cv + inward[1] * 0.4 + dv * f) for f in np.linspace(-(e - s) / 2, (e - s) / 2, 4)]
                hi, lo = max(inside), min(foot)
                lap_ = 0.3
                far = max(sample_uv(cu + inward[0] * o + du * f, cv + inward[1] * o + dv * f)
                          for o in reach for f in np.linspace(-(e - s) / 2 - 10.0, (e - s) / 2 + 10.0, 12))
                if ed.get("clip_above_b") is not None and hi - B >= ed["clip_above_b"]:
                    box(key, f"{label} clip {k:02d}", "Boundary", "trim", cu, cv, lo - 0.5, max(max(far, hi) + ed.get("clip", 4.0), B + sec.get("clip_top_b", 30.0)),
                        e - s + 2 * lap_, 0.4, du, dv, invisible=True)
                elif gate:
                    top = hi + ed["above"] - 0.05
                    box(key, f"{label} gate {k:02d}", "Boundary", "trim", cu, cv, lo - 0.05, top, e - s, 0.1, du, dv)
                    for pf in (-0.5, 0.5):   # gate posts
                        box(key, f"{label} gate {k:02d} post {'ab'[pf > 0]}", "Boundary", ed.get("mat", "wall"),
                            cu + du * pf * (e - s + 0.3), cv + dv * pf * (e - s + 0.3), lo - 0.3, top + 0.15, 0.3, 0.42, du, dv)
                    next_gate = e + gate_every * float(rng.uniform(0.7, 1.3))
                else:
                    th = float(rng.uniform(t0, t1))
                    top = hi + ed["above"] + float(rng.uniform(0.0, ed.get("jitter", 0.15)))
                    box(key, f"{label} {k:02d}", "Boundary", ed.get("mat", "wall"), cu + inward[0] * 0.0, cv + inward[1] * 0.0,
                        lo - 0.4, top, e - s + 2 * lap_, th, du, dv)
                if ed.get("clip") and not (ed.get("clip_above_b") is not None and hi - B >= ed["clip_above_b"]):
                    # out of sight on the frame line, over the wall's whole thickness (its top can't be stood on) and up
                    # to `clip` over the highest ground 10 m either way along it (no stepping from a higher neighbour)
                    box(key, f"{label} clip {k:02d}", "Boundary", "trim", cu, cv, hi + ed["above"] - 0.25,
                        max(max(far, hi) + ed["clip"], B + sec.get("clip_top_b", 30.0)), e - s + 2 * lap_, max(t1, 0.42) + 0.14, du, dv, invisible=True)
                made.append((k, s, e, n0, len(boxes)))
                s, k = e, k + 1
        return made

    edge_courses = {}
    for ed in sec.get("edges", []):
        side = ed["side"]
        if side in "EW":
            sgn = 1.0 if side == "E" else -1.0
            line = lambda t, sgn=sgn: (sgn * (HALF_U - inset), t)
            inward = (-sgn, 0.0)
            du, dv = 0.0, 1.0
            lim = HALF_V - inset
        else:
            sgn = 1.0 if side == "N" else -1.0
            line = lambda t, sgn=sgn: (t, sgn * (HALF_V - inset))
            inward = (0.0, -sgn)
            du, dv = 1.0, 0.0
            lim = HALF_U - inset
        a, b = max(ed["range"][0], -lim), min(ed["range"][1], lim)
        runs, cur = [], a                       # the edge minus the roadblock corridors
        for g0, g1 in sorted(gaps[side]):
            if g1 <= cur or g0 >= b:
                continue
            if g0 > cur:
                runs.append((cur, g0))
            cur = max(cur, g1)
        if cur < b:
            runs.append((cur, b))
        edge_courses[side] = (ed, courses(ed, ed["name"], runs, line, inward, du, dv, rng, ed.get("gate_every")))

    # jogs (review 7, r15): the edge wall steps out round a spawn yard, so the starts get rear depth without moving
    # (and without changing any timing). The courses whose middle lies in `v` come out (the yard's span is theirs, so
    # every other course stays exactly where it was); an outer wall `depth` beyond the frame line and two return walls
    # close the yard, built course by course like the edge, from their own random stream. The yard is a declared
    # extension of the frame (greybox meta `extensions`): the Containment Test counts it as inside.
    for ji, jg in enumerate(sec.get("jogs", [])):
        side = jg["side"]
        assert side in "EW", f"{jg['name']}: jogs only on the E / W edges"
        sgn = 1.0 if side == "E" else -1.0
        ed, made = edge_courses[side]
        hit = [c for c in made if jg["v"][0] <= (c[1] + c[2]) / 2 <= jg["v"][1]]
        s_min, e_max = min(c[1] for c in hit), max(c[2] for c in hit)
        drop = {i for c in hit for i in range(c[3], c[4])}
        assert all(boxes[i]["name"].startswith(ed["name"]) for i in drop), f"{jg['name']}: one jog per edge (box indices)"
        boxes[:] = [bx for i, bx in enumerate(boxes) if i not in drop]
        u_in, u_out = sgn * (HALF_U - inset), sgn * (HALF_U - inset + jg["depth"])
        jr = np.random.default_rng(sec["seed"] * 100 + 51 + ji)
        nm = jg["name"]
        courses(ed, f"{nm} wall", [(s_min, e_max)], lambda t: (u_out, t), (-sgn, 0.0), 0.0, 1.0, jr, None)
        lo_u, hi_u = sorted((u_in, u_out))
        courses(ed, f"{nm} return S", [(lo_u, hi_u)], lambda t: (t, s_min), (0.0, 1.0), 1.0, 0.0, jr, None)
        courses(ed, f"{nm} return N", [(lo_u, hi_u)], lambda t: (t, e_max), (0.0, -1.0), 1.0, 0.0, jr, None)
        extensions.append({"name": nm, "side": side, "u": [float(min(sgn * HALF_U, u_out)), float(max(sgn * HALF_U, u_out))],
                           "v": [float(s_min), float(e_max)], "wall_u": float(u_out)})
        print(f"  {nm}: courses {[c[0] for c in hit]} out, yard v {s_min:+.2f}..{e_max:+.2f}, outer wall u {u_out:+.2f}")

    # the lanes now end at their roadblocks: each closed Task 7 lane route is walked between them instead
    for name, lid in sec.get("lane_routes", {}).items():
        pts = lane_geo[lid][0]
        _, lu, lv = stations(pts, 2.0)
        lim = HALF_U - max(rb.get("_set_in", 2.4) for rb in sec["roadblocks"]) - 0.9 - 1.5
        m = np.abs(lu) <= lim
        route(f"{name} (between the roadblocks)", np.stack([lu[m], lv[m]], axis=1))

    ob = sec.get("oob")
    if ob:
        d, gap, h0, h1 = ob["depth"], ob["gap"], B + ob["z"][0], B + ob["z"][1]
        for side, cu, cv, L, du, dv in (("east", HALF_U + gap + d / 2, 0.0, 2 * (HALF_V + gap + d), 0.0, 1.0),
                                        ("west", -(HALF_U + gap + d / 2), 0.0, 2 * (HALF_V + gap + d), 0.0, 1.0),
                                        ("north", 0.0, HALF_V + gap + d / 2, 2 * HALF_U, 1.0, 0.0),
                                        ("south", 0.0, -(HALF_V + gap + d / 2), 2 * HALF_U, 1.0, 0.0)):
            yards = [x for x in extensions if {"E": "east", "W": "west"}[x["side"]] == side]
            if not yards:
                box(key, f"OutOfBounds {side}", "Boundary", "trim", cu, cv, h0, h1, L, d, du, dv, trigger=True)
                continue
            sg = 1.0 if side == "east" else -1.0
            cuts, lo_v = [], -(HALF_V + gap + d)     # the strip outside the frame minus each yard (and a gap round it)
            for x in sorted(yards, key=lambda x: x["v"][0]):
                cuts.append((lo_v, x["v"][0] - gap))
                lo_v = x["v"][1] + gap
                u0 = abs(x["wall_u"]) + 0.5 + gap           # behind the yard's outer wall
                u1 = HALF_U + gap + d
                box(key, f"OutOfBounds {side} behind {x['name']}", "Boundary", "trim", sg * (u0 + u1) / 2, (x["v"][0] + x["v"][1]) / 2,
                    h0, h1, x["v"][1] - x["v"][0] + 2 * gap, u1 - u0, du, dv, trigger=True)
            cuts.append((lo_v, HALF_V + gap + d))
            for i, (a_, b_) in enumerate(cuts):
                box(key, f"OutOfBounds {side}" + ("" if i == 0 else f" {i + 1}"), "Boundary", "trim", cu, (a_ + b_) / 2, h0, h1, b_ - a_, d, du, dv, trigger=True)


# ---------------------------------------------------------------- run

cams = []
for key, sec in GB["sections"].items():
    print(f"section {key} (Task {sec['task']}){' - locked' if sec.get('locked') else ''}")
    set_kept(sec["task"])
    {"ravine": build_ravine, "boundary": build_boundary}.get(sec.get("kind"), build_section)(key, sec)
    rng_w = np.random.default_rng(sec["seed"] + 1000)   # own stream: never shifts the section's other draws
    for w in sec.get("retaining", []):
        retaining_wall(key, w, rng_w)
    cams += cameras(sec)
    for r in sec.get("routes", []):   # walk-test routes through the section's geometry (frame coords)
        route(r["name"], r["points"])
set_kept(max(sec["task"] for sec in GB["sections"].values()))

OUT = PG / "greybox_meta.json"
OUT.write_text(json.dumps({"sections": {k: {"task": v["task"], "locked": bool(v.get("locked"))} for k, v in GB["sections"].items()},
                           "replaces": all_replaces + auto_replaced, "boxes": boxes, "ribbons": ribbons, "stone_chunks": stone_chunks, "routes": routes, "cameras": cams,
                           # Task 7 walk routes a section closes off (their own routes replace them)
                           "closed_routes": [n for sec in GB["sections"].values() for n in sec.get("closes", [])],
                           "closures": closures, "extensions": extensions,
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
d.text((40, 10), f"Task {TASK} greybox - frame view (u east along the main street, v uphill). Grey = placeholders kept; "
       "dark = blocks, tan = low roofs, orange = walkable, green = cover, black = trim/rails, red = walk routes",
       fill=(0, 0, 0), font=f)
SHOTS.mkdir(parents=True, exist_ok=True)
img.save(SHOTS / "greybox_plan.png")
print(f"sheet {SHOTS / 'greybox_plan.png'}")
