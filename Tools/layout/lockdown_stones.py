"""Flagstone packing for the kalderimi (stone paths) of LOCKDOWN.

Used by lockdown_greybox.py. Stones are packed in plan (frame metres, so a stone has the same size on flat ground, on
a ramp and at the mouths), as irregular convex polygons: dart-throwing with size classes, a power diagram (random
size weights) clipped per stone, a gentle relaxation, then each polygon is inset for the joint. Heights are not
decided here: the caller drapes every vertex on the ground.

pack(cs, cu, cv, width, seed, cfg) -> list of stones {"poly": Nx2 (u,v), "tone": int, "proud": m, "tilt": (gu, gv)}
and the width envelope (a function of arc length).
"""
import math

import numpy as np

# palette swatches (placeholder look, matte): 16 neutral limestone greys, 3 faint beige-grey, 2 faint pink-grey,
# 3 darker greys
TONES = (
    [(g + 1, g, g - 2) for g in np.linspace(118, 165, 16).round().astype(int)]
    + [(146, 142, 133), (138, 134, 126), (152, 148, 139)]
    + [(144, 137, 134), (136, 130, 128)]
    + [(104, 103, 101), (96, 95, 94), (110, 109, 106)]
)
N_LIME = 16
TONE_W = np.array([0.80 / N_LIME] * N_LIME + [0.04 / 3] * 3 + [0.02 / 2] * 2 + [0.14 / 3] * 3)


def roughen(p, rng):
    """Turn a convex inset polygon into an irregular, worn fieldstone (neither a sharp Voronoi polygon nor a uniform
    circle/oval):
    - occasionally merge a short side so 4-, 5-, 6-, and 7-sided base shapes all appear
    - apply random directional squash + asymmetric per-vertex inward pull (staying convex via slight smoothing) so no
      stone looks symmetric or regular
    - 2 passes of asymmetric corner-cutting with per-corner random cut depths (some corners worn deep and round,
      others left with a broader natural facet) so edges stay clean and worn without jagged concave notches."""
    cx, cy = _centroid(p)
    pts = list(p)

    # 1. Occasionally merge one short side on 6-7-sided polygons so we get a random mix of 4-7-sided base stones
    if len(pts) >= 6 and rng.random() < 0.50:
        n0 = len(pts)
        lens = [math.hypot(pts[(i + 1) % n0][0] - pts[i][0], pts[(i + 1) % n0][1] - pts[i][1]) for i in range(n0)]
        k_drop = int(np.argmin(lens))
        del pts[k_drop]

    # 2. Directional squash + smooth per-vertex radial variation (breaks Voronoi straightness & symmetry)
    a = rng.uniform(0, math.pi)
    ux, uy = math.cos(a), math.sin(a)
    k = rng.uniform(0.75, 1.0)
    n_pts = len(pts)
    raw_pulls = rng.uniform(0.84, 1.0, n_pts)
    # 3-tap circular blend keeps the deformed polygon convex (no jagged concave notches) while varying corner reach
    pulls = [0.2 * raw_pulls[i - 1] + 0.6 * raw_pulls[i] + 0.2 * raw_pulls[(i + 1) % n_pts] for i in range(n_pts)]
    q = []
    for (x, y), pull in zip(pts, pulls):
        dx, dy = x - cx, y - cy
        along = dx * ux + dy * uy
        dx, dy = dx - (1 - k) * along * ux, dy - (1 - k) * along * uy
        q.append((cx + dx * pull, cy + dy * pull))

    # 3. Pass 1: asymmetric corner cutting on corners sharper than 148 deg.
    #    Randomize how deeply each corner is worn (0.14 to 0.34) so some corners round broadly and others stay firmer.
    pass1 = []
    n1 = len(q)
    cos148 = math.cos(math.radians(148.0))
    cos105 = math.cos(math.radians(105.0))
    for i in range(n1):
        pv, v, nx = q[i - 1], q[i], q[(i + 1) % n1]
        e1, e2 = (pv[0] - v[0], pv[1] - v[1]), (nx[0] - v[0], nx[1] - v[1])
        l1, l2 = math.hypot(*e1), math.hypot(*e2)
        cosang = (e1[0] * e2[0] + e1[1] * e2[1]) / max(l1 * l2, 1e-12)
        if cosang > cos105 and l1 > 0.012 and l2 > 0.012:
            # Acute/sharp corner: always cut generously (22-35%) so no sharp triangle tip survives
            f1 = rng.uniform(0.22, 0.35)
            f2 = rng.uniform(0.22, 0.35)
            pass1.append((v[0] + e1[0] * f1, v[1] + e1[1] * f1))
            pass1.append((v[0] + e2[0] * f2, v[1] + e2[1] * f2))
        elif cosang > cos148 and l1 > 0.014 and l2 > 0.014:
            # Medium corner: asymmetric cut (12-30%)
            f1 = rng.uniform(0.12, 0.30)
            f2 = rng.uniform(0.12, 0.30)
            pass1.append((v[0] + e1[0] * f1, v[1] + e1[1] * f1))
            pass1.append((v[0] + e2[0] * f2, v[1] + e2[1] * f2))
        else:
            pass1.append(v)

    # 4. Pass 2: soften any remaining crisp corners sharper than 132 deg (leaves obtuse 132-180 deg facets intact so
    #    stones still have flat/gently curved natural sides rather than becoming uniform circles)
    out = []
    n2 = len(pass1)
    cos132 = math.cos(math.radians(132.0))
    for i in range(n2):
        pv, v, nx = pass1[i - 1], pass1[i], pass1[(i + 1) % n2]
        e1, e2 = (pv[0] - v[0], pv[1] - v[1]), (nx[0] - v[0], nx[1] - v[1])
        l1, l2 = math.hypot(*e1), math.hypot(*e2)
        cosang = (e1[0] * e2[0] + e1[1] * e2[1]) / max(l1 * l2, 1e-12)
        if cosang > cos132 and l1 > 0.010 and l2 > 0.010:
            f1 = rng.uniform(0.18, 0.30)
            f2 = rng.uniform(0.18, 0.30)
            out.append((v[0] + e1[0] * f1, v[1] + e1[1] * f1))
            out.append((v[0] + e2[0] * f2, v[1] + e2[1] * f2))
        else:
            out.append(v)
    return out


