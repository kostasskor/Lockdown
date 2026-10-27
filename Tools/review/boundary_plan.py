"""Boundary proposal (Task 9 r16, phase A): where should the map's edge run instead of the 150 x 104 rectangle?

Read-only on the project: reads play_meta / play_513 / play_mask, greybox_meta, the layout JSON and the physics
reachability export (build world), writes SourceData/playground/boundary_proposal.json and
Screenshots/task09/boundary_plan.png.

Per frame side, at every 0.5 m station, it looks inward along the normal for the first thing that must stay inside:
  path K  - lanes / connectors / pads (play_mask), walk routes, areas, capture circles, spawn zones + 10 m rear depth,
            the East spawn yard, the roadblock corridors;
  box K   - every greybox block that is not part of the boundary (houses, yards, walls, stairs, roofs), Task 7
            placeholders.
Then it picks, per stretch:
  a) a terrace / garden wall 1.5 m (1.0-2.0) off the walkable edge, turning with it;
  b) a natural feature between that edge and the frame: a house / yard back (box K), or a bank / terrace step
     (a rise of >= 0.9 m within 2 m of ground) - the line runs along its foot or top;
  c) the frame line where the dead strip is < 2.5 m deep (those courses stay as they are);
  c~) long frame-line runs bent gently inward (0.3-1.0 m, only over dead ground) so no run is ruler-straight.
The line never goes outward (only the existing East spawn yard stays out).

usage: uv run --offline --no-project --with numpy --with scipy --with pillow --with scikit-image python Tools/review/boundary_plan.py
"""
import json
import math
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont
from scipy.ndimage import distance_transform_edt, map_coordinates, median_filter, binary_dilation

ROOT = Path(__file__).resolve().parents[2]
PG = ROOT / "SourceData" / "playground"
META = json.loads((PG / "play_meta.json").read_text())
GM = json.loads((PG / "greybox_meta.json").read_text())
LAYOUT = json.loads((ROOT / "Assets/Lockdown/Data/Layout/lockdown_layout.json").read_text())
OUT_JSON = PG / "boundary_proposal.json"
OUT_PNG = ROOT / "Screenshots" / "task09" / "boundary_plan.png"

CX, CZ = META["frame_center"]
R = math.radians(META["frame_rotation_deg"])
COS, SIN = math.cos(R), math.sin(R)
HU, HV = META["frame_size"][0] / 2, META["frame_size"][1] / 2
RES = META["resolution"]
STEP = META["size"] / (RES - 1)
X0, Z0 = META["x0"], META["z0"]
B = META["flag_b_height"]
raw = np.fromfile(PG / META["file"], "<u2").reshape(RES, RES).astype(float)
HGT = META["base_height"] + raw / 65535 * (META["top_height"] - META["base_height"])
MASK = np.asarray(Image.open(PG / META["mask"]).convert("L"), float)[::-1, :] / 255.0

G = 0.25                                         # raster (frame coordinates)
U0, U1, V0, V1 = -HU - 1.0, HU + 8.0, -HV - 1.0, HV + 1.0
NU, NV = int(round((U1 - U0) / G)) + 1, int(round((V1 - V0) / G)) + 1
UU, VV = np.meshgrid(U0 + np.arange(NU) * G, V0 + np.arange(NV) * G)      # [iv, iu]

SEED = 1607
rng = np.random.default_rng(SEED)


def to_world(u, v):
    return CX + u * COS - v * SIN, CZ + u * SIN + v * COS


def to_frame(x, z):
    dx, dz = x - CX, z - CZ
    return dx * COS + dz * SIN, -dx * SIN + dz * COS


def ground(u, v):
    x, z = to_world(np.asarray(u, float), np.asarray(v, float))
    return map_coordinates(HGT, [(z - Z0) / STEP, (x - X0) / STEP], order=1, mode="nearest")


def mask_at(u, v):
    x, z = to_world(np.asarray(u, float), np.asarray(v, float))
    k = (MASK.shape[0] - 1) / (RES - 1)
    return map_coordinates(MASK, [(z - Z0) / STEP * k, (x - X0) / STEP * k], order=1, mode="nearest") > 0.5


