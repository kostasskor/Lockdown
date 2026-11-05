"""Layout review measurements: flag and spawn timings, sightlines, peeks, height, containment, sun, routes, perches.

Run from the project root:
    uv run --with numpy --with scipy --with pillow Tools/review/review2_layout.py [section ...] [--checks PATH]
Sections: pace sight peeks height contain sun terrain routes perch landmarks   (default: all)

Method = the playground_checks.py method (same raster, blockers, eye heights, 6.5 m/s, 0.5 m grid; heights relative to
the Flag B deck, graded ground, true bearings), reused by running that file silently (review_lib.load). Nothing in the
project is written. --checks points at another copy of playground_checks.py (e.g. a pre-Task-9 tree) for a before column.
"""
import heapq
import math
import sys
from collections import Counter, defaultdict

import numpy as np
from scipy.ndimage import binary_dilation, map_coordinates

sys.path.insert(0, str(__import__("pathlib").Path(__file__).resolve().parent))
from review_lib import load  # noqa: E402

args = sys.argv[1:]
checks = None
if "--checks" in args:
    i = args.index("--checks")
    checks = args[i + 1]
    del args[i:i + 2]
SECTIONS = args or ["pace", "sight", "peeks", "height", "contain", "sun", "terrain", "routes", "approaches", "pockets", "extras", "deadends", "widths", "perch", "landmarks", "modes", "names"]

g = load(checks)
META, LAYOUT, B = g["META"], g["LAYOUT"], g["B"]
ground, walk_h, occ, XX, ZZ, UU, VV = (g[k] for k in ("ground", "walk_h", "occ", "XX", "ZZ", "UU", "VV"))
STEP, RES, SPRINT = g["STEP"], g["RES"], g["SPRINT"]
inside, walkable, surface, blocked, deck = (g[k] for k in ("inside", "walkable", "surface", "blocked", "deck"))
treads = g.get("treads", np.zeros_like(deck))  # older copies of the checks have no tread raster
feature, feature_names = g["feature"], g["feature_names"]
los, sample, to_world, to_frame, grid_index = g["los"], g["sample"], g["to_world"], g["to_frame"], g["grid_index"]
stand_height, lane_points, chaikin = g["stand_height"], g["lane_points"], g["chaikin"]
stakes = g["stakes"]
cap_r = g["cap_r"]
reach, reach1 = g["reach"], g["reach1"]
HALF_U, HALF_V = g["HALF_U"], g["HALF_V"]


def out(*a):
    print(*a, flush=True)


def bearing(p, q):
    return math.degrees(math.atan2(q[0] - p[0], q[1] - p[1])) % 360


def merge_angles(angs, gap=15.0):
    return g["merge_angles"](angs, gap)


starts_w = [(s["x"], s["z"]) for s in META["stakes"] if s["kind"] == "start_west"]
starts_e = [(s["x"], s["z"]) for s in META["stakes"] if s["kind"] == "start_east"]
EYE = 1.7

_dcache = {}


def dist_from(pt):
    k = (round(pt[0], 3), round(pt[1], 3))
    if k not in _dcache:
        _dcache[k] = g["dijkstra"](pt)
    return _dcache[k]


def multi(pts):
    return np.minimum.reduce([dist_from(p) for p in pts])


def t_at(dist, p):
    return g["at"](dist, p[0], p[1]) / SPRINT


dW = dE = None


def need_dist():
    global dW, dE
    if dW is None:
        dW, dE = multi(starts_w), multi(starts_e)


def fp(name):
    return (stakes[name]["x"], stakes[name]["z"])


def uvs(p):
    u, v = to_frame(*p)
    return f"u{u:+.0f} v{v:+.0f}"


def feat_at(r, c):
    k = feature[r, c]
    return feature_names[k] if k >= 0 else "(unnamed ground/roof)"


# ================================================================== PACE
def pace():
    need_dist()
    out("\n=== PACE (MEASURED; 1 m ... 0.5 m grid Dijkstra, 6.5 m/s, nearest of each side's 6 start points) ===")
    tW_B, tE_B = t_at(dW, fp("Flag_B")), t_at(dE, fp("Flag_B"))
    out(f"spawn(starts)->B   West {tW_B:.1f}  East {tE_B:.1f}  diff {abs(tW_B - tE_B):.1f} s = {abs(tW_B - tE_B) / max(tW_B, tE_B) * 100:.0f}%")
    # stake-to-stake as in the checks
    sW, sE = fp("Spawn_West"), fp("Spawn_East")
    dsW, dsE = dist_from(sW), dist_from(sE)
    out(f"spawn(stake)->B    West {t_at(dsW, fp('Flag_B')):.1f}  East {t_at(dsE, fp('Flag_B')):.1f}")
    out(f"spawn->spawn: stake {t_at(dsW, sE):.1f} s; nearest start pair {min(t_at(dW, q) for q in starts_e):.1f} s; "
        f"farthest start pair {max(t_at(dW, q) for q in starts_e):.1f} s")
    for f in ("Flag_A", "Flag_B", "Flag_C"):
        out(f"{f}: West {t_at(dW, fp(f)):.1f} s  East {t_at(dE, fp(f)):.1f} s   (stakes: W {t_at(dsW, fp(f)):.1f} E {t_at(dsE, fp(f)):.1f})")
    dA, dB_, dC = (dist_from(fp(f)) for f in ("Flag_A", "Flag_B", "Flag_C"))
    out(f"flag->flag: A-B {t_at(dA, fp('Flag_B')):.1f}  B-C {t_at(dB_, fp('Flag_C')):.1f}  A-C {t_at(dA, fp('Flag_C')):.1f} s")
    # meeting points on each lane (equal arrival)
    for lane in LAYOUT["lanes"]:
        u, v = lane_points(lane, 2.0)
        wx, wz = to_world(u, v)
        tw = np.array([t_at(dW, (x, z)) for x, z in zip(wx, wz)])
        te = np.array([t_at(dE, (x, z)) for x, z in zip(wx, wz)])
        k = int(np.argmin(np.abs(tw - te)))
        out(f"meeting point {lane['id']}: u {u[k]:+.0f} at {max(tw[k], te[k]):.1f} s (W {tw[k]:.1f} / E {te[k]:.1f})")

    # first contact by line of sight: earliest T with a W-reachable cell and an E-reachable cell that see each other
    step = 6  # 3 m grid
    sel = np.zeros(ground.shape, bool)
    sel[::step, ::step] = True
    cand = sel & walkable & inside & np.isfinite(dW) & np.isfinite(dE)
    rr, cc = np.nonzero(cand)
    px, pz = XX[rr, cc], ZZ[rr, cc]
    tw, te = dW[rr, cc] / SPRINT, dE[rr, cc] / SPRINT
    hh = np.array([stand_height(x, z) for x, z in zip(px, pz)]) + EYE

    def contact_exists(T, dmax, want_pair=False):
        iw, ie = np.nonzero(tw <= T)[0], np.nonzero(te <= T)[0]
        for i in iw:
            q = ie
            d = np.hypot(px[q] - px[i], pz[q] - pz[i])
            m = (d > 2.0) & (d <= dmax)
            if not m.any():
                continue
            q, d = q[m], d[m]
            K = int(d.max() / 0.5)
            t = np.linspace(0, 1, K)[1:-1]
            xs_ = px[i] + (px[q][:, None] - px[i]) * t
            zs_ = pz[i] + (pz[q][:, None] - pz[i]) * t
            line = hh[i] + (hh[q][:, None] - hh[i]) * t
            o = map_coordinates(occ, [((zs_ - g["Z0"]) / STEP).ravel(), ((xs_ - g["X0"]) / STEP).ravel()], order=1, mode="nearest").reshape(xs_.shape)
            clear = np.all(line > o + 0.05, axis=1)
            if clear.any():
                j = q[np.nonzero(clear)[0][0]]
                if want_pair:
                    return (i, j, float(np.hypot(px[i] - px[j], pz[i] - pz[j])))
                return True
        return None if want_pair else False

    for dmax in (140.0, 70.0, 50.0, 35.0):
        lo, hi = 2.0, 30.0
        while hi - lo > 0.25:
            mid = (lo + hi) / 2
            if contact_exists(mid, dmax):
                hi = mid
            else:
                lo = mid
        pr = contact_exists(hi, dmax, True)
        if pr:
            i, j, d = pr
            out(f"FIRST CONTACT by line of sight (pair <= {dmax:.0f} m): {hi:.1f} s (W cell {uvs((px[i], pz[i]))} at {tw[i]:.1f} s; E cell {uvs((px[j], pz[j]))} at {te[j]:.1f} s; {d:.0f} m apart; 3 m grid, 0.25 s)")
    return