def _clip(poly, a, b, c):
    """Keep the part of convex polygon `poly` with a*x + b*y <= c."""
    out = []
    n = len(poly)
    for i in range(n):
        p, q = poly[i], poly[(i + 1) % n]
        dp, dq = a * p[0] + b * p[1] - c, a * q[0] + b * q[1] - c
        if dp <= 0:
            out.append(p)
        if (dp < 0 < dq) or (dq < 0 < dp):
            t = dp / (dp - dq)
            out.append((p[0] + t * (q[0] - p[0]), p[1] + t * (q[1] - p[1])))
    return out


def _area(poly):
    x = np.array([p[0] for p in poly])
    y = np.array([p[1] for p in poly])
    return 0.5 * float(np.dot(x, np.roll(y, -1)) - np.dot(y, np.roll(x, -1)))


def _centroid(poly):
    x = np.array([p[0] for p in poly])
    y = np.array([p[1] for p in poly])
    a = _area(poly)
    if abs(a) < 1e-12:
        return float(x.mean()), float(y.mean())
    cr = x * np.roll(y, -1) - np.roll(x, -1) * y
    return float(np.dot(x + np.roll(x, -1), cr) / (6 * a)), float(np.dot(y + np.roll(y, -1), cr) / (6 * a))


def inset(poly, d):
    """Move every edge of a convex polygon inwards by d (counter-clockwise polygon)."""
    out = list(poly)
    n = len(poly)
    for i in range(n):
        p, q = poly[i], poly[(i + 1) % n]
        ex, ey = q[0] - p[0], q[1] - p[1]
        L = math.hypot(ex, ey)
        if L < 1e-9:
            continue
        nx, ny = -ey / L, ex / L      # inward normal of a CCW polygon
        # keep n.x >= n.p + d  ->  (-n).x <= -(n.p + d)
        out = _clip(out, -nx, -ny, -(nx * p[0] + ny * p[1] + d))
        if len(out) < 3:
            return []
    return out