def cell(u, v):
    return int(round((v - V0) / G)), int(round((u - U0) / G))


def stamp_box(m, cu, cv, du, dv, L, W, pad):
    n = math.hypot(du, dv) or 1.0
    du, dv = du / n, dv / n
    a = (UU - cu) * du + (VV - cv) * dv
    b = -(UU - cu) * dv + (VV - cv) * du
    m |= (np.abs(a) <= L / 2 + pad) & (np.abs(b) <= W / 2 + pad)


def stamp_line(m, pts, r):
    pts = np.asarray(pts, float)
    for (ua, va), (ub, vb) in zip(pts[:-1], pts[1:]):
        du, dv = ub - ua, vb - va
        L2 = du * du + dv * dv or 1e-9
        t = np.clip(((UU - ua) * du + (VV - va) * dv) / L2, 0, 1)
        m |= np.hypot(UU - (ua + t * du), VV - (va + t * dv)) <= r


# ------------------------------------------------------------------ what must stay inside
frame_in = (np.abs(UU) <= HU) & (np.abs(VV) <= HV)
yard = np.zeros_like(frame_in)
for ex in GM.get("extensions", []):
    yard |= (UU >= ex["u"][0]) & (UU <= ex["u"][1]) & (VV >= ex["v"][0]) & (VV <= ex["v"][1])
inside_now = frame_in | yard

path = mask_at(UU, VV) & inside_now
for r_ in GM["routes"]:
    u_, v_ = to_frame(np.asarray(r_["xs"]), np.asarray(r_["zs"]))
    stamp_line(path, np.stack([u_, v_], 1), 1.0)
for l in LAYOUT["lanes"]:
    stamp_line(path, l["points"], l["width"] / 2)
for c in LAYOUT.get("connectors", []):
    stamp_line(path, c["points"], c.get("width", 3.0) / 2)
for a in LAYOUT["areas"]:
    if a["shape"] == "circle":
        path |= np.hypot(UU - a["center"][0], VV - a["center"][1]) <= a["radius"]
    else:
        path |= (np.abs(UU - a["center"][0]) <= a["size"][0] / 2) & (np.abs(VV - a["center"][1]) <= a["size"][1] / 2)
for f in LAYOUT["flags"]:
    path |= np.hypot(UU - f["at"][0], VV - f["at"][1]) <= 15.0          # capture circle + its 15 m ring zone
for side, pts in LAYOUT["initial_spawns"].items():
    pts = np.asarray(pts, float)
    sg = -1 if side == "west" else 1
    for u_, v_ in pts:
        path |= np.hypot(UU - u_, VV - v_) <= 3.0
    far = pts[:, 0].min() if sg < 0 else pts[:, 0].max()
    path |= ((UU - far) * sg >= 0) & ((UU - far) * sg <= 10.5) & (VV >= pts[:, 1].min() - 3) & (VV <= pts[:, 1].max() + 3)
path |= yard

boxk = np.zeros_like(path)
rb_corr = np.zeros_like(path)
for b in GM["boxes"]:
    if b.get("trigger"):
        continue
    if b["section"] == "boundary":
        if " roadblock " in b["name"] and not b.get("invisible"):
            # the corridor from the roadblock row out to the frame edge stays as it is
            sg = 1 if b["u"] > 0 else -1
            rb_corr |= ((UU - b["u"]) * sg >= -1.0) & (np.abs(VV - b["v"]) <= b["sz"] / 2 + 4.0)
        continue
    if "u" not in b:
        b["u"], b["v"] = to_frame(b["x"], b["z"])
        yaw = math.radians(b["yaw"])
        b["du"], b["dv"] = to_frame(CX + math.sin(yaw), CZ + math.cos(yaw))
    stamp_box(boxk, b["u"], b["v"], b["du"], b["dv"], b["sz"], b["sx"], 0.3)
for b in META.get("boxes", []):                                    # Task 7 placeholders (stand in for future buildings)
    u_, v_ = to_frame(b["x"], b["z"])
    yaw = math.radians(b.get("yaw", 0.0))
    du_, dv_ = to_frame(CX + math.sin(yaw), CZ + math.cos(yaw))
    stamp_box(boxk, u_, v_, du_, dv_, b["sz"], b["sx"], 0.3)