# ================================================================== SIGHTLINES
def sight():
    need_dist()
    out("\n=== SIGHTLINES (MEASURED; eye 1.65 m, lane-centre points 2 m apart, inside the frame) ===")
    res = {}
    for lane in LAYOUT["lanes"]:
        u, v = lane_points(lane, 2.0)
        wx, wz = to_world(u, v)
        best = (0.0, 0, 0)
        for i in range(len(u)):
            for j in range(len(u) - 1, i, -1):
                d = math.hypot(wx[j] - wx[i], wz[j] - wz[i])
                if d <= best[0]:
                    break
                if los((wx[i], wz[i]), (wx[j], wz[j]), 1.65, 1.65):
                    best = (d, i, j)
                    break
        d, i, j = best
        res[lane["id"]] = best
        out(f"{lane['id']:6s} {d:5.1f} m  u {u[i]:+.0f}->{u[j]:+.0f}   ends: {uvs((wx[i], wz[i]))} -> {uvs((wx[j], wz[j]))}")
        # count of 40+ m lines (share of start points that see >=40 m along the lane)
        n40 = 0
        for i2 in range(len(u)):
            for j2 in range(len(u) - 1, i2, -1):
                dd = math.hypot(wx[j2] - wx[i2], wz[j2] - wz[i2])
                if dd < 40:
                    break
                if los((wx[i2], wz[i2]), (wx[j2], wz[j2]), 1.65, 1.65):
                    n40 += 1
                    break
        out(f"       lane points that see >=40 m along the lane: {n40}/{len(u)}")
    # zone -> spawn lines, bridge -> spawn
    starts = starts_w + starts_e
    for f in ("Flag_A", "Flag_B", "Flag_C"):
        p = fp(f)
        pts = [p] + [(p[0] + r * math.cos(k * math.pi / 6), p[1] + r * math.sin(k * math.pi / 6)) for r in (cap_r, 2 * cap_r) for k in range(12)]
        hit = [(q, s) for q in pts for s in starts if los(q, s, EYE, EYE)]
        out(f"{f}: lines to any of 12 start points from centre + 5 m + 10 m rings (25 points x 12): {len(hit)}")
    # spawn exposure: enemy-first seers
    for side, sts, dOwn, dEn, yard in (("West", starts_w, dW, dE, (-66, -8)), ("East", starts_e, dE, dW, (67, -16))):
        yx, yz = to_world(*yard)
        cells = np.argwhere(reach1 & (np.hypot(XX - yx, ZZ - yz) > 12.0) & (np.hypot(XX - yx, ZZ - yz) <= 120.0))
        seen, near, enemy_first, farthest = 0, 1e9, 0, 0.0
        for r, c in cells:
            q = (XX[r, c], ZZ[r, c])
            if any(los(q, s, EYE, EYE, 0.5) for s in sts):
                seen += 1
                dd = min(math.hypot(q[0] - s[0], q[1] - s[1]) for s in sts)
                farthest = max(farthest, dd)
                if dEn[r, c] < dOwn[r, c]:
                    enemy_first += 1
                    near = min(near, dd)
        out(f"{side} starts are seen from {seen} cells (1 m grid, >12 m from the yard centre); {enemy_first} of them the enemy reaches first; nearest such cell {near:.0f} m; farthest cell that sees a start {farthest:.0f} m")
    # rear depth: walkable reach cells behind the starts
    for side, sts in (("West", starts_w), ("East", starts_e)):
        us = [to_frame(*s)[0] for s in sts]
        vs = [to_frame(*s)[1] for s in sts]
        edge = -HALF_U if side == "West" else HALF_U
        what = "the frame edge"
        # a boundary jog (greybox `extensions`) round the starts moves the rear wall out: measure to its outer wall
        for ex in g.get("_gb", {}).get("extensions", []):
            if ex["side"] == side[0] and ex["v"][0] <= min(vs) and max(vs) <= ex["v"][1]:
                edge, what = ex["wall_u"], f"the {ex['name']} wall"
        far = min(us) if side == "West" else max(us)
        rear = abs(edge - far)
        near_ = abs(edge - (max(us) if side == "West" else min(us)))
        out(f"{side} spawn: starts at u {min(us):+.1f}..{max(us):+.1f}, v {min(vs):+.1f}..{max(vs):+.1f}; distance to {what} (u {edge:+.1f}): rearmost start {rear:.1f} m, front start {near_:.1f} m")
        # rear depth along the lane-ish: farthest reach cell behind the rearmost start
        mk = reach & inside & (np.hypot(XX - np.mean([s[0] for s in sts]), ZZ - np.mean([s[1] for s in sts])) <= 40)
        uu_ = UU[mk]
        out(f"   reachable ground within 40 m of the spawn ends at u {uu_.min():+.1f}..{uu_.max():+.1f}")


# ================================================================== PEEKS
CANON = {"Upper lane - the rock road": "Upper Road", "Main street": "Main Street", "Lower lane - gardens and olives": "Lower Lane",
         "The ravine floor": "Ravine floor", "Plateia (street widening)": "Plateia"}
CALLOUTS = [(c["name"], c["at"]) for c in LAYOUT["callouts"] if c.get("kind") != "lane"]
LANE_CANON = {"upper": "Upper Road", "main": "Main Street", "lower": "Lower Lane"}
_lane_pts = {l["id"]: chaikin(l["points"]) for l in LAYOUT["lanes"]}


def place_name(r, c):
    k = feature[r, c]
    if k >= 0:
        n = feature_names[k]
        return CANON.get(n, n.replace(" (roof)", ""))
    u, v = to_frame(XX[r, c], ZZ[r, c])
    best = min(CALLOUTS, key=lambda cl: math.hypot(cl[1][0] - u, cl[1][1] - v))
    dd = math.hypot(best[1][0] - u, best[1][1] - v)
    if dd <= 14.0:
        return best[0]
    lane = min(_lane_pts, key=lambda i: np.min(np.hypot(_lane_pts[i][:, 0] - u, _lane_pts[i][:, 1] - v)))
    return LANE_CANON[lane] + " verge"


def zone_sets(fid, rmax=110.0):
    p = fp(fid)
    d = np.hypot(XX - p[0], ZZ - p[1])
    idx = np.argwhere(reach1 & (d >= 5.0) & (d <= rmax))
    vis = np.zeros(ground.shape, bool)
    for r, c in idx:
        if los(p, (XX[r, c], ZZ[r, c]), EYE, EYE, 0.5):
            vis[r, c] = True
    return p, d, vis


def _merge_into(m, rc):
    m["s1"] = max(m["s1"], rc["s1"])
    m["n"] += rc["n"]
    m["dmin"], m["dmax"] = min(m["dmin"], rc["dmin"]), max(m["dmax"], rc["dmax"])
    m["dhmin"], m["dhmax"] = min(m["dhmin"], rc["dhmin"]), max(m["dhmax"], rc["dhmax"])
    m["dh35"] = max(m["dh35"], rc["dh35"])
    m["tw"], m["te"] = min(m["tw"], rc["tw"]), min(m["te"], rc["te"])
    m["nH"] += rc["nH"]
    m["nelev"] += rc["nelev"]
    if rc["top"][0] > m["top"][0]:
        m["top"] = rc["top"]