def simplify(poly, max_n=7, min_edge=0.015):
    """Merge vertices closer than min_edge and drop the shortest edges until at most max_n sides remain."""
    pts = list(poly)
    changed = True
    while changed and len(pts) > 3:
        changed = False
        n = len(pts)
        lens = [math.hypot(pts[(i + 1) % n][0] - pts[i][0], pts[(i + 1) % n][1] - pts[i][1]) for i in range(n)]
        i = int(np.argmin(lens))
        if lens[i] < min_edge or n > max_n:
            j = (i + 1) % n
            pts[i] = ((pts[i][0] + pts[j][0]) / 2, (pts[i][1] + pts[j][1]) / 2)
            del pts[j]
            changed = True
    return pts


class Path:
    """The path centreline in frame metres (stations), with a seeded smooth width envelope."""

    def __init__(self, cs, cu, cv, width, seed, wobble=0.15):
        self.cs, self.cu, self.cv = np.asarray(cs, float), np.asarray(cu, float), np.asarray(cv, float)
        self.width = width
        rng = np.random.default_rng(seed + 7)
        # width = width * (1 +/- wobble): sum of a few random sines over the length (a smooth, never-repeating ragged edge)
        k = rng.uniform(0.25, 1.1, 4)
        ph = rng.uniform(0, 2 * math.pi, 4)
        amp = rng.uniform(0.5, 1.0, 4)
        amp = amp / amp.sum() * wobble
        self.env = lambda s, side: width * 0.5 * (1 + sum(a * math.sin(kk * s + p + side * 1.7) for a, kk, p in zip(amp, k, ph)))
        # a coarse polyline for projection
        step = max(int(0.25 / max(float(np.median(np.diff(self.cs))), 1e-3)), 1)
        idx = list(range(0, len(self.cs), step))
        if idx[-1] != len(self.cs) - 1:
            idx.append(len(self.cs) - 1)
        self.ps = np.stack([self.cu[idx], self.cv[idx]], 1)
        self.ss = self.cs[idx]

    def project(self, pts):
        """(s, o) for world points (N,2): arc length of the nearest centreline point and signed offset (left = +)."""
        pts = np.atleast_2d(pts)
        a, b = self.ps[:-1], self.ps[1:]
        ab = b - a
        L2 = (ab ** 2).sum(1)
        out = np.empty((len(pts), 2))
        for i, p in enumerate(pts):
            ap = p - a
            t = np.clip((ap * ab).sum(1) / L2, 0, 1)
            q = a + ab * t[:, None]
            d = p - q
            dist = np.hypot(d[:, 0], d[:, 1])
            j = int(np.argmin(dist))
            tl = (ap[j] * ab[j]).sum() / L2[j]
            s = self.ss[j] + t[j] * (self.ss[j + 1] - self.ss[j])
            side = np.sign(ab[j, 0] * d[j, 1] - ab[j, 1] * d[j, 0]) or 1.0
            beyond = max(-tl, tl - 1.0, 0.0) * math.sqrt(L2[j])   # distance past an end of the centreline
            out[i] = (s, side * dist[j]) if beyond < 1e-6 else (s + math.copysign(beyond, 1) * (1 if tl > 1 else -1), side * dist[j])
        return out

    def point(self, s, o):
        """Frame point at arc length s, offset o."""
        i = int(np.clip(np.searchsorted(self.cs, s), 1, len(self.cs) - 1))
        t = (s - self.cs[i - 1]) / max(self.cs[i] - self.cs[i - 1], 1e-9)
        p = np.array([self.cu[i - 1] + t * (self.cu[i] - self.cu[i - 1]), self.cv[i - 1] + t * (self.cv[i] - self.cv[i - 1])])
        tg = np.array([self.cu[i] - self.cu[i - 1], self.cv[i] - self.cv[i - 1]])
        tg /= max(np.hypot(*tg), 1e-9)
        return p + np.array([-tg[1], tg[0]]) * o