path |= rb_corr

# physics-reachable ground (build world)
hd = json.loads((PG / "reach_physics_build.json").read_text())
buf = (PG / "reach_physics_build.bin").read_bytes()
pos = np.frombuffer(buf, "<f4", 3 * hd["nodes"], 0).reshape(-1, 3)
pu, pv = to_frame(pos[:, 0].astype(float), pos[:, 2].astype(float))
reach = np.zeros_like(path)
ok = (pu >= U0) & (pu <= U1) & (pv >= V0) & (pv <= V1)
ri, ci = ((pv[ok] - V0) / G).round().astype(int), ((pu[ok] - U0) / G).round().astype(int)
reach[ri.clip(0, NV - 1), ci.clip(0, NU - 1)] = True
reach = binary_dilation(reach, iterations=1) & inside_now          # nodes sit on a 0.5 m grid

# ------------------------------------------------------------------ the four sides
SIDES = {   # frame point at station t, inward normal, station range
    "W": (lambda t, d: (-HU + d, t), (1, 0), (-HV, HV)),
    "E": (lambda t, d: (HU - d, t), (-1, 0), (-HV, HV)),
    "S": (lambda t, d: (t, -HV + d), (0, 1), (-HU, HU)),
    "N": (lambda t, d: (t, HV - d), (0, -1), (-HU, HU)),
}
DS = 0.5           # station spacing
DMAX = 14.0        # how far in we look


def first_hit(m, side, t):
    f = SIDES[side][0]
    for s in np.arange(0.0, DMAX, G):
        u, v = f(t, s)
        r, c = cell(u, v)
        if 0 <= r < NV and 0 <= c < NU and m[r, c]:
            return s
    return DMAX


def bank_at(side, t, lo, hi):
    """A bank / terrace step between depth lo and hi on the inward ray: (depth of its top, rise) or None."""
    f = SIDES[side][0]
    s = np.arange(0.0, max(hi, 0.0) + 0.01, 0.25)
    if len(s) < 9:
        return None
    pts = np.array([f(t, x) for x in s])
    h = ground(pts[:, 0], pts[:, 1])
    best = None
    for i in range(len(s) - 8):          # rise over 2 m
        rise = abs(h[i + 8] - h[i])
        if rise >= 0.9 and s[i] >= lo and (best is None or rise > best[1]):
            best = (float(s[i + 8] if h[i + 8] > h[i] else s[i]), float(rise))
    return best