def peek_rows(fid):
    need_dist()
    p, d, vis = zone_sets(fid)
    hz = stand_height(*p)
    v1, r1, d1 = vis[::2, ::2], reach1[::2, ::2], d[::2, ::2]
    calc = r1 & (d1 >= 5.0) & (d1 <= 110.0)
    hid = calc & ~v1
    retreat = binary_dilation(hid, structure=np.ones((3, 3), bool))  # a hidden walkable cell within 1.41 m
    band = (d1 >= 7.0) & (d1 <= 50.0)
    peek, openc = v1 & retreat & band, v1 & ~retreat & band
    X1, Z1, H1 = XX[::2, ::2], ZZ[::2, ::2], walk_h[::2, ::2]
    tW1, tE1 = dW[::2, ::2] / SPRINT, dE[::2, ::2] / SPRINT
    by_sec = defaultdict(list)
    for r, c in zip(*np.nonzero(v1 & band)):
        by_sec[int(bearing(p, (X1[r, c], Z1[r, c])) // 15)].append((r, c, bool(peek[r, c])))
    recs = {}
    for sct, lst in by_sec.items():
        npk = sum(1 for *_, pk in lst if pk)
        nop = len(lst) - npk
        el_pk = sum(1 for r, c, pk in lst if pk and H1[r, c] - hz >= 2.5)
        el_op = sum(1 for r, c, pk in lst if not pk and H1[r, c] - hz >= 2.5)
        # small perches count too: >= 2 elevated (>= 2.5 m) cells are enough, a ground-level peek needs 4 cells
        if npk >= 4 or el_pk >= 2:
            kind, use = "PEEK", [(r, c) for r, c, pk in lst if pk]
        elif nop >= 12 or el_op >= 2:
            kind, use = "OPEN", [(r, c) for r, c, pk in lst if not pk]
        else:
            continue
        names = Counter(place_name(r * 2, c * 2) for r, c in use)
        dist = np.array([d1[r, c] for r, c in use])
        dh = np.array([H1[r, c] - hz for r, c in use])
        top = max(use, key=lambda rc_: H1[rc_[0], rc_[1]])
        recs[sct] = dict(top=(float(H1[top] - hz), float(d1[top]), place_name(top[0] * 2, top[1] * 2), uvs((X1[top], Z1[top])), float(X1[top]), float(Z1[top])),
                         kind=kind, s0=sct, s1=sct, n=len(use), place=names.most_common(1)[0][0], places=names.most_common(3),
                         dmin=float(dist.min()), dmax=float(dist.max()), dhmin=float(dh.min()), dhmax=float(dh.max()),
                         dh35=float(max([x for x, dd in zip(dh, dist) if dd <= 35], default=-99)),
                         tw=float(min(tW1[r, c] for r, c in use)), te=float(min(tE1[r, c] for r, c in use)),
                         nH=int(sum(1 for x, dd in zip(dh, dist) if x >= 5.0 and dd <= 35)), nelev=int((dh >= 2.5).sum()))
    rows = []
    for sct in sorted(recs):
        rc = recs[sct]
        if rows and rows[-1]["kind"] == rc["kind"] and rows[-1]["s1"] == sct - 1 and rows[-1]["place"] == rc["place"]:
            _merge_into(rows[-1], rc)
        else:
            rows.append(dict(rc))
    if len(rows) > 1 and rows[0]["s0"] == 0 and rows[-1]["s1"] == 23 and rows[0]["kind"] == rows[-1]["kind"] and rows[0]["place"] == rows[-1]["place"]:
        last = rows.pop()
        rows[0]["s0"] = last["s0"] - 24
        _merge_into(rows[0], last)
    for m in rows:
        if m["dh35"] >= 5.0:
            m["sev"] = "H"
        elif m["kind"] == "OPEN" and m["dmin"] <= 35 and m["n"] >= 40:
            m["sev"] = "H"
        elif m["dhmax"] >= 2.5 or m["dmin"] > 35 or m["kind"] == "OPEN":
            m["sev"] = "M"
        else:
            m["sev"] = "L"
        m["retreat"] = "Y" if m["kind"] == "PEEK" else "N"
    rr, cc = np.nonzero(vis)
    far = np.zeros(72)
    for r, c in zip(rr, cc):
        k = int(bearing(p, (XX[r, c], ZZ[r, c])) // 5)
        far[k] = max(far[k], d[r, c])
    return dict(p=p, hz=hz, rows=rows, far=far, nvis=int(v1[calc].sum()), ncalc=int(calc.sum()))


def peeks():
    need_dist()
    out("\n=== PEEK AUDIT (MEASURED; flood-fill reach from the spawns <=33 deg, 1 m grid, eye 1.7 m both ends) ===")
    out("peek = visible reach cell 7-50 m out with a hidden reach cell within 1.41 m; open = visible, none hidden within 1.41 m;")
    out("angle = 15 deg bearing sector with >=4 peek cells, or >=2 peek cells 2.5 m or more above the zone (>=12 open cells, or >=2 elevated ones = open axis); adjacent sectors of the same kind AND place merge.")
    summary = {}
    for fid in ("Flag_B", "Flag_A", "Flag_C"):
        R = peek_rows(fid)
        rows = R["rows"]
        out(f"\n--- {fid} ({uvs(R['p'])}, floor {R['hz'] - B:+.1f} m vs B); visible reach cells 5-110 m: {R['nvis']} of {R['ncalc']} ---")
        for x in rows:
            nm = "; ".join(f"{n} ({c})" for n, c in x["places"])
            out(f"  {x['kind']} {x['s0'] * 15 % 360:3d}-{(x['s1'] + 1) * 15 % 360:3d} deg  {x['dmin']:3.0f}-{x['dmax']:3.0f} m  dh {x['dhmin']:+5.1f}..{x['dhmax']:+4.1f}  cells {x['n']:4d}  "
                f"first W {x['tw']:4.1f} / E {x['te']:4.1f} s  retreat {x['retreat']}  sev {x['sev']}  {x['place']}   [{nm}]")
        pk = [x for x in rows if x["kind"] == "PEEK"]
        op = [x for x in rows if x["kind"] == "OPEN"]
        n_sec = sum(x["s1"] - x["s0"] + 1 for x in pk)
        out(f"  => peek angles (merged rows) {len(pk)} [{n_sec} sectors of 15 deg], open axes {len(op)}, total {len(rows)}; elevated>=2.5: {sum(1 for x in rows if x['dhmax'] >= 2.5)}; "
            f"elevated>=5 within 35 m: {sum(1 for x in rows if x['dh35'] >= 5)}; beyond 35 m only: {sum(1 for x in rows if x['dmin'] > 35)}; H-severity: {sum(1 for x in rows if x['sev'] == 'H')}")
        far = R["far"]
        out(f"  interior lines from the centre (furthest visible cell per 5 deg): >25 m: {(far > 25).sum()}/72 bins, >40 m: {(far > 40).sum()}/72, >60 m: {(far > 60).sum()}/72; longest {far.max():.0f} m at {far.argmax() * 5 + 2.5:.0f} deg")
        big = [x for x in rows if x["n"] >= 10]
        out(f"  angles with >=10 cells (10 m2 of standing room): {len(big)} (peek {sum(1 for x in big if x['kind'] == 'PEEK')}, open {sum(1 for x in big if x['kind'] == 'OPEN')}); elevated>=2.5 among them {sum(1 for x in big if x['dhmax'] >= 2.5)}")
        for x in sorted(rows, key=lambda y: -y['top'][0])[:3]:
            out(f"  highest visible spot in a row: {x['top'][0]:+.1f} m at {x['top'][1]:.0f} m, {x['top'][2]} ({x['top'][3]}), bearing {x['s0'] * 15 % 360}-{(x['s1'] + 1) * 15 % 360}")
        summary[fid] = (len(pk), len(op), sum(1 for x in rows if x['dhmax'] >= 2.5), sum(1 for x in rows if x['sev'] == 'H'), len(big))
    out("\nSUMMARY (peek angles, open axes, elevated rows, H rows):", summary)


# ================================================================== HEIGHT
def seg_stats(h, ds=0.5):
    gr = np.abs(np.diff(h)) / ds
    return gr


def height():
    out("\n=== HEIGHT AUDIT (MEASURED; walk surface relative to the Flag B deck) ===")
    for lane in LAYOUT["lanes"]:
        pts = np.array(lane["points"], float)
        sm = chaikin(pts)
        seg = np.hypot(*np.diff(sm, axis=0).T)
        cum = np.concatenate([[0], np.cumsum(seg)])
        s = np.arange(0, cum[-1], 0.5)
        u, v = np.interp(s, cum, sm[:, 0]), np.interp(s, cum, sm[:, 1])
        keep = (np.abs(u) <= HALF_U) & (np.abs(v) <= HALF_V)
        u, v = u[keep], v[keep]
        wx, wz = to_world(u, v)
        h = sample(walk_h, wx, wz) - B
        gr = np.abs(np.diff(h)) / 0.5
        g4 = np.convolve(gr, np.ones(8) / 8, mode="valid")
        out(f"{lane['name']}: {h.min():+.1f}..{h.max():+.1f}, west end {h[0]:+.1f}, east end {h[-1]:+.1f}, steepest 4 m grade {g4.max() * 100:.1f}% ({math.degrees(math.atan(g4.max())):.1f} deg) at u {u[int(np.argmax(g4)) + 4]:+.0f}")
        # flat shelf: longest stretch within +-0.3 m
        run = best = 0
        for k in range(1, len(h)):
            pass
        for cand_center in np.arange(0, len(h), 4):
            lo = hi = int(cand_center)
            while hi + 1 < len(h) and abs(h[hi + 1] - h[int(cand_center)]) <= 0.3:
                hi += 1
            while lo - 1 >= 0 and abs(h[lo - 1] - h[int(cand_center)]) <= 0.3:
                lo -= 1
            best = max(best, (hi - lo) * 0.5)
        out(f"   longest stretch within +-0.3 m of one height: {best:.0f} m")
    # lane separations
    mainl = next(l for l in LAYOUT["lanes"] if l["id"] == "main")
    rows = []
    for u0 in range(-70, 71, 10):
        hs = {}
        for l in LAYOUT["lanes"]:
            uu, vv = lane_points(l, 0.5)
            k = int(np.argmin(np.abs(uu - u0)))
            hs[l["id"]] = float(sample(walk_h, *to_world(uu[k], vv[k]))[0] - B)
        rows.append((u0, hs))
    out("lane heights by u: " + "; ".join(f"u{u0:+d}: U {h['upper']:+.1f} M {h['main']:+.1f} L {h['lower']:+.1f}" for u0, h in rows))
    out(f"Upper - Main {min(h['upper'] - h['main'] for _, h in rows):.1f}..{max(h['upper'] - h['main'] for _, h in rows):.1f} m;  Main - Lower {min(h['main'] - h['lower'] for _, h in rows):.1f}..{max(h['main'] - h['lower'] for _, h in rows):.1f} m")
    out("\nConnectors (walk-test routes, 0.5 m samples; grade windows exclude tread cells for the ramp figure):")
    stairs = set(b["name"].split(" tread ")[0] for b in META["boxes"] if " tread " in b["name"])
    out("stair elements:", sorted(stairs))
    tab = []
    for r in META["routes"]:
        xs_, zs_ = np.array(r["xs"]), np.array(r["zs"])
        sg = np.hypot(np.diff(xs_), np.diff(zs_))
        cum = np.concatenate([[0], np.cumsum(sg)])
        s = np.arange(0, cum[-1], 0.5)
        px, pz = np.interp(s, cum, xs_), np.interp(s, cum, zs_)
        h = sample(walk_h, px, pz)
        on_t = sample(treads.astype(float), px, pz, order=0) > 0.5
        gr = np.abs(np.diff(h)) / 0.5
        g1 = np.convolve(gr, np.ones(2) / 2, mode="valid") if len(gr) > 2 else gr
        g25 = np.convolve(gr, np.ones(5) / 5, mode="valid") if len(gr) > 5 else gr
        g4 = np.convolve(gr, np.ones(8) / 8, mode="valid") if len(gr) > 8 else gr
        nt = np.convolve(on_t, np.ones(5), mode="same") > 0
        mask1 = ~(nt[:len(g1)] | nt[2:len(g1) + 2]) if len(g1) else g1
        deg1 = math.degrees(math.atan(g1[mask1].max())) if len(g1) and mask1.any() else 0.0
        deg25 = math.degrees(math.atan(g25.max()))
        pitch_all = math.degrees(math.atan(g25.max()))
        name = r["name"]
        tr = on_t.mean() * 100
        tab.append(dict(name=name, rise=float(h[-1] - h[0]), climb=float(np.clip(np.diff(h), 0, None).sum()), run=float(cum[-1]), rng=(float(h.min() - B), float(h.max() - B)),
                        mean=abs(h[-1] - h[0]) / max(cum[-1], 1) * 100, deg25=deg25, deg1=deg1, treads=tr, g4=float(g4.max()) if len(g4) else 0.0,
                        g4deg=math.degrees(math.atan(g4.max())) if len(g4) else 0.0,
                        start=float(h[0] - B), end=float(h[-1] - B)))
    for t in tab:
        out(f"  {t['name'][:44]:44s} {t['start']:+5.1f} -> {t['end']:+5.1f} (net {t['rise']:+5.1f}, climbing {t['climb']:4.1f}) run {t['run']:5.1f} m  avg {t['mean']:4.1f}%  steepest 2.5 m {t['deg25']:4.1f} deg, 4 m {t['g4']*100:4.1f}% ({t['g4deg']:.1f} deg)  on treads {t['treads']:3.0f}%  between landings {g['per_rise'].get(t['name'].split(' (')[0], float('nan')):.1f}")
    out("\nTop 5 by rise per metre (steepest 4 m window, % = rise/run):")
    for t in sorted(tab, key=lambda y: -y["g4"])[:5]:
        out(f"  {t['name']}: {t['g4']*100:.0f}% ({t['g4deg']:.1f} deg) over 4 m; net {t['rise']:+.1f} m in {t['run']:.0f} m")
    out("Top 5 by net height change (small lanes):")
    for t in sorted(tab, key=lambda y: -abs(y["rise"]))[:5]:
        out(f"  {t['name']}: {t['rise']:+.1f} m in {t['run']:.0f} m run ({t['mean']:.0f}% average)")
    # the ravine: floor profile and rims
    rv = LAYOUT["ravine"]
    sm = chaikin(np.array(rv["points"], float))
    out("ravine floor profile (centreline ground, relative to B): " + "; ".join(
        f"v{v0:+d} {float(sample(ground, *to_world(*sm[int(np.argmin(np.abs(sm[:, 1] - v0)))]))[0]) - B:+.1f}" for v0 in range(50, -51, -10)))
    # east-bank path perch details
    return


# ================================================================== CONTAINMENT
def contain():
    # The walker's physics decides (Lockdown/Review/Containment Test, PROGRESS decision 2026-10-13): the build world
    # (EditorOnly placeholders gone) is what counts; the editor world is shown next to it.
    out("\n=== CONTAINMENT (PHYSICS: the walker's own moves, Lockdown/Review/Containment Test) ===")
    for world in ("build", "editor"):
        P, state = g["PHYS"][world], g["PHYS_STATE"][world]
        if P is None or state:
            out(f"{world} world: physics graph {state or 'missing'} -> run the Containment Test in Unity (FAIL)")
            continue
        u, v = to_frame(P["pos"][:, 0], P["pos"][:, 2])
        outside = (np.abs(u) > HALF_U) | (np.abs(v) > HALF_V)
        cells = len(np.unique(P["col"][outside]))
        h = P["hdr"]
        out(f"{world} world ('{h['label']}' {h['time']}): reachable outside the 150x104 frame {cells * STEP * STEP:.0f} m2 "
            f"(Unity report {h['outside_m2']:.0f} m2), exits {h['exits']}, closure crossings {h['closure_crossings']}")
        rep = g["ROOT"] / "Screenshots" / "task09" / f"containment_{h['label']}_{world}.txt"
        if rep.exists():
            for line in rep.read_text(encoding="utf-8").splitlines()[3:]:
                out("   " + line)
    out("\n--- grid estimate (old 2.5D flood, NOT the decision: no walker radius, its own step limit) ---")
    wall_cells = np.zeros(ground.shape, bool)
    for b in META["boxes"]:
        if b["group"] == "Walls":
            y = math.radians(b["yaw"])
            fx, fz = math.sin(y), math.cos(y)
            a = (XX - b["x"]) * fx + (ZZ - b["z"]) * fz
            c = (XX - b["x"]) * fz - (ZZ - b["z"]) * fx
            wall_cells |= (np.abs(a) <= b["sz"] / 2) & (np.abs(c) <= b["sx"] / 2)
    slope = g["slope"]
    open_all = ((slope <= 33.0) | deck) & ~(blocked & ~wall_cells)
    # flood from the spawns without the frame limit
    seeds = starts_w + starts_e
    seen = np.zeros(ground.shape, bool)
    stack = []
    for x, z in seeds:
        r, c = (int(round(v)) for v in grid_index(x, z))
        stack.append((r, c))
    wh = walk_h
    lim = 0.4 + STEP * math.tan(math.radians(33.0))
    seen_l = seen.tolist()
    is_open = open_all.tolist()
    whl = wh.tolist()
    while stack:
        r, c = stack.pop()
        if seen_l[r][c]:
            continue
        seen_l[r][c] = True
        h0 = whl[r][c]
        for dr in (-1, 0, 1):
            for dc in (-1, 0, 1):
                rr, cc = r + dr, c + dc
                if 0 <= rr < RES and 0 <= cc < RES and is_open[rr][cc] and not seen_l[rr][cc] and abs(whl[rr][cc] - h0) <= lim:
                    stack.append((rr, cc))
    seen = np.array(seen_l)
    outside = seen & ~inside
    area = outside.sum() * STEP * STEP
    out(f"walkable ground reachable from the spawns (<=33 deg, 0.4 m steps, no frame limit): {seen.sum() * STEP * STEP:.0f} m2, of which OUTSIDE the 150x104 frame: {area:.0f} m2")
    if outside.any():
        d_out = np.maximum(np.abs(UU) - HALF_U, np.abs(VV) - HALF_V)
        out(f"   farthest reachable point outside the frame: {d_out[outside].max():.0f} m beyond the edge; highest {walk_h[outside].max() - B:+.1f} m vs B (tile margin only {(RES - 1) * STEP / 2:.0f} m from the centre)")
        # classify by side
        for name, m in (("west (u<-75)", UU < -HALF_U), ("east (u>75)", UU > HALF_U), ("north (v>52)", VV > HALF_V), ("south (v<-52)", VV < -HALF_V)):
            mm = outside & m
            if mm.any():
                out(f"   {name}: {mm.sum() * STEP * STEP:.0f} m2, height {walk_h[mm].min() - B:+.1f}..{walk_h[mm].max() - B:+.1f}")
    # which frame-edge cells are open ground (the edge has no wall/geometry there)
    edge = inside & ~binary_dilation(inside, iterations=-1) if False else None
    ring = inside & ~(np.abs(UU) <= HALF_U - 1.0) | (inside & ~(np.abs(VV) <= HALF_V - 1.0))
    ring &= inside
    leak = ring & seen
    out(f"frame-edge band (1 m wide): reachable cells {leak.sum() * STEP * STEP:.0f} m2 of {ring.sum() * STEP * STEP:.0f} m2")
    # leak gaps along each edge
    for name, m in (("west edge", ring & (UU < -HALF_U + 1.0)), ("east edge", ring & (UU > HALF_U - 1.0)), ("north edge", ring & (VV > HALF_V - 1.0)), ("south edge", ring & (VV < -HALF_V + 1.0))):
        mm = m & seen
        if mm.any():
            along = VV[mm] if "west" in name or "east" in name else UU[mm]
            order = np.sort(along)
            gaps = np.split(order, np.where(np.diff(order) > 1.5)[0] + 1)
            out(f"   {name}: open ground at " + ", ".join(f"{a[0]:+.0f}..{a[-1]:+.0f}" for a in gaps if len(a) > 2))
    # jump / mantle exploit: a walker (jump 1.0 m, step 0.4 m) climbs onto any block top that is <= 1.0 m above where it stands
    out("\nJump-reach (up onto a block top <= 1.0 m, terrain <= 33 deg; see-through rails and overhead canopies ignored)")
    top = ground.copy()
    owner = np.full(ground.shape, -1)
    for k, b in enumerate(META["boxes"]):
        if b.get("overhead") or b.get("rail"):
            continue
        y = math.radians(b["yaw"])
        fx, fz = math.sin(y), math.cos(y)
        a = (XX - b["x"]) * fx + (ZZ - b["z"]) * fz
        c = (XX - b["x"]) * fz - (ZZ - b["z"]) * fx
        fpm = (np.abs(a) <= b["sz"] / 2) & (np.abs(c) <= b["sx"] / 2)
        t_ = b["y"] + b["sy"] / 2
        upd = fpm & (t_ >= top)
        top[upd] = t_
        owner[upd] = k
    is_block = top > ground + 0.3
    sl_ = np.zeros(ground.shape, bool)
    stack = [(int(round(grid_index(x, z)[0])), int(round(grid_index(x, z)[1]))) for x, z in seeds]
    topl, blk, inl, gl = top.tolist(), is_block.tolist(), inside.tolist(), ground.tolist()
    sl = sl_.tolist()
    lim_t = 0.4 + STEP * math.tan(math.radians(33.0))
    while stack:
        r, c = stack.pop()
        if sl[r][c]:
            continue
        sl[r][c] = True
        h0 = topl[r][c]
        for dr in (-1, 0, 1):
            for dc in (-1, 0, 1):
                rr, cc = r + dr, c + dc
                if not (0 <= rr < RES and 0 <= cc < RES) or sl[rr][cc] or not inl[rr][cc]:
                    continue
                dh = topl[rr][cc] - h0
                if blk[rr][cc]:
                    ok_ = dh <= 1.0          # jump up onto a block top
                else:
                    ok_ = dh <= lim_t        # terrain: slope limit (no climbing a 55 deg cliff from a block)
                if ok_:
                    stack.append((rr, cc))
    sl = np.array(sl)
    base_ok = seen & inside
    extra = sl & ~base_ok & is_block
    out(f"   extra standing area reachable by jumping onto blocks: {extra.sum() * STEP * STEP:.0f} m2")
    per = defaultdict(lambda: [0, -99, 99])
    for k in np.unique(owner[extra]):
        if k < 0:
            continue
        m = extra & (owner == k)
        nm = META["boxes"][k]["name"]
        per[nm][0] += m.sum() * STEP * STEP
        per[nm][1] = max(per[nm][1], float(top[m].max() - B))
        per[nm][2] = min(per[nm][2], float((top[m] - ground[m]).max()))
    tall = sorted(((n, a, hmax) for n, (a, hmax, hh) in per.items()), key=lambda x: -x[2])[:25]
    out("   highest reachable block tops (name, m2 standable, top vs B):")
    for n, a_, hmax in tall:
        out(f"     {n[:50]:50s} {a_:6.1f} m2  {hmax:+.1f}")
    # climb height above the local intended ground
    over = [(n, a_, hmax) for n, (a_, hmax, hh) in per.items() if hh > 1.6]
    out(f"   blocks whose reachable top stands >1.6 m above the ground under it: {len(over)}")
    return


# ================================================================== SUN
def sun():
    out("\n=== SUN (MEASURED; sun azimuth 263.9 deg true, elevation 19.9 deg, Task 5 review lighting) ===")
    AZ = 263.9
    for lane in LAYOUT["lanes"]:
        u, v = lane_points(lane, 2.0)
        wx, wz = to_world(u, v)
        hd = np.array([bearing((wx[k], wz[k]), (wx[k + 1], wz[k + 1])) for k in range(len(wx) - 1)])
        # angle between the lane axis (either direction) and the sun azimuth
        d = np.abs(((hd - AZ + 180) % 360) - 180)
        axis = np.minimum(d, 180 - d)
        west = np.abs(((hd + 180 - AZ + 180) % 360) - 180)  # looking back (towards the sun) vs forwards, whichever is nearer
        out(f"{lane['id']:6s} axis-to-sun angle: min {axis.min():.0f} deg, median {np.median(axis):.0f}, max {axis.max():.0f}; share of length within 45 deg of the sun axis: {(axis < 45).mean() * 100:.0f}%; within 20 deg: {(axis < 20).mean() * 100:.0f}%")
        wf = np.abs((((hd + 180) % 360 - AZ + 180) % 360) - 180)  # travelling west = looking into the sun
        facing = wf[wf <= 90]
        if not len(facing):
            facing = np.array([90.0])
        out(f"       segments heading within 90 deg of the sun azimuth (looking into the sun): {len(facing)} of {len(hd)}, offset median {np.median(facing):.0f} deg, 10th pct {np.percentile(facing, 10):.0f} deg, min {facing.min():.0f}")
        k = int(np.argmin(axis))
        out(f"       closest segment at u {u[k]:+.0f} (heading {hd[k]:.0f} deg)")
    for c in LAYOUT["connectors"]:
        pass


# ================================================================== TERRAIN
def terrain():
    out("\n=== TERRAIN SLOPE (MEASURED; graded ground raster, 0.5 m, inside the frame) ===")
    gz, gx = np.gradient(ground, STEP)
    sl = np.degrees(np.arctan(np.hypot(gx, gz)))
    ins = inside
    for lim in (20, 33, 35, 45):
        out(f"  frame cells steeper than {lim} deg: {(sl[ins] > lim).mean() * 100:.1f}%")
    sl2 = g["slope"]
    out(f"  with blocks (walk surface): >35 deg {(sl2[ins] > 35).mean() * 100:.1f}%")
    sf = surface & inside
    out(f"  walkable surface (lanes+connectors+pads+decks) steeper than 20 deg: {(sl2[sf] > 20).mean() * 100:.1f}% of {sf.sum() * STEP * STEP:.0f} m2")


# ================================================================== ROUTES, APPROACHES, COVER, DEAD ENDS
def lane_of_uv(u, v):
    best, bd = None, 1e9
    for lid, pts in _lane_pts.items():
        dd = float(np.min(np.hypot(pts[:, 0] - u, pts[:, 1] - v)))
        if dd < bd:
            best, bd = lid, dd
    return best, bd


ENTRY = defaultdict(list)


def routes():
    need_dist()
    out("\n=== ROUTES (MEASURED; 1 m grid, same walkable raster, 6.5 m/s) ===")
    walk1 = (walkable & inside)[::2, ::2]
    h1 = walk_h[::2, ::2]
    N = walk1.shape[0]
    X1, Z1 = XX[::2, ::2], ZZ[::2, ::2]
    U1, V1 = UU[::2, ::2], VV[::2, ::2]
    lim = 0.4 + math.tan(math.radians(33.0))

    def cell(p):
        r, c = (int(round(v / 2)) for v in grid_index(*p))
        win = [(r + a, c + b) for a in range(-3, 4) for b in range(-3, 4) if 0 <= r + a < N and 0 <= c + b < N and walk1[r + a, c + b]]
        return min(win, key=lambda t: (t[0] - r) ** 2 + (t[1] - c) ** 2)

    wl, hl = walk1.tolist(), h1.tolist()

    def dijk(src, dst, banned):
        dist = {src: 0.0}
        prev = {}
        pq = [(0.0, src)]
        while pq:
            d, (r, c) = heapq.heappop(pq)
            if (r, c) == dst:
                break
            if d > dist.get((r, c), 1e18):
                continue
            h0 = hl[r][c]
            for dr in (-1, 0, 1):
                for dc in (-1, 0, 1):
                    if dr == 0 and dc == 0:
                        continue
                    rr, cc = r + dr, c + dc
                    if not (0 <= rr < N and 0 <= cc < N) or not wl[rr][cc] or banned[rr][cc]:
                        continue
                    w = 1.4142 if dr and dc else 1.0
                    if abs(hl[rr][cc] - h0) > lim * w:
                        continue
                    nd = d + w
                    if nd < dist.get((rr, cc), 1e18):
                        dist[(rr, cc)] = nd
                        prev[(rr, cc)] = (r, c)
                        heapq.heappush(pq, (nd, (rr, cc)))
        if dst not in dist:
            return None
        path, cur = [dst], dst
        while cur != src:
            cur = prev[cur]
            path.append(cur)
        return dist[dst], path[::-1]

    def near_mask(path, rad):
        m = np.zeros((N, N), bool)
        for r, c in path:
            m[max(r - rad, 0):r + rad + 1, max(c - rad, 0):c + rad + 1] = True
        # disc, not square: use distance
        return m

    zones = {"A": fp("Flag_A"), "B": fp("Flag_B"), "C": fp("Flag_C"), "W": starts_w[0], "E": starts_e[0]}
    cells = {k: cell(p) for k, p in zones.items()}
    pairs = [("A", "B"), ("B", "C"), ("A", "C"), ("W", "A"), ("W", "B"), ("W", "C"), ("E", "A"), ("E", "B"), ("E", "C"), ("W", "E")]
    lane_share = defaultdict(float)
    tot = 0.0
    for a, b in pairs:
        ca, cb = cells[a], cells[b]
        res = []
        banned = np.zeros((N, N), bool)
        for k in range(3):
            r_ = dijk(ca, cb, banned.tolist())
            if not r_:
                res.append(None)
                break
            d, path = r_
            res.append((d, path))
            # corridor ban: 6 m around the path, except within 12 m of both ends
            ban_new = np.zeros((N, N), bool)
            pa = np.array(path)
            for r, c in path[::2]:
                ban_new[max(r - 6, 0):r + 7, max(c - 6, 0):c + 7] = True
            keep_a = np.zeros((N, N), bool)
            rr_, cc_ = np.ogrid[:N, :N]
            for end in (ca, cb):
                keep_a |= (rr_ - end[0]) ** 2 + (cc_ - end[1]) ** 2 <= 12 ** 2
            banned |= ban_new & ~keep_a
        txt = []
        for k, rr in enumerate(res):
            if rr is None:
                txt.append(f"route {k + 1}: none")
                continue
            d, path = rr
            if res[0] is not None and d <= 1.6 * res[0][0]:
                for zone_key, seq in ((b, path[::-1]), (a, path)):
                    if zone_key in ("A", "B", "C"):
                        zc = cells[zone_key]
                        for r, c in seq:
                            if math.hypot(r - zc[0], c - zc[1]) >= 16.0:
                                ENTRY[zone_key].append((bearing(zones[zone_key], (X1[r, c], Z1[r, c])), d / SPRINT, f"{a}->{b} r{k + 1}"))
                                break
            ln = Counter()
            for r, c in path:
                lid, dd = lane_of_uv(U1[r, c], V1[r, c])
                ln[lid if dd <= 4.0 else "connector/other"] += 1
            if k == 0 or True:
                for kk, vv in ln.items():
                    lane_share[kk] += vv * (1.0 if k == 0 else 0.0)
            tot += len(path) if k == 0 else 0
            txt.append(f"route {k + 1}: {d / SPRINT:.1f} s ({d:.0f} m; " + ", ".join(f"{kk} {vv}" for kk, vv in ln.most_common(3)) + ")")
        out(f"{a}->{b}: " + " | ".join(txt))
    out("share of shortest-route length by lane (10 pairs):", {k: f"{v / tot * 100:.0f}%" for k, v in lane_share.items()})

    # lane viability: time spawn -> flag when forced through a point on a lane
    out("\nForced-lane times (via a lane-centre point, u -40/-15/10/35 best), seconds; baseline = unconstrained:")
    for side, dsrc in (("West", dW), ("East", dE)):
        for fid in ("Flag_A", "Flag_B", "Flag_C"):
            dflag = dist_from(fp(fid))
            base = t_at(dsrc, fp(fid))
            row = []
            for lane in LAYOUT["lanes"]:
                u, v = lane_points(lane, 1.0)
                best = 1e9
                for u0 in (-40, -15, 10, 35):
                    k = int(np.argmin(np.abs(u - u0)))
                    wp = to_world(u[k], v[k])
                    best = min(best, t_at(dsrc, wp) + t_at(dist_from(wp), fp(fid)))
                row.append(f"{lane['id']} {best:.1f} ({best / base:.2f}x)")
            out(f"  {side}->{fid[-1]} base {base:.1f}: " + "; ".join(row))
    return


def approaches():
    need_dist()
    if not ENTRY:
        import contextlib, io
        with contextlib.redirect_stdout(io.StringIO()):
            routes()
    out("\n=== ZONE APPROACHES AND COVER (MEASURED) ===")
    for fid in ("Flag_A", "Flag_B", "Flag_C"):
        z = fid[-1]
        p = fp(fid)
        bs = sorted(ENTRY[z])
        clusters = []
        for b_, t_, lab in bs:
            if clusters and b_ - clusters[-1][-1][0] <= 35.0:
                clusters[-1].append((b_, t_, lab))
            else:
                clusters.append([(b_, t_, lab)])
        if len(clusters) > 1 and bs[0][0] + 360 - bs[-1][0] <= 35.0:
            clusters[0] = clusters[-1] + clusters[0]
            clusters.pop()
        out(f"{fid}: {len(clusters)} approach corridors at 16 m (routes <= 1.6x the shortest, banned-corridor alternates from both spawns and the other flags): " +
            "; ".join(f"{min(x[0] for x in c):.0f}-{max(x[0] for x in c):.0f} deg ({len(c)} routes: {', '.join(sorted(set(x[2] for x in c))[:4])})" for c in clusters))
        inside_c, ring_c = [], []
        for b in META["boxes"]:
            dd = math.hypot(b["x"] - p[0], b["z"] - p[1])
            if dd <= 12.0 and 0.9 <= b["sy"] <= 2.6 and min(b["sx"], b["sz"]) >= 0.3 and max(b["sx"], b["sz"]) >= 0.8 and b["group"] in ("Cover", "Walls", "Bridges", "Buildings") and not b.get("rail") and not b.get("overhead"):
                (inside_c if dd <= cap_r else ring_c).append(b["name"])
        out(f"   cover-height blocks (0.9-2.6 m tall, >= 0.8 m long): within the {cap_r:.0f} m circle {len(inside_c)}, 5-12 m ring {len(ring_c)}  e.g. {', '.join(sorted(set(ring_c))[:6])}")
        tall = [b["name"] for b in META["boxes"] if math.hypot(b["x"] - p[0], b["z"] - p[1]) <= 12.0 and b["sy"] > 2.6 and not b.get("overhead")]
        out(f"   walls/buildings > 2.6 m within 12 m: {len(tall)}")


def pockets():
    out("\n=== EXITS OF EVERY PAD / ROOF (MEASURED; walkable designed surface just outside the footprint, arcs >= 2 m wide, step <= 0.5 m) ===")
    surf = (surface | deck) & walkable
    targets = []
    for a in LAYOUT["areas"]:
        if a["shape"] == "circle":
            m = np.hypot(UU - a["center"][0], VV - a["center"][1]) <= a["radius"]
            R = a["radius"]
        else:
            m = (np.abs(UU - a["center"][0]) <= a["size"][0] / 2) & (np.abs(VV - a["center"][1]) <= a["size"][1] / 2)
            R = max(a["size"]) / 2
        targets.append((a["name"], m, a["center"], R))
    for name, fp_ in g["roofs"]:
        targets.append((name, fp_, None, None))
    for name, m, center, R in targets:
        if not (m & surf).any():
            continue
        grown = binary_dilation(m, iterations=6)  # 3 m
        edge = grown & ~m & surf & ~binary_dilation(m, iterations=3) * False
        edge = grown & ~binary_dilation(m, iterations=2) & surf
        idx = np.argwhere(edge)
        if not len(idx):
            out(f"   {name:46s} exits 0")
            continue
        cen = np.array([XX[m].mean(), ZZ[m].mean()])
        angs = np.array([math.degrees(math.atan2(XX[r, c] - cen[0], ZZ[r, c] - cen[1])) % 360 for r, c in idx])
        rad = np.array([math.hypot(XX[r, c] - cen[0], ZZ[r, c] - cen[1]) for r, c in idx])
        order = np.argsort(angs)
        a_s, r_s = angs[order], rad[order]
        gap = math.degrees(2.5 / max(float(np.median(rad)), 3.0))
        splits = np.where(np.diff(a_s) > max(gap, 6.0))[0] + 1
        arcs = np.split(a_s, splits)
        if len(arcs) > 1 and a_s[0] + 360 - a_s[-1] <= max(gap, 6.0):
            arcs[0] = np.concatenate([arcs[-1], arcs[0]])
            arcs.pop()
        wide = [x for x in arcs if len(x) >= 4]
        out(f"   {name[:46]:46s} exits {len(wide)}  (bearings {', '.join(f'{x.min():.0f}-{x.max():.0f}' for x in wide)})")


def extras():
    need_dist()
    out("\n=== EXTRA LINES (MEASURED) ===")
    fl = {k: fp(k) for k in ("Flag_A", "Flag_B", "Flag_C")}
    for a_, b_ in (("Flag_A", "Flag_B"), ("Flag_B", "Flag_C"), ("Flag_A", "Flag_C")):
        pa, pb = fl[a_], fl[b_]
        d = math.hypot(pa[0] - pb[0], pa[1] - pb[1])
        ring = lambda p: [p] + [(p[0] + cap_r * math.cos(k * math.pi / 6), p[1] + cap_r * math.sin(k * math.pi / 6)) for k in range(12)]
        n = sum(1 for x in ring(pa) for y in ring(pb) if los(x, y, EYE, EYE))
        out(f"{a_} <-> {b_}: {d:.0f} m apart, centre-to-centre LOS {los(pa, pb, EYE, EYE)}, capture-circle ring pairs with LOS {n}/169")
    # zone rings -> starts
    starts = starts_w + starts_e
    for f_, p in fl.items():
        for rad in (cap_r, 7.0, 8.0, 10.0, 15.0):
            ring = [(p[0] + rad * math.cos(k * math.pi / 12), p[1] + rad * math.sin(k * math.pi / 12)) for k in range(24)]
            hit = [(i, j) for i, q in enumerate(ring) for j, s_ in enumerate(starts) if los(q, s_, EYE, EYE)]
            if hit:
                i, j = hit[0]
                out(f"{f_}: ring r={rad:.0f} m -> start points: {len(hit)} lines of {24 * 12}; first at ring point {uvs(ring[i])} -> start {j} {uvs(starts[j])} ({math.hypot(ring[i][0] - starts[j][0], ring[i][1] - starts[j][1]):.0f} m)")
            else:
                out(f"{f_}: ring r={rad:.0f} m -> start points: none")
    # who sees the B deck from the spawn exits
    for side, sts in (("West", starts_w), ("East", starts_e)):
        deckpts = []
        for b_ in META['boxes']:
            if b_['name'] == 'Main bridge deck':
                yw = math.radians(b_['yaw'])
                for a_ in (-0.4, 0, 0.4):
                    for c_ in (-0.4, 0, 0.4):
                        deckpts.append((b_['x'] + math.sin(yw) * a_ * b_['sz'] + math.cos(yw) * c_ * b_['sx'], b_['z'] + math.cos(yw) * a_ * b_['sz'] - math.sin(yw) * c_ * b_['sx']))
                out(f"Main bridge deck {b_['sx']:.1f} x {b_['sz']:.1f} m, capture radius {cap_r} m")
        n = sum(1 for q in deckpts for s_ in sts if los(q, s_, EYE, EYE))
        out(f"{side} starts <- bridge deck points: {n} lines of {len(deckpts) * len(sts)}")


def deadends():
    from skimage.morphology import skeletonize
    out("\n=== DEAD ENDS (MEASURED; skeleton of the reachable walking network, 1 m grid) ===")
    m = (reach & surface)[::2, ::2]
    sk = skeletonize(m)
    nb = sum(np.roll(np.roll(sk, a, 0), b, 1) for a in (-1, 0, 1) for b in (-1, 0, 1) if (a, b) != (0, 0))
    ends = sk & (nb == 1)
    X1, Z1 = XX[::2, ::2], ZZ[::2, ::2]
    rr, cc = np.nonzero(ends)
    need_dist()
    rows = []
    for r, c in zip(rr, cc):
        u, v = to_frame(X1[r, c], Z1[r, c])
        edge = min(HALF_U - abs(u), HALF_V - abs(v))
        k = feature[r * 2, c * 2]
        nm = feature_names[k] if k >= 0 else "?"
        rows.append((u, v, edge, nm, float(dW[r * 2, c * 2] / SPRINT), float(dE[r * 2, c * 2] / SPRINT)))
    out(f"skeleton end points: {len(rows)} (those 10 m or more from the frame edge are real dead ends)")
    for u, v, edge, nm, tw_, te_ in sorted(rows, key=lambda x: -x[2]):
        if edge >= 3.0:
            out(f"   u{u:+.0f} v{v:+.0f}  {edge:.0f} m from the edge  {nm}   W {tw_:.1f} s / E {te_:.1f} s")


def widths():
    out("\n=== CLEAR WIDTH OF EVERY ROUTE (MEASURED; perpendicular scan to the first block >= 1.0 m above the surface, 7 m cap each side) ===")
    for r in META["routes"]:
        xs_, zs_ = np.array(r["xs"]), np.array(r["zs"])
        sg = np.hypot(np.diff(xs_), np.diff(zs_))
        cum = np.concatenate([[0], np.cumsum(sg)])
        s = np.arange(1, cum[-1] - 1, 2.0)
        px, pz = np.interp(s, cum, xs_), np.interp(s, cum, zs_)
        tx, tz = np.gradient(px), np.gradient(pz)
        nn = np.hypot(tx, tz) + 1e-9
        tx, tz = tx / nn, tz / nn
        w = []
        for i in range(len(px)):
            side = []
            for sg_ in (1, -1):
                hit = 7.0
                for dd in np.arange(0.25, 7.0, 0.25):
                    x, z = px[i] - tz[i] * sg_ * dd, pz[i] + tx[i] * sg_ * dd
                    rr_, cc_ = int(round((z - g["Z0"]) / STEP)), int(round((x - g["X0"]) / STEP))
                    if blocked[rr_, cc_] and occ[rr_, cc_] - walk_h[rr_, cc_] >= 1.0:
                        hit = dd
                        break
                side.append(hit)
            w.append(sum(side))
        w = np.array(w)
        out(f"  {r['name'][:46]:46s} min {w.min():4.1f} m, median {np.median(w):4.1f} m, share under 2.0 m {(w < 2.0).mean() * 100:3.0f}%, under 3.0 m {(w < 3.0).mean() * 100:3.0f}%")


# ================================================================== PERCH EXPOSURE (high ground costs exposure)
def perch():
    need_dist()
    out("\n=== HIGH GROUND COSTS EXPOSURE (MEASURED): counter-angles on each elevated spot that sees a zone ===")
    for fid in ("Flag_B", "Flag_A", "Flag_C"):
        R = peek_rows(fid)
        for x in R["rows"]:
            if x["dhmax"] < 2.5:
                continue
            # the highest cell of the row: reuse its recorded world position
            wx, wz = x["top"][4], x["top"][5]
            sp = (wx, wz)
            d = np.hypot(XX - wx, ZZ - wz)
            idx = np.argwhere(reach1 & (d >= 7.0) & (d <= 50.0))
            secs = Counter()
            below = 0
            for r, c in idx[::2]:
                if los(sp, (XX[r, c], ZZ[r, c]), EYE, EYE, 0.5):
                    secs[int(bearing(sp, (XX[r, c], ZZ[r, c])) // 15)] += 1
            n_sec = sum(1 for v in secs.values() if v >= 4)
            out(f"  {fid[-1]} zone, row {x['s0'] * 15 % 360}-{(x['s1'] + 1) * 15 % 360} {x['place']} (+{x['top'][0]:.1f} m at {x['top'][1]:.0f} m, {x['top'][3]}): the spot can be seen from {n_sec} sectors of 15 deg (>=4 cells), {sum(secs.values())} half-grid cells, {x['kind']}")


# ================================================================== LANDMARKS
def landmarks():
    need_dist()
    out("\n=== LANDMARK READABILITY (MEASURED; share of reach cells 7-60 m from the zone that see the roofline) ===")
    boxes = {b["name"]: b for b in META["boxes"]}

    def find(prefix):
        # exact name first (a prefix match once picked the "Olive press terrace wall" strip instead of the press); a tuple
        # lists alternatives in order (the press block is "Olive press" until Task 9's review 3 replaced it)
        for pf in (prefix if isinstance(prefix, tuple) else (prefix,)):
            c = [b for b in META["boxes"] if b["name"] == pf] or [b for b in META["boxes"] if b["name"].startswith(pf)]
            if c:
                return c[0]
        return None

    lm = {"A": [("Switchback stair head", "Switchback stair head west pier")], "B": [("The Skeleton", "The Skeleton"), ("Plane tree canopy", "Plane tree canopy"), ("Kafeneio", "Kafeneio body")],
          "C": [("Olive press", ("Olive press body", "Olive press")), ("Olive press chimney", "Olive press chimney")]}
    out("   (landmark, height above its ground, prominence = top minus median blocker height within 20 m)")
    for z, items in lm.items():
        p = fp("Flag_" + z)
        d = np.hypot(XX - p[0], ZZ - p[1])
        idx = np.argwhere(reach1 & (d >= 7.0) & (d <= 60.0))
        for label, pref in items:
            b = find(pref)
            if b is None:
                out(f"   zone {z}: {label}: not in the scene boxes")
                continue
            top = b["y"] + b["sy"] / 2
            gh = float(sample(ground, b["x"], b["z"])[0])
            near = np.hypot(XX - b["x"], ZZ - b["z"]) <= 20
            prom = top - float(np.median(occ[near]))
            # footprint exclusion
            yw = math.radians(b["yaw"])
            fx, fz = math.sin(yw), math.cos(yw)
            a_ = (XX - b["x"]) * fx + (ZZ - b["z"]) * fz
            c_ = (XX - b["x"]) * fz - (ZZ - b["z"]) * fx
            foot = (np.abs(a_) <= b["sz"] / 2 + 0.6) & (np.abs(c_) <= b["sx"] / 2 + 0.6)
            occ2 = occ.copy()
            occ2[foot] = ground[foot]
            seen = tot_ = 0
            for r, c in idx[::2]:
                q0 = (XX[r, c], ZZ[r, c])
                dx, dz = b["x"] - q0[0], b["z"] - q0[1]
                dd = math.hypot(dx, dz)
                if dd < 1:
                    continue
                inset = min(b["sx"], b["sz"]) / 2
                tgt = (b["x"] - dx / dd * inset, b["z"] - dz / dd * inset)
                n = max(int(dd / 0.5), 2)
                t = np.linspace(0, 1, n)[1:-1]
                xs_, zs_ = q0[0] + (tgt[0] - q0[0]) * t, q0[1] + (tgt[1] - q0[1]) * t
                h0 = stand_height(*q0) + EYE
                line = h0 + (top + 0.3 - h0) * t
                ok = bool(np.all(line > sample(occ2, xs_, zs_) + 0.05))
                tot_ += 1
                seen += ok
            out(f"   zone {z}: {label}: {top - gh:.1f} m tall (top {top - B:+.1f} vs B), prominence {prom:.1f} m, roofline seen from {seen / max(tot_, 1) * 100:.0f}% of {tot_} cells")
    # lane landmarks: how many lane centre points (2 m) see each candidate roofline
    out("   lane landmarks (share of that lane's centre points, 2 m apart, that see the roofline):")
    for lane in LAYOUT["lanes"]:
        u, v = lane_points(lane, 2.0)
        wx, wz = to_world(u, v)
        res = []
        for label, pref in [("Bell tower", "Bell tower"), ("Plane tree canopy", "Plane tree canopy"), ("The Skeleton", "The Skeleton"), ("Rock Roofs", "Rock Roofs roof M"), ("East roofs", "East roofs roof W"), ("Olive press", ("Olive press body", "Olive press")), ("Kafeneio", "Kafeneio body")]:
            b = find(pref)
            if b is None:
                continue
            top = b["y"] + b["sy"] / 2
            seen = 0
            yw = math.radians(b["yaw"])
            fx, fz = math.sin(yw), math.cos(yw)
            a_ = (XX - b["x"]) * fx + (ZZ - b["z"]) * fz
            c_ = (XX - b["x"]) * fz - (ZZ - b["z"]) * fx
            foot = (np.abs(a_) <= b["sz"] / 2 + 0.6) & (np.abs(c_) <= b["sx"] / 2 + 0.6)
            occ2 = occ.copy()
            occ2[foot] = ground[foot]
            for x, z in zip(wx, wz):
                dx, dz = b["x"] - x, b["z"] - z
                dd = math.hypot(dx, dz)
                if dd > 80 or dd < 3:
                    continue
                inset = min(b["sx"], b["sz"]) / 2
                tgt = (b["x"] - dx / dd * inset, b["z"] - dz / dd * inset)
                n = max(int(dd / 0.5), 2)
                t = np.linspace(0, 1, n)[1:-1]
                xs_, zs_ = x + (tgt[0] - x) * t, z + (tgt[1] - z) * t
                h0 = stand_height(x, z) + EYE
                line = h0 + (top + 0.3 - h0) * t
                seen += bool(np.all(line > sample(occ2, xs_, zs_) + 0.05))
            res.append(f"{label} {seen / len(wx) * 100:.0f}%")
        out(f"     {lane['id']}: " + ", ".join(res))


# ================================================================== MODES
def modes():
    need_dist()
    out("\n=== MODE SUPPORT (ESTIMATED: no hill or flip-spawn data exists in the project yet) ===")
    cand = {"Switchback Head (A)": fp("Flag_A"), "Church Yard": to_world(-30, -31), "Plateia": to_world(*[-16, -4]), "The Bridge (B)": fp("Flag_B"), "Skeleton yard": to_world(19, -10.5), "Olive Press (C)": fp("Flag_C"),
            "Aloni": to_world(62, 18), "Rock Roofs": to_world(-14, 22), "Press terraces": to_world(32, -33)}
    dd = {k: dist_from(p) for k, p in cand.items()}
    out("candidate hill: time West / East / difference")
    for k, p in cand.items():
        tw_, te_ = t_at(dW, p), t_at(dE, p)
        out(f"   {k:18s} {tw_:5.1f} / {te_:5.1f} s   diff {abs(tw_ - te_):.1f} s")
    ks = list(cand)
    out("hop times between candidate hills (s):")
    for i, a in enumerate(ks):
        out("   " + a[:16].ljust(16) + " " + " ".join(f"{t_at(dd[a], cand[b]):5.1f}" for b in ks))
    # flip areas: places 20-40 m from a spawn that the enemy reaches late
    out("flip candidates (own time <= 8 s, enemy time >= 14 s):")
    for k, (u, v) in {"Gravel yard": (-66, 22), "Empty lot": (-44, 24), "Dry stream": (-62, -26), "Aloni": (62, 18), "Olive grove": (58, -50), "East yard south": (60, -30), "West yard": (-66, -8), "East yard": (67, -16)}.items():
        p = to_world(u, v)
        tw_, te_ = t_at(dW, p), t_at(dE, p)
        out(f"   {k:16s} W {tw_:5.1f} s / E {te_:5.1f} s")


# ================================================================== NAMES AND CONSISTENCY
def names():
    out("\n=== NAMES (canonical = layout callouts / lane / flag / spawn ids) ===")
    can = [c["name"] for c in LAYOUT["callouts"]]
    out("callouts:", can)
    out("lane names:", [l["name"] for l in LAYOUT["lanes"]])
    out("connector names:", [c["name"] for c in LAYOUT["connectors"]])
    out("area names:", [a["name"] for a in LAYOUT["areas"]])
    out("flags:", [(f["id"], f["callout"]) for f in LAYOUT["flags"]], "spawns:", [(s["id"], s["callout"]) for s in LAYOUT["spawns"]])
    cnt = Counter(b["name"].split(" ")[0] + " " + " ".join(b["name"].split(" ")[1:2]) for b in META["boxes"])
    nm = sorted(set(b["name"] for b in META["boxes"] if not any(ch.isdigit() for ch in b["name"])))
    out("greybox box names without numbers:", nm[:80])
    out("stake names:", [s["name"] for s in META["stakes"] if not s["kind"].startswith("start_")])




if __name__ == "__main__":
    FN = {k: v for k, v in globals().items() if callable(v)}
    for sec in SECTIONS:
        FN[sec]()