def pack(path, cfg, seed, s0, s1, hairpin=None):
    rng = np.random.default_rng(seed)
    classes = cfg.get("classes", [  # (radius m, share of the area) — exact original size classes
        (0.19, 0.14), (0.125, 0.36), (0.08, 0.32), (0.055, 0.18)])
    W = path.width
    margin = 0.28
    area = (s1 - s0) * (W + 2 * margin)
    cell = 0.25
    grid = {}
    sites = []   # (x, y, r)

    def free(x, y, r):
        cx, cy = int(math.floor(x / cell)), int(math.floor(y / cell))
        for gx in range(cx - 2, cx + 3):
            for gy in range(cy - 2, cy + 3):
                for k in grid.get((gx, gy), ()):
                    sx, sy, sr = sites[k]
                    if (sx - x) ** 2 + (sy - y) ** 2 < (0.66 * (r + sr)) ** 2:
                        return False
        return True

    def add(x, y, r):
        sites.append((x, y, r))
        grid.setdefault((int(math.floor(x / cell)), int(math.floor(y / cell))), []).append(len(sites) - 1)

    def candidates(n):
        ss = rng.uniform(s0 - 0.1, s1 + 0.1, n)
        oo = rng.uniform(-W / 2 - margin, W / 2 + margin, n)
        return [(s, o, *path.point(s, o)) for s, o in zip(ss, oo)]

    for r0, share in classes:
        n_target = share * area / (math.pi * r0 * r0 * 0.6)
        for s, o, x, y in candidates(int(n_target * 14)):
            r = r0 * rng.uniform(0.5, 1.5)
            if free(x, y, r):
                add(x, y, r)
    # tiny wedge fillers in whatever holes remain
    for s, o, x, y in candidates(int(area * 700)):
        r = rng.uniform(0.032, 0.05)
        if free(x, y, r * 0.8):
            add(x, y, r)

    # power diagram cells (random polygon capped around each site, clipped by every near neighbour's bisector)
    pos = np.array([(x, y) for x, y, _ in sites])
    rad = np.array([r for _, _, r in sites])
    wgt = (rad * np.random.default_rng(seed + 5).uniform(0.25, 1.8, len(rad))) ** 2

    def cells(pos):
        g2 = {}
        for k, (x, y) in enumerate(pos):
            g2.setdefault((int(x // 0.5), int(y // 0.5)), []).append(k)
        res = []
        for k in range(len(pos)):
            px, py = pos[k]
            ang0 = rng_cells.uniform(0, 2 * math.pi)
            nside = rng_cells.integers(5, 8)
            ring = []
            for q in range(nside):
                a = ang0 + 2 * math.pi * (q + rng_cells.uniform(-0.2, 0.2)) / nside
                rr = rad[k] * rng_cells.uniform(2.6, 3.4)
                ring.append((px + rr * math.cos(a), py + rr * math.sin(a)))
            poly = ring
            cx, cy = int(px // 0.5), int(py // 0.5)
            for gx in range(cx - 2, cx + 3):
                for gy in range(cy - 2, cy + 3):
                    for j in g2.get((gx, gy), ()):
                        if j == k:
                            continue
                        dx, dy = pos[j, 0] - px, pos[j, 1] - py
                        if dx * dx + dy * dy > (2.4 * (rad[k] + rad[j])) ** 2:
                            continue
                        # |x-pk|^2 - wk <= |x-pj|^2 - wj
                        c = (pos[j, 0] ** 2 + pos[j, 1] ** 2 - px * px - py * py - wgt[j] + wgt[k]) / 2
                        poly = _clip(poly, dx, dy, c)
                        if len(poly) < 3:
                            break
                    if len(poly) < 3:
                        break
                if len(poly) < 3:
                    break
            res.append(poly if len(poly) >= 3 else None)
        return res

    rng_cells = np.random.default_rng(seed + 11)
    cl = cells(pos)

    stones = []
    rng_s = np.random.default_rng(seed + 23)
    rng_r = np.random.default_rng(seed + 31)   # own stream for the outline roughening
    sop = path.project(pos)
    # large-scale tone drift along the path (a stone patch is a little warmer / darker than the next)
    tk = rng_s.uniform(0.5, 1.4, 3)
    tp = rng_s.uniform(0, 2 * math.pi, 3)
    for k, poly in enumerate(cl):
        if not poly:
            continue
        if _area(poly) < 0:
            poly = poly[::-1]
        s, o = sop[k]
        # ragged edge: the envelope plus a per-stone jitter; stones past it vanish, stones near it thin out
        env = path.env(s, 1 if o > 0 else -1)
        j = rng_s.normal(0, 0.05)
        edge = abs(o) - (env + j)
        if edge > 0 or s < s0 - 0.05 or s > s1 + 0.05:
            continue
        near_edge = edge > -0.12
        near_end = (s - s0) < 0.35 or (s1 - s) < 0.35
        if (near_edge or near_end) and rng_s.random() < 0.28:
            continue
        d = rng_s.uniform(0.004, 0.011)                       # half joint: neighbours' gap 1-3 cm
        if near_edge and rng_s.random() < 0.5:
            d += rng_s.uniform(0.01, 0.03)                    # edge stones are smaller / more loosely set
        p = inset(poly, d)
        if len(p) < 3:
            continue
        p = simplify(p, 7, 0.015)
        if len(p) < 4 or _area(p) < 0.0016:
            continue
        p = roughen(p, rng_r)
        if _area(p) < 0:
            p = p[::-1]
        r_eff = math.sqrt(max(abs(_area(p)), 1e-6) / math.pi)
        crown = float(np.clip(r_eff * rng_r.uniform(0.12, 0.20), 0.006, 0.020))
        # tone: random swatch, shifted by the slow drift
        drift = sum(math.sin(kk * s * 0.35 + ph) for kk, ph in zip(tk, tp)) / 3
        if rng_s.random() < 0.80:
            tone = int(np.clip(round(rng_s.uniform(0, N_LIME - 1) + drift * 3.0), 0, N_LIME - 1))
        else:
            tone = int(N_LIME + rng_s.choice(len(TONES) - N_LIME, p=TONE_W[N_LIME:] / TONE_W[N_LIME:].sum()))
        ang = rng_s.uniform(0, 2 * math.pi)
        tilt = math.tan(math.radians(rng_s.uniform(0.5, 2.0)))
        stones.append({"poly": p, "tone": tone, "proud": float(rng_s.uniform(0.004, 0.010)), "crown": crown,
                       "tilt": (tilt * math.cos(ang), tilt * math.sin(ang)), "s": float(s)})

    # loose stones outside the edge (a few, never in a row)
    n_loose = int((s1 - s0) / 1.4)
    for _ in range(n_loose):
        s = rng_s.uniform(s0, s1)
        side = rng_s.choice([-1, 1])
        o = side * (path.env(s, side) + rng_s.uniform(0.06, 0.32))
        x, y = path.point(s, o)
        r = rng_s.uniform(0.04, 0.085)
        n = int(rng_s.integers(5, 8))
        a0 = rng_s.uniform(0, 2 * math.pi)
        poly = [(x + r * rng_s.uniform(0.75, 1.15) * math.cos(a0 + 2 * math.pi * q / n),
                 y + r * rng_s.uniform(0.75, 1.15) * math.sin(a0 + 2 * math.pi * q / n)) for q in range(n)]
        if _area(poly) < 0:
            poly = poly[::-1]
        poly = roughen(poly, rng_r)
        if _area(poly) < 0:
            poly = poly[::-1]
        r_eff = math.sqrt(max(abs(_area(poly)), 1e-6) / math.pi)
        crown = float(np.clip(r_eff * rng_r.uniform(0.12, 0.20), 0.006, 0.018))
        ang = rng_s.uniform(0, 2 * math.pi)
        tilt = math.tan(math.radians(rng_s.uniform(1.0, 4.0)))
        stones.append({"poly": poly, "tone": int(rng_s.integers(0, N_LIME)), "proud": float(rng_s.uniform(0.008, 0.02)),
                       "crown": crown, "tilt": (tilt * math.cos(ang), tilt * math.sin(ang)), "s": float(s), "loose": True})
    return stones