prop = {}
for side, (f, nrm, (t0, t1)) in SIDES.items():
    ts = np.arange(t0 + 1.0, t1 - 1.0 + 1e-6, DS)
    kp_raw = np.array([first_hit(path, side, t) for t in ts])
    kb_raw = np.array([first_hit(boxk, side, t) for t in ts])
    dmax = np.minimum(kp_raw - 1.0, kb_raw - 0.2)           # the deepest the wall may go (clear of everything kept)
    kp = median_filter(kp_raw, size=9, mode="nearest")      # the edge's course, without single-cell noise
    kb = median_filter(kb_raw, size=9, mode="nearest")
    target = np.zeros_like(ts)
    kind = np.array(["c"] * len(ts), dtype=object)
    NEAR = 9.0         # a path / house this close to the frame line sets the edge; farther in, the frame line stays
    for i, t in enumerate(ts):
        a_ = kp[i] - 1.5 if kp[i] <= NEAR else 0.0
        b_ = kb[i] - 0.3 if kb[i] <= NEAR and kb[i] < kp[i] else 0.0
        cand = [(x, k) for x, k in ((a_, "a"), (b_, "b-house")) if x >= 1.0]
        if not cand:
            continue
        d, k = min(cand)
        bk = bank_at(side, t, 1.0, d)
        if bk is not None and bk[0] <= dmax[i]:
            d, k = bk[0], "b-bank"
        target[i], kind[i] = min(d, max(dmax[i], 0)), k
    # smooth, drop short stretches, fill short gaps
    target = median_filter(target, size=9, mode="nearest")
    target = np.minimum(target, np.maximum(dmax, 0))
    pos_ = target > 0
    runs, i = [], 0
    while i < len(ts):
        if pos_[i]:
            j = i
            while j < len(ts) and pos_[j]:
                j += 1
            runs.append([i, j])
            i = j
        else:
            i += 1
    merged = []
    for r_ in runs:
        if merged and (r_[0] - merged[-1][1]) * DS < 8.0:
            gap = slice(merged[-1][1], r_[0])
            target[gap] = np.minimum(np.interp(ts[gap], [ts[merged[-1][1] - 1], ts[r_[0]]], [target[merged[-1][1] - 1], target[r_[0]]]), np.maximum(dmax[gap], 0))
            merged[-1][1] = r_[1]
        else:
            merged.append(r_)
    for r_ in merged:
        if (r_[1] - r_[0]) * DS < 8.0:
            target[r_[0]:r_[1]] = 0
    # transitions at field-wall angles: depth changes at most 0.7 m per m (about 35 deg), never deeper than allowed
    k = 0.7
    for _ in range(2):
        for i in range(1, len(ts)):
            target[i] = min(target[i], target[i - 1] + k * DS)
        for i in range(len(ts) - 2, -1, -1):
            target[i] = min(target[i], target[i + 1] + k * DS)
    # long straight frame runs: bend gently inward over dead ground (c~)
    soft = np.zeros_like(ts)
    i = 0
    while i < len(ts):
        if target[i] == 0:
            j = i
            while j < len(ts) and target[j] == 0:
                j += 1
            if (j - i) * DS > 15.0:
                tt = ts[i]
                knots_t, knots_d = [ts[i]], [0.0]
                while tt < ts[j - 1] - 6.0:
                    tt += float(rng.uniform(5.0, 11.0))
                    tq = min(tt, ts[j - 1])
                    knots_t.append(tq)
                    room = float(np.min(dmax[max(i, int((tq - ts[0]) / DS) - 6):int((tq - ts[0]) / DS) + 7]))
                    knots_d.append(float(rng.uniform(0.3, 0.9)) if room >= 1.3 else 0.0)
                knots_t.append(ts[j - 1]); knots_d.append(0.0)
                seg = slice(i, j)
                soft[seg] = np.minimum(np.interp(ts[seg], knots_t, knots_d), np.maximum(dmax[seg] - 0.4, 0))
            i = j
        else:
            i += 1
    prop[side] = dict(ts=ts, d=target, soft=soft, kind=kind, kp=kp, kb=kb, dmax=dmax)

# ------------------------------------------------------------------ stretches, natural polylines, cut-off area
stretches = []
for side, P in prop.items():
    f = SIDES[side][0]
    ts, d = P["ts"], P["d"]
    for which, prof in (("main", d), ("soft", np.where(d > 0, 0, P["soft"]))):
        on = prof > 0.05
        i = 0
        while i < len(ts):
            if not on[i]:
                i += 1
                continue
            j = i
            while j < len(ts) and on[j]:
                j += 1
            if (j - i) * DS < (10.0 if which == "soft" else 8.0):
                i = j
                continue                                   # no fragments
            s0, s1 = ts[max(i - 1, 0)], ts[min(j, len(ts) - 1)]
            # polyline: knots every 3-9 m (seeded), depth jitter +-0.25 m inside the clearance band, ends on the frame line
            kt = [s0]
            while kt[-1] < s1 - 3.0:
                kt.append(min(kt[-1] + float(rng.uniform(3.0, 9.0)), s1))
            if kt[-1] < s1:
                kt.append(s1)
            pts = []
            for q, tq in enumerate(kt):
                dq = float(np.interp(tq, ts, prof))
                if 0 < q < len(kt) - 1:
                    lim = float(np.interp(tq, ts, np.maximum(P["dmax"], 0)))
                    dq = float(np.clip(dq + rng.uniform(-0.25, 0.25), 0.05, max(lim, 0.05)))
                else:
                    dq = 0.0
                pts.append(f(tq, dq))
            sl = slice(i, j)
            kinds = [k for k in P["kind"][sl] if k != "c"]
            typ = "c~" if which == "soft" else (max(set(kinds), key=kinds.count) if kinds else "a")
            # ground cut off: reachable cells between the frame line and the new line
            dep = np.interp(np.clip((VV if side in "EW" else UU), s0, s1), kt, [0.0] + [math.hypot(*np.subtract(pts[q], f(kt[q], 0))) for q in range(1, len(kt) - 1)] + [0.0])
            depth_now = {"W": UU + HU, "E": HU - UU, "S": VV + HV, "N": HV - VV}[side]
            tcoord = VV if side in "EW" else UU
            band = (tcoord >= s0) & (tcoord <= s1) & (depth_now >= 0) & (depth_now < dep) & frame_in
            cut_reach = float((band & reach).sum() * G * G)
            cut_all = float(band.sum() * G * G)
            clear = float(np.min(np.interp(ts[sl], ts, P["kp"]) - prof[sl])) if j > i else 0.0
            L = float(sum(math.dist(a, b) for a, b in zip(pts[:-1], pts[1:])))
            near_rb = bool(np.any(rb_corr[band]) or any(abs(t - c) < 6 for t in (s0, s1) for c in []))
            stretches.append(dict(side=side, type=typ, t=[float(s0), float(s1)], points=[[round(p[0], 2), round(p[1], 2)] for p in pts],
                                  length=round(L, 1), max_depth=round(float(prof[sl].max()), 2), cut_walkable_m2=round(cut_reach, 1),
                                  cut_ground_m2=round(cut_all, 1), min_clear_path=round(clear, 2)))
            i = j

order = {"N": 0, "E": 1, "S": 2, "W": 3}
stretches.sort(key=lambda s: (order[s["side"]], s["t"][0] * (1 if s["side"] in "EN" else -1)))
for n, s in enumerate(stretches, 1):
    s["id"] = n
    s["follows"] = {"a": "garden / terrace wall 1-2 m off the walkable edge", "b-house": "back of a house / yard",
                    "b-bank": "a bank / terrace step", "c~": "frame line, bent gently inward"}[s["type"]]

OUT_JSON.write_text(json.dumps({"seed": SEED, "stretches": stretches}, indent=1))

# ------------------------------------------------------------------ sheet
PX = 7
W_, H_ = int((U1 - U0 + 12) * PX), int((V1 - V0 + 22) * PX)


def px(u, v):
    return ((u - U0 + 6) * PX, (V1 - v + 16) * PX)


img = Image.new("RGB", (W_, H_), (246, 244, 238))
d = ImageDraw.Draw(img, "RGBA")
try:
    font = ImageFont.truetype("arial.ttf", 13)
    big = ImageFont.truetype("arialbd.ttf", 15)
except OSError:
    font = big = ImageFont.load_default()
sub = (slice(None, None, 2), slice(None, None, 2))
for mm, col in ((reach, (190, 186, 176, 255)), (path, (122, 170, 110, 120)), (boxk, (90, 90, 96, 170))):
    rr, cc = np.nonzero(mm[sub])
    for r_, c_ in zip(rr, cc):
        x, y = px(U0 + c_ * 2 * G, V0 + r_ * 2 * G)
        d.rectangle([x, y - 2 * G * PX, x + 2 * G * PX, y], fill=col)
for l in LAYOUT["lanes"]:
    d.line([px(*p) for p in l["points"]], fill=(150, 110, 60, 255), width=2)
for r_ in GM["routes"]:
    u_, v_ = to_frame(np.asarray(r_["xs"]), np.asarray(r_["zs"]))
    d.line([px(a, b) for a, b in zip(u_, v_)], fill=(200, 40, 40, 140), width=1)
# old rectangle (dashed) and the East spawn yard
corn = [(-HU, -HV), (HU, -HV), (HU, HV), (-HU, HV), (-HU, -HV)]
for (ua, va), (ub, vb) in zip(corn[:-1], corn[1:]):
    n_ = int(math.dist((ua, va), (ub, vb)) / 2)
    for k in range(0, n_, 2):
        a0, a1 = k / n_, min((k + 1) / n_, 1)
        d.line([px(ua + (ub - ua) * a0, va + (vb - va) * a0), px(ua + (ub - ua) * a1, va + (vb - va) * a1)], fill=(80, 80, 80, 255), width=2)
for ex in GM.get("extensions", []):
    d.rectangle([px(ex["u"][0], ex["v"][1]), px(ex["u"][1], ex["v"][0])], outline=(80, 80, 80, 255), width=2)
# proposed stretches
colt = {"a": (215, 40, 40, 255), "b-house": (40, 90, 200, 255), "b-bank": (130, 60, 170, 255), "c~": (230, 140, 20, 255)}
for s in stretches:
    p = [px(*q) for q in s["points"]]
    poly = p + [px(*SIDES[s["side"]][0](s["t"][1], 0)), px(*SIDES[s["side"]][0](s["t"][0], 0))]
    d.polygon(poly, fill=colt[s["type"]][:3] + (60,))
    d.line(p, fill=colt[s["type"]], width=4)
    mu, mv = s["points"][len(s["points"]) // 2]
    nu, nv = SIDES[s["side"]][1]
    lx, ly = px(mu + nu * 3.5, mv + nv * 3.5)
    lab = f"{s['id']} {s['type']}  {s['cut_walkable_m2']:.0f} m2"
    tw = d.textlength(lab, font=big)
    d.rectangle([lx - tw / 2 - 3, ly - 10, lx + tw / 2 + 3, ly + 9], fill=(255, 255, 255, 220))
    d.text((lx - tw / 2, ly - 9), lab, fill=colt[s["type"]][:3], font=big)
for st in META["stakes"]:
    if st["kind"] in ("flag", "spawn_west", "spawn_east"):
        u_, v_ = to_frame(st["x"], st["z"])
        x, y = px(u_, v_)
        d.ellipse([x - 6, y - 6, x + 6, y + 6], outline=(0, 0, 0, 255), width=2)
        d.text((x + 8, y - 8), st["name"], fill=(0, 0, 0), font=font)
# the review screenshot spot: Lower Lane east end, looking 178 deg (world heading)
lu_, lv_ = 70.0, -46.0
x, y = px(lu_, lv_)
hd_ = math.radians(178.0)
wx, wz = math.sin(hd_), math.cos(hd_)
fu, fv = wx * COS + wz * SIN, -wx * SIN + wz * COS
x2, y2 = px(lu_ + fu * 6, lv_ + fv * 6)
d.ellipse([x - 5, y - 5, x + 5, y + 5], fill=(0, 0, 0, 255))
d.line([(x, y), (x2, y2)], fill=(0, 0, 0, 255), width=3)
d.text((x - 120, y - 22), "your screenshot (178 deg)", fill=(0, 0, 0), font=font)
d.text((12, 10), "Boundary proposal r16: grey dashed = today's rectangle (+ East spawn yard); coloured = proposed line;"
       " numbers = stretch id, type, walkable ground cut off", fill=(0, 0, 0), font=big)
d.text((12, 32), "a = garden/terrace wall 1-2 m off the walkable edge   b-house = back of a house/yard   b-bank = bank/terrace step"
       "   c~ = frame line bent gently inward   (everything else stays on the frame line = c)", fill=(0, 0, 0), font=font)
d.text((12, 50), "green = must stay inside (paths, routes, areas, flag rings, spawns), dark = greybox blocks, light grey = reachable ground",
       fill=(0, 0, 0), font=font)
OUT_PNG.parent.mkdir(parents=True, exist_ok=True)
img.save(OUT_PNG)

print(f"{'id':>3} {'side':4} {'type':8} {'from':>7} {'to':>7} {'len m':>6} {'depth':>6} {'cut walk m2':>11} {'cut all m2':>10} {'clear':>6}  follows")
for s in stretches:
    print(f"{s['id']:>3} {s['side']:4} {s['type']:8} {s['t'][0]:7.1f} {s['t'][1]:7.1f} {s['length']:6.1f} {s['max_depth']:6.2f} "
          f"{s['cut_walkable_m2']:11.1f} {s['cut_ground_m2']:10.1f} {s['min_clear_path']:6.2f}  {s['follows']}")
print(f"total walkable cut {sum(s['cut_walkable_m2'] for s in stretches):.0f} m2, ground cut {sum(s['cut_ground_m2'] for s in stretches):.0f} m2")
print("wrote", OUT_JSON, "and", OUT_PNG)
