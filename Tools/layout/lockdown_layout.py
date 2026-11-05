"""Layout diagram for LOCKDOWN (Task 6): draws the condensed village plan on the real ground.

Run from the project root, after lockdown_dem.py and lockdown_satellite.py:
    uv run --with numpy --with pillow --with matplotlib Tools/layout/lockdown_layout.py

Reads Assets/Lockdown/Data/Layout/lockdown_layout.json (frame coordinates u along the main street, v uphill)
and writes review images to Screenshots/ (never Assets/):
    task06_layout_plan.png      plan over the aerial photo and 5 m contours
    task06_layout_clean.png     the same plan over contours and hillshade only
    task06_slope_profiles.png   real ground sections through the play area
    task06_layout_simple.png    minimal sheet: initial spawns, capture zones and place names only
    task06_layout_terrain.png   the minimal sheet over the real ground coloured by height, each place with its height vs Flag B
It also prints the metrics: sprint times along walkable space, sightline lengths and which ones face the sun.
"""
import heapq
import json
import math
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
LAYOUT = ROOT / "Assets" / "Lockdown" / "Data" / "Layout" / "lockdown_layout.json"
DEM = ROOT / "SourceData" / "dem"
SAT = ROOT / "Assets" / "Lockdown" / "Textures" / "Review" / "Satellite"
OUT = ROOT / "Screenshots"

SUN_AZIMUTH, SUN_ELEVATION = 263.9, 19.9     # review sun (Task 5)
GLARE_DEG, IN_VIEW_DEG = 30.0, 50.0          # horizontal angle to the sun: glare / sun in the field of view
PPM = 8                                       # pixels per metre on the plan
VIEW_U, VIEW_V = (-110.0, 110.0), (-92.0, 92.0)
PANEL_W = 820
CONTOUR_MINOR, CONTOUR_MAJOR = 5.0, 25.0

LABEL_COLORS = {"REAL": (70, 200, 90), "ADAPTED": (245, 180, 40), "INVENTED": (190, 110, 255)}
TEAM_WEST, TEAM_EAST = (60, 200, 255), (255, 90, 170)
SUN_COLOR, SUN_SOFT, LINE_COLOR = (255, 120, 20), (255, 210, 80), (235, 235, 235)


def font(size, bold=False):
    # Pillow looks font names up in the system font folders (Arial on Windows, DejaVu on most Linux/macOS setups).
    for name in (("arialbd.ttf", "DejaVuSans-Bold.ttf") if bold else ("arial.ttf", "DejaVuSans.ttf")):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            pass
    return ImageFont.load_default()


# ---------------------------------------------------------------- frame and ground

class Frame:
    def __init__(self, f):
        self.cx, self.cz = f["center"]
        self.rot = math.radians(f["rotation_deg"])
        self.size = f["size"]

    def world(self, u, v):
        c, s = math.cos(self.rot), math.sin(self.rot)
        return self.cx + u * c - v * s, self.cz + u * s + v * c

    def world_dir(self, du, dv):
        c, s = math.cos(self.rot), math.sin(self.rot)
        return du * c - dv * s, du * s + dv * c

    def frame_dir(self, dx, dz):
        c, s = math.cos(self.rot), math.sin(self.rot)
        return dx * c + dz * s, -dx * s + dz * c


def load_heights():
    meta = json.loads((DEM / "dem_meta.json").read_text())
    res, size = meta["village"]["resolution"], meta["village"]["size"]
    raw = np.fromfile(DEM / meta["village"]["file"], dtype="<u2").reshape(res, res).astype(np.float32)
    heights = meta["base_height"] + raw / 65535.0 * (meta["top_height"] - meta["base_height"])  # rows south to north

    def sample(x, z):
        fx = np.clip((np.asarray(x) + size / 2) / size * (res - 1), 0, res - 1.001)
        fz = np.clip((np.asarray(z) + size / 2) / size * (res - 1), 0, res - 1.001)
        ix, iz = np.floor(fx).astype(int), np.floor(fz).astype(int)
        tx, tz = fx - ix, fz - iz
        return ((heights[iz, ix] * (1 - tx) + heights[iz, ix + 1] * tx) * (1 - tz)
                + (heights[iz + 1, ix] * (1 - tx) + heights[iz + 1, ix + 1] * tx) * tz)
    return sample


def load_satellite():
    sat_meta = json.loads((SAT / "satellite_meta.json").read_text())
    tile = sat_meta["tile_px"]
    m = np.zeros((2 * tile, 2 * tile, 3), np.uint8)  # rows north to south
    for col in range(2):
        for row in range(2):  # row 0 = south
            y0 = (1 - row) * tile
            m[y0:y0 + tile, col * tile:(col + 1) * tile] = np.asarray(Image.open(SAT / f"sat_{col}_{row}.jpg").convert("RGB"))
    return m, sat_meta["size"], sat_meta["m_per_px"]


# ---------------------------------------------------------------- plan raster helpers

W = int((VIEW_U[1] - VIEW_U[0]) * PPM)
H = int((VIEW_V[1] - VIEW_V[0]) * PPM)


def px(u, v):
    return (u - VIEW_U[0]) * PPM, (VIEW_V[1] - v) * PPM


def base_layers(frame, height_at):
    us = VIEW_U[0] + (np.arange(W) + 0.5) / PPM
    vs = VIEW_V[1] - (np.arange(H) + 0.5) / PPM
    uu, vv = np.meshgrid(us, vs)
    c, s = math.cos(frame.rot), math.sin(frame.rot)
    xx = frame.cx + uu * c - vv * s
    zz = frame.cz + uu * s + vv * c
    h = height_at(xx, zz)

    sat, sat_size, mpp = load_satellite()
    col = np.clip(((xx + sat_size / 2) / mpp).astype(int), 0, sat.shape[1] - 1)
    row = np.clip(((sat_size / 2 - zz) / mpp).astype(int), 0, sat.shape[0] - 1)
    photo = sat[row, col].astype(np.float32)

    # Hillshade lit from the review sun, for the clean sheet.
    gy, gx = np.gradient(h, 1.0 / PPM)  # gy along rows (v decreasing), gx along u
    dx_w = gx * c - (-gy) * s           # slope in world x
    dz_w = gx * s + (-gy) * c           # slope in world z
    az, el = math.radians(SUN_AZIMUTH), math.radians(SUN_ELEVATION)
    lx, lz, ly = math.sin(az) * math.cos(el), math.cos(az) * math.cos(el), math.sin(el)
    n = np.sqrt(dx_w ** 2 + dz_w ** 2 + 1)
    shade = np.clip((-dx_w * lx - dz_w * lz + ly) / n, 0, 1)
    return h, photo, shade


def contour_masks(h):
    def edges(step):
        b = np.floor(h / step)
        e = np.zeros(h.shape, bool)
        e[:-1, :] |= b[:-1, :] != b[1:, :]
        e[:, :-1] |= b[:, :-1] != b[:, 1:]
        return e
    return edges(CONTOUR_MINOR), edges(CONTOUR_MAJOR)


# ---------------------------------------------------------------- walkable grid and sprint times

def seg_dist(pu, pv, a, b):
    au, av = a
    bu, bv = b
    du, dv = bu - au, bv - av
    L2 = du * du + dv * dv
    t = np.clip(((pu - au) * du + (pv - av) * dv) / L2, 0, 1) if L2 > 0 else 0
    return np.hypot(pu - (au + t * du), pv - (av + t * dv))


def walk_mask(layout, cell=1.0):
    fu, fv = layout["frame"]["size"][0] / 2, layout["frame"]["size"][1] / 2
    us = np.arange(-fu, fu + 1e-6, cell)
    vs = np.arange(-fv, fv + 1e-6, cell)
    uu, vv = np.meshgrid(us, vs)
    m = np.zeros(uu.shape, bool)

    def poly(points, width):
        nonlocal m
        for a, b in zip(points[:-1], points[1:]):
            m |= seg_dist(uu, vv, a, b) <= width / 2

    for lane in layout["lanes"]:
        poly(lane["points"], lane["width"])
    for con in layout["connectors"]:
        poly(con["points"], con["width"])
    poly(layout["ravine"]["points"], layout["ravine"]["walkable_floor"])
    for ar in layout["areas"]:
        cu, cv = ar["center"]
        if ar["shape"] == "circle":
            m |= np.hypot(uu - cu, vv - cv) <= ar["radius"]
        else:
            m |= (np.abs(uu - cu) <= ar["size"][0] / 2) & (np.abs(vv - cv) <= ar["size"][1] / 2)
    return m, us, vs


def dijkstra(mask, start):
    hgt, wid = mask.shape
    dist = np.full(mask.shape, np.inf)
    dist[start] = 0.0
    pq = [(0.0, start)]
    steps = [(-1, 0, 1), (1, 0, 1), (0, -1, 1), (0, 1, 1),
             (-1, -1, math.sqrt(2)), (-1, 1, math.sqrt(2)), (1, -1, math.sqrt(2)), (1, 1, math.sqrt(2))]
    while pq:
        d, (r, c) = heapq.heappop(pq)
        if d > dist[r, c]:
            continue
        for dr, dc, w in steps:
            rr, cc = r + dr, c + dc
            if 0 <= rr < hgt and 0 <= cc < wid and mask[rr, cc] and d + w < dist[rr, cc]:
                dist[rr, cc] = d + w
                heapq.heappush(pq, (d + w, (rr, cc)))
    return dist


def sprint_times(layout):
    mask, us, vs = walk_mask(layout)

    def cell(p):
        return int(round(p[1] - vs[0])), int(round(p[0] - us[0]))

    sprint = layout["metrics"]["sprint_mps"]
    pts = {s["id"]: s["at"] for s in layout["spawns"]}
    pts.update({f["id"]: f["at"] for f in layout["flags"]})
    results = {}
    for sid in ("Spawn_West", "Spawn_East"):
        d = dijkstra(mask, cell(pts[sid]))
        for k, p in pts.items():
            if k != sid:
                # Grid paths overestimate straight runs slightly; 1 m cells keep it within ~5 %.
                results[(sid, k)] = d[cell(p)] / sprint
    return results


# ---------------------------------------------------------------- sun facing

def bearing(dx, dz):
    return math.degrees(math.atan2(dx, dz)) % 360.0


def sun_angle(frame, a, b):
    """Horizontal angle between the view a -> b and the sun azimuth."""
    dx, dz = frame.world_dir(b[0] - a[0], b[1] - a[1])
    diff = abs((bearing(dx, dz) - SUN_AZIMUTH + 180) % 360 - 180)
    return diff, bearing(dx, dz)


# ---------------------------------------------------------------- drawing

def dashed(draw, p0, p1, fill, width, dash=12, gap=8):
    x0, y0 = p0
    x1, y1 = p1
    L = math.hypot(x1 - x0, y1 - y0)
    if L == 0:
        return
    ux, uy = (x1 - x0) / L, (y1 - y0) / L
    t = 0.0
    while t < L:
        e = min(t + dash, L)
        draw.line([(x0 + ux * t, y0 + uy * t), (x0 + ux * e, y0 + uy * e)], fill=fill, width=width)
        t = e + gap


def text_outlined(draw, xy, text, fnt, fill, outline=(0, 0, 0), anchor="la", stroke=3):
    draw.text(xy, text, font=fnt, fill=fill, anchor=anchor, stroke_width=stroke, stroke_fill=outline)


def arrow(draw, p0, p1, fill, width, head=16):
    draw.line([p0, p1], fill=fill, width=width)
    ang = math.atan2(p1[1] - p0[1], p1[0] - p0[0])
    for da in (2.6, -2.6):
        draw.line([p1, (p1[0] + head * math.cos(ang + da), p1[1] + head * math.sin(ang + da))], fill=fill, width=width)


def draw_plan(layout, frame, base_rgb, minor, major, h, sight_info, landmark_index):
    img = base_rgb.copy()
    img[minor] = (0.5 * img[minor] + 0.5 * np.array([255, 255, 255])).astype(np.uint8)
    img[major] = np.array([255, 255, 255], np.uint8)
    im = Image.fromarray(img).convert("RGBA")
    over = Image.new("RGBA", im.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(over)

    # Outside the play area: darken.
    fu, fv = frame.size[0] / 2, frame.size[1] / 2
    shade = Image.new("RGBA", im.size, (0, 0, 0, 120))
    hole = ImageDraw.Draw(shade)
    hole.rectangle([px(-fu, fv), px(fu, -fv)], fill=(0, 0, 0, 0))
    im = Image.alpha_composite(im, shade)

    # Ravine.
    rv = layout["ravine"]
    rpts = [px(*p) for p in rv["points"]]
    d.line(rpts, fill=(20, 90, 60, 200), width=int(rv["width"] * PPM), joint="curve")
    d.line(rpts, fill=(110, 200, 150, 230), width=int(rv["walkable_floor"] * PPM), joint="curve")

    # Areas.
    for ar in layout["areas"]:
        cu, cv = ar["center"]
        fill = (255, 255, 255, 50) if not ar.get("roof") else (255, 230, 120, 70)
        edge = (255, 255, 255, 200) if not ar.get("roof") else (255, 220, 90, 230)
        if ar["shape"] == "circle":
            r = ar["radius"]
            d.ellipse([px(cu - r, cv + r), px(cu + r, cv - r)], fill=fill, outline=edge, width=3)
        else:
            sw, sh = ar["size"][0] / 2, ar["size"][1] / 2
            d.rectangle([px(cu - sw, cv + sh), px(cu + sw, cv - sh)], fill=fill, outline=edge, width=3)

    # Lanes.
    for lane in layout["lanes"]:
        pts = [px(*p) for p in lane["points"]]
        d.line(pts, fill=(30, 30, 30, 230), width=int(lane["width"] * PPM) + 6, joint="curve")
        d.line(pts, fill=(205, 205, 200, 235), width=int(lane["width"] * PPM), joint="curve")
    # Connectors.
    for con in layout["connectors"]:
        pts = [px(*p) for p in con["points"]]
        col = (255, 220, 90, 255) if con.get("roof") else (255, 255, 255, 255)
        for a, b in zip(pts[:-1], pts[1:]):
            dashed(d, a, b, (20, 20, 20, 255), 9)
            dashed(d, a, b, col, 5)
    # Crossings.
    for cr in rv["crossings"]:
        cu, cv = cr["at"]
        d.rectangle([px(cu - 6, cv + 3), px(cu + 6, cv - 3)], outline=(255, 255, 255, 255), width=4)

    # Frame.
    d.rectangle([px(-fu, fv), px(fu, -fv)], outline=(255, 255, 255, 255), width=4)

    # Sightlines.
    f_small = font(19, True)
    for sl, info in zip(layout["sightlines"], sight_info):
        a, b = px(*sl["a"]), px(*sl["b"])
        worst = min(info["angle_ab"], info["angle_ba"])
        col = SUN_COLOR if worst <= GLARE_DEG else SUN_SOFT if worst <= IN_VIEW_DEG else (120, 230, 255)
        dashed(d, a, b, (0, 0, 0, 255), 7, 16, 8)
        dashed(d, a, b, col + (255,), 4, 16, 8)
        for p in (a, b):
            d.ellipse([p[0] - 5, p[1] - 5, p[0] + 5, p[1] + 5], fill=col + (255,))
        # Mark the end that looks into the sun (its viewer faces west).
        if worst <= IN_VIEW_DEG:
            viewer = a if info["angle_ab"] <= info["angle_ba"] else b
            sun_glyph(d, viewer, col)
        mid = (a[0] + (b[0] - a[0]) * 0.3, a[1] + (b[1] - a[1]) * 0.3)
        text_outlined(d, (mid[0] + 8, mid[1] - 22), f"{info['length']:.0f} m", f_small, col + (255,))

    im = Image.alpha_composite(im, over)
    d = ImageDraw.Draw(im)

    # Cordon.
    f_lab = font(20, True)
    for cp in layout["cordon"]:
        x, y = px(*cp["at"])
        d.rectangle([x - 14, y - 9, x + 14, y + 9], fill=(20, 60, 200), outline=(255, 255, 255), width=2)
        d.rectangle([x - 14, y - 9, x, y - 3], fill=(255, 40, 40))
        d.rectangle([x, y - 9, x + 14, y - 3], fill=(60, 140, 255))

    # Landmarks (numbered dots).
    f_num = font(18, True)
    for i, lm in landmark_index:
        u, v = lm["at"]
        u = min(max(u, VIEW_U[0] + 4), VIEW_U[1] - 4)
        x, y = px(u, v)
        col = LABEL_COLORS[lm["label"]]
        d.ellipse([x - 15, y - 15, x + 15, y + 15], fill=col, outline=(0, 0, 0), width=3)
        d.text((x, y), str(i), font=f_num, fill=(0, 0, 0), anchor="mm")
        if lm.get("outside"):
            arrow(d, (x - 60, y), (x - 20, y), (255, 255, 255), 4)
            text_outlined(d, (x - 20, y + 22), "outside", f_lab, (255, 255, 255), anchor="ra")

    # Spawns and flags.
    f_big, f_call = font(40, True), font(24, True)
    for sp in layout["spawns"]:
        x, y = px(*sp["at"])
        col = TEAM_WEST if "West" in sp["id"] else TEAM_EAST
        d.polygon([(x, y - 30), (x + 28, y + 20), (x - 28, y + 20)], fill=col, outline=(0, 0, 0), width=4)
        d.text((x, y + 4), "S", font=font(26, True), fill=(0, 0, 0), anchor="mm")
        text_outlined(d, (x, y + 30), sp["callout"], f_call, col, anchor="ma")
    for fl in layout["flags"]:
        x, y = px(*fl["at"])
        letter = fl["id"][-1]
        d.ellipse([x - 30, y - 30, x + 30, y + 30], fill=(255, 255, 255), outline=(0, 0, 0), width=5)
        d.text((x, y + 1), letter, font=f_big, fill=(0, 0, 0), anchor="mm")
        text_outlined(d, (x + 30, y - 58), fl["callout"], f_call, (255, 255, 255))

    # Lane names.
    f_lane = font(24, True)
    for lane in layout["lanes"]:
        # Outside the frame on the west, level with where the lane enters.
        v0 = lane["points"][0][1]
        for line_no, part in enumerate(lane["name"].split(" - ")):
            text_outlined(d, px(-108, v0 + 9 - line_no * 3.6), part, f_lane if line_no == 0 else f_small, (255, 255, 255))
    text_outlined(d, px(-14, 56), "RAVINE", f_lane, (150, 240, 190))

    # Compass, sun, scale.
    north_and_sun(d, frame, (W - 120, 120))
    x0, y0 = 40, H - 50
    d.rectangle([x0, y0, x0 + 50 * PPM, y0 + 12], fill=(255, 255, 255), outline=(0, 0, 0), width=2)
    d.rectangle([x0, y0, x0 + 25 * PPM, y0 + 12], fill=(0, 0, 0))
    text_outlined(d, (x0, y0 - 30), "0", f_lab, (255, 255, 255))
    text_outlined(d, (x0 + 25 * PPM, y0 - 30), "25", f_lab, (255, 255, 255), anchor="ma")
    text_outlined(d, (x0 + 50 * PPM, y0 - 30), "50 m", f_lab, (255, 255, 255), anchor="ma")
    text_outlined(d, (x0 + 50 * PPM + 70, y0 - 6), "Play area 150 x 120 m  ·  contours 5 m (bold 25 m, real ground)", f_lab, (255, 255, 255))
    return im


def sun_glyph(d, p, col):
    x, y = p
    for k in range(8):
        a = k * math.pi / 4
        d.line([(x + 14 * math.cos(a), y + 14 * math.sin(a)), (x + 22 * math.cos(a), y + 22 * math.sin(a))], fill=col + (255,), width=3)
    d.ellipse([x - 10, y - 10, x + 10, y + 10], fill=col + (255,), outline=(0, 0, 0, 255), width=2)


def north_and_sun(d, frame, c):
    cx, cy = c
    d.ellipse([cx - 85, cy - 85, cx + 85, cy + 85], fill=(25, 25, 25), outline=(255, 255, 255), width=3)

    def to_screen(dx, dz, r):
        du, dv = frame.frame_dir(dx, dz)
        L = math.hypot(du, dv)
        return cx + du / L * r, cy - dv / L * r

    n = to_screen(0, 1, 70)
    arrow(d, (cx, cy), n, (255, 255, 255), 5)
    text_outlined(d, (n[0] + 4, n[1] - 30), "N", font(26, True), (255, 255, 255))
    az = math.radians(SUN_AZIMUTH)
    s = to_screen(math.sin(az), math.cos(az), 70)
    arrow(d, (cx, cy), s, SUN_COLOR, 5)
    sun_glyph(d, s, SUN_COLOR)
    text_outlined(d, (cx, cy + 92), f"sun {SUN_AZIMUTH:.0f}° / {SUN_ELEVATION:.0f}° up", font(20, True), SUN_COLOR, anchor="ma")


def draw_panel(layout, frame, times, sight_info, landmark_index, lane_stats):
    p = Image.new("RGB", (PANEL_W, H), (24, 26, 30))
    d = ImageDraw.Draw(p)
    f_h, f_b, f_t = font(30, True), font(19, True), font(18)
    y = 24
    d.text((24, y), "LOCKDOWN — layout v1 (Task 6)", font=f_h, fill=(255, 255, 255))
    y += 46
    for lab, col in LABEL_COLORS.items():
        d.ellipse([24, y, 44, y + 20], fill=col)
        d.text((52, y), lab, font=f_b, fill=col)
        d.text((52 + 120, y), {"REAL": "exists there", "ADAPTED": "real, moved or changed", "INVENTED": "added for play"}[lab], font=f_t, fill=(200, 200, 200))
        y += 26
    y += 6
    keys = [((120, 230, 255), "sightline"), (SUN_SOFT, f"sightline, sun in view (< {IN_VIEW_DEG:.0f}°)"),
            (SUN_COLOR, f"sightline into the sun (< {GLARE_DEG:.0f}°)")]
    for col, txt in keys:
        d.line([(24, y + 10), (60, y + 10)], fill=col, width=5)
        d.text((70, y), txt, font=f_t, fill=(220, 220, 220))
        y += 24
    d.line([(24, y + 10), (60, y + 10)], fill=(255, 255, 255), width=4)
    d.text((70, y), "lane connector (dashed; yellow = rooftop)    ▭ police roadblock", font=f_t, fill=(220, 220, 220))
    y += 36

    d.text((24, y), "Landmarks", font=f_b, fill=(255, 255, 255))
    y += 28
    for i, lm in landmark_index:
        col = LABEL_COLORS[lm["label"]]
        d.ellipse([24, y, 46, y + 22], fill=col)
        d.text((35, y + 11), str(i), font=font(14, True), fill=(0, 0, 0), anchor="mm")
        d.text((56, y + 1), lm["name"], font=f_t, fill=(235, 235, 235))
        d.text((330, y + 1), lm["label"], font=f_t, fill=col)
        y += 25
    y += 12

    d.text((24, y), "Sprint times (6.5 m/s, along walkable space)", font=f_b, fill=(255, 255, 255))
    y += 28
    rows = [("Spawn → Flag B", times[("Spawn_West", "Flag_B")], times[("Spawn_East", "Flag_B")], "10–15 s"),
            ("Spawn → own flag", times[("Spawn_West", "Flag_A")], times[("Spawn_East", "Flag_C")], ""),
            ("Spawn → far flag", times[("Spawn_West", "Flag_C")], times[("Spawn_East", "Flag_A")], ""),
            ("Spawn → spawn", times[("Spawn_West", "Spawn_East")], times[("Spawn_East", "Spawn_West")], "25–35 s")]
    d.text((260, y), "West", font=f_b, fill=TEAM_WEST)
    d.text((360, y), "East", font=f_b, fill=TEAM_EAST)
    d.text((460, y), "target", font=f_b, fill=(180, 180, 180))
    y += 24
    for name, a, b, tgt in rows:
        d.text((24, y), name, font=f_t, fill=(230, 230, 230))
        d.text((260, y), f"{a:.1f} s", font=f_t, fill=(230, 230, 230))
        d.text((360, y), f"{b:.1f} s", font=f_t, fill=(230, 230, 230))
        d.text((460, y), tgt, font=f_t, fill=(180, 180, 180))
        y += 23
    wb, eb = times[("Spawn_West", "Flag_B")], times[("Spawn_East", "Flag_B")]
    d.text((24, y), f"Flag B balance: {abs(wb - eb) / max(wb, eb) * 100:.0f} % (target ≤ 10 %)", font=f_t, fill=(230, 230, 230))
    y += 34

    d.text((24, y), "Sightlines", font=f_b, fill=(255, 255, 255))
    y += 26
    for sl, info in zip(layout["sightlines"], sight_info):
        worst = min(info["angle_ab"], info["angle_ba"])
        col = SUN_COLOR if worst <= GLARE_DEG else SUN_SOFT if worst <= IN_VIEW_DEG else (200, 200, 200)
        sun_txt = f"  sun {worst:.0f}° off" if worst <= IN_VIEW_DEG else ""
        d.text((24, y), f"{sl['name']}", font=f_t, fill=col)
        d.text((330, y), f"{info['length']:.0f} m{sun_txt}", font=f_t, fill=col)
        y += 22
    y += 12
    d.text((24, y), "Real ground along each lane (before terracing)", font=f_b, fill=(255, 255, 255))
    y += 26
    for name, lo, hi, grad in lane_stats:
        d.text((24, y), f"{name}: {lo:.0f}–{hi:.0f} m a.s.l., steepest {grad:.0f} %", font=f_t, fill=(220, 220, 220))
        y += 22
    return p


# Height colours relative to Flag B: below = cool green, level = sand, above = warm brown.
HEIGHT_STOPS = [(-16, (70, 140, 150)), (-8, (130, 185, 150)), (0, (232, 222, 180)),
                (8, (225, 175, 110)), (16, (190, 125, 80)), (26, (140, 90, 70))]
HEIGHT_STEP = 2.0


def height_colors(rel):
    stepped = np.floor(rel / HEIGHT_STEP) * HEIGHT_STEP + HEIGHT_STEP / 2
    xs = [s[0] for s in HEIGHT_STOPS]
    return np.stack([np.interp(stepped, xs, [s[1][k] for s in HEIGHT_STOPS]) for k in range(3)], -1)


def rel_text(rel):
    return "±0 m" if round(rel) == 0 else f"{round(rel):+d} m"


def draw_simple(layout, frame, shade, terrain=None):
    """Minimal sheet: lanes, ravine, initial spawns, capture zones, place names where they are.
    terrain = (height grid, height_at, reference height): colour the ground by height and add each place's height."""
    if terrain is None:
        base = (np.clip(shade, 0, 1)[..., None] * np.array([40, 38, 34]) + np.array([200, 196, 188])).astype(np.uint8)
    else:
        h, height_at, ref = terrain
        col = height_colors(h - ref) * (0.82 + 0.18 * np.clip(shade, 0, 1)[..., None])
        band = np.floor((h - ref) / HEIGHT_STEP)
        edge = np.zeros(h.shape, bool)
        edge[:-1, :] |= band[:-1, :] != band[1:, :]
        edge[:, :-1] |= band[:, :-1] != band[:, 1:]
        col[edge] *= 0.8
        base = np.clip(col, 0, 255).astype(np.uint8)
    im = Image.fromarray(base).convert("RGBA")
    d = ImageDraw.Draw(im)
    fu, fv = frame.size[0] / 2, frame.size[1] / 2

    # Lanes and connectors as plain walkable space.
    road = (250, 250, 247)
    for con in layout["connectors"]:
        d.line([px(*p) for p in con["points"]], fill=road if not con.get("roof") else (245, 225, 160), width=int(con["width"] * PPM), joint="curve")
    for lane in layout["lanes"]:
        d.line([px(*p) for p in lane["points"]], fill=road, width=int(lane["width"] * PPM), joint="curve")
    rv = layout["ravine"]
    d.line([px(*p) for p in rv["points"]], fill=(120, 175, 140), width=int(rv["width"] * PPM), joint="curve")
    for cr in rv["crossings"]:
        cu, cv = cr["at"]
        d.rectangle([px(cu - 6, cv + 3), px(cu + 6, cv - 3)], fill=road, outline=(90, 90, 90), width=2)

    # Spawn zones.
    for side, col in (("west", TEAM_WEST), ("east", TEAM_EAST)):
        pts = layout["initial_spawns"][side]
        us, vs = [p[0] for p in pts], [p[1] for p in pts]
        box = [px(min(us) - 4, max(vs) + 4), px(max(us) + 4, min(vs) - 4)]
        d.rounded_rectangle(box, radius=24, fill=col + (70,), outline=col + (255,), width=4)
        for u, v in pts:
            x, y = px(u, v)
            d.polygon([(x, y - 13), (x + 11, y + 9), (x - 11, y + 9)], fill=col, outline=(0, 0, 0), width=2)

    # Capture zones.
    r = layout["capture_radius"]
    for fl in layout["flags"]:
        cu, cv = fl["at"]
        d.ellipse([px(cu - r, cv + r), px(cu + r, cv - r)], fill=(255, 255, 255, 150), outline=(30, 30, 30), width=4)

    im = Image.alpha_composite(Image.fromarray(base).convert("RGBA"), im)
    d = ImageDraw.Draw(im)
    d.rectangle([px(-fu, fv), px(fu, -fv)], outline=(60, 60, 60), width=3)

    ink, halo = (25, 25, 25), (250, 250, 247)
    for fl in layout["flags"]:
        x, y = px(*fl["at"])
        d.text((x, y), fl["id"][-1], font=font(44, True), fill=ink, anchor="mm")
        name = fl["callout"].upper()
        if terrain is not None:
            name += "  " + rel_text(float(height_at(*frame.world(*fl["at"]))) - terrain[2])
        text_outlined(d, (x, y + r * PPM + 6), name, font(22, True), ink, outline=halo, anchor="ma")
    for sp, side, col in ((layout["spawns"][0], "west", TEAM_WEST), (layout["spawns"][1], "east", TEAM_EAST)):
        pts = layout["initial_spawns"][side]
        x, y = px(sum(p[0] for p in pts) / len(pts), min(p[1] for p in pts) - 4)
        text_outlined(d, (x, y + 8), sp["callout"].upper(), font(22, True), ink, outline=halo, anchor="ma")
        sub = "West spawn" if side == "west" else "East spawn"
        if terrain is not None:
            sub += "  " + rel_text(float(height_at(*frame.world(*sp["at"]))) - terrain[2])
        text_outlined(d, (x, y + 36), sub, font(18, True) if terrain is not None else font(18), ink, outline=halo, anchor="ma")

    for c in layout["callouts"]:
        x, y = px(*c["at"])
        if c.get("kind") == "lane":
            text_outlined(d, (x, y), c["name"], font(24, True), (70, 70, 70), outline=halo, anchor="mm")
        else:
            text_outlined(d, (x, y), c["name"], font(20, True), ink, outline=halo, anchor="mm")
        if terrain is not None:
            rel = float(height_at(*frame.world(*c["at"]))) - terrain[2]
            text_outlined(d, (x, y + 20), rel_text(rel), font(17, True), (120, 30, 20) if rel > 0.5 else (20, 70, 90),
                          outline=halo, anchor="mm")
    # North and sun, scale.
    north_and_sun(d, frame, (px(fu, fv)[0] + 100, px(fu, fv)[1] + 110))
    x0, y0 = px(-fu, -fv)
    y0 += 30
    d.rectangle([x0, y0, x0 + 25 * PPM, y0 + 10], fill=(30, 30, 30))
    d.text((x0 + 25 * PPM + 10, y0 - 6), "25 m", font=font(20, True), fill=ink)
    d.text((x0 + 25 * PPM + 90, y0 - 6), "Triangles = initial spawns (6 per side)  ·  circles = capture zones A / B / C",
           font=font(20), fill=ink)
    if terrain is not None:
        # Height key: colour bar from the lowest to the highest band.
        kx, ky, kw = x0, y0 + 30, 26
        lo, hi = -16, 26
        for i, v in enumerate(range(lo, hi, int(HEIGHT_STEP))):
            c = tuple(int(k) for k in height_colors(np.array(float(v)))) 
            d.rectangle([kx + i * kw, ky, kx + (i + 1) * kw, ky + 18], fill=c)
            if v % 8 == 0:
                d.text((kx + i * kw, ky + 22), f"{v:+d}", font=font(16), fill=ink)
        d.text((kx + (hi - lo) // int(HEIGHT_STEP) * kw + 14, ky - 2),
               "m vs Flag B (real ground, 2 m bands; before Task 7 terracing)", font=font(18), fill=ink)
    # Crop to the play area with a margin.
    left, top = px(-fu - 10, fv + 10)
    right, bottom = px(fu + 32, -fv - (14 if terrain is None else 24))
    return im.crop((int(left), int(top), int(right), int(bottom))).convert("RGB")


def main():
    layout = json.loads(LAYOUT.read_text(encoding="utf-8"))
    frame = Frame(layout["frame"])
    height_at = load_heights()

    # Metrics.
    times = sprint_times(layout)
    sight_info = []
    for sl in layout["sightlines"]:
        a, b = sl["a"], sl["b"]
        ang_ab, brg_ab = sun_angle(frame, a, b)
        ang_ba, brg_ba = sun_angle(frame, b, a)
        sight_info.append({"length": math.hypot(b[0] - a[0], b[1] - a[1]),
                           "angle_ab": ang_ab, "angle_ba": ang_ba, "bearing_ab": brg_ab, "bearing_ba": brg_ba})
    lane_stats = []
    for lane in layout["lanes"]:
        pts = lane["points"]
        # Sample every 2 m along the lane; steepest grade over 20 m stretches (the DEM is 30 m, finer is noise).
        samples = []
        for a, b in zip(pts[:-1], pts[1:]):
            n = max(int(math.hypot(b[0] - a[0], b[1] - a[1]) / 2), 1)
            samples += [(a[0] + (b[0] - a[0]) * t / n, a[1] + (b[1] - a[1]) * t / n) for t in range(n)]
        samples.append(tuple(pts[-1]))
        hs = [float(height_at(*frame.world(*p))) for p in samples]
        grads = [abs(hs[i + 10] - hs[i]) / 20.0 * 100 for i in range(len(hs) - 10)]
        lane_stats.append((lane["name"].split(" - ")[0], min(hs), max(hs), max(grads)))

    landmark_index = list(enumerate(layout["landmarks"], start=1))

    # Raster layers.
    h, photo, shade = base_layers(frame, height_at)
    minor, major = contour_masks(h)
    sat_rgb = np.clip(photo * 0.62 + 18, 0, 255).astype(np.uint8)
    gray = (shade[..., None] * np.array([150, 140, 125]) + np.array([60, 58, 55])).astype(np.uint8)

    OUT.mkdir(exist_ok=True)
    panel = draw_panel(layout, frame, times, sight_info, landmark_index, lane_stats)
    for name, base in (("task06_layout_plan.png", sat_rgb), ("task06_layout_clean.png", gray)):
        plan = draw_plan(layout, frame, base, minor, major, h, sight_info, landmark_index).convert("RGB")
        sheet = Image.new("RGB", (W + PANEL_W, H), (0, 0, 0))
        sheet.paste(plan, (0, 0))
        sheet.paste(panel, (W, 0))
        sheet.save(OUT / name)
        print("wrote", OUT / name)

    draw_simple(layout, frame, shade).save(OUT / "task06_layout_simple.png")
    b_at = next(f["at"] for f in layout["flags"] if f["id"] == "Flag_B")
    ref = float(height_at(*frame.world(*b_at)))
    draw_simple(layout, frame, shade, (h, height_at, ref)).save(OUT / "task06_layout_terrain.png")
    print("wrote", OUT / "task06_layout_terrain.png")
    print("wrote", OUT / "task06_layout_simple.png")
    profiles(layout, frame, height_at)

    # Console summary.
    print("\nSprint times (s):")
    for (a, b), t in sorted(times.items()):
        print(f"  {a:>10} -> {b:<10} {t:5.1f}")
    print("\nSightlines:")
    for sl, info in zip(layout["sightlines"], sight_info):
        print(f"  {sl['name']:<28} {info['length']:5.1f} m  a->b bearing {info['bearing_ab']:5.1f} (sun {info['angle_ab']:5.1f} off)"
              f"  b->a bearing {info['bearing_ba']:5.1f} (sun {info['angle_ba']:5.1f} off)")
    print("\nLanes on the real ground:")
    for row in lane_stats:
        print("  %-22s %6.1f-%6.1f m  steepest %4.1f %%" % row)
    print("\nPoints of interest (world x, z, ground height):")
    for k in layout["spawns"] + layout["flags"]:
        x, z = frame.world(*k["at"])
        print(f"  {k['id']:<11} x {x:7.1f} z {z:7.1f}  h {float(height_at(x, z)):6.1f}")
    corners = [frame.world(u, v) for u in (-75, 75) for v in (-60, 60)]
    print("  frame corners:", ", ".join(f"({x:.0f}, {z:.0f})" for x, z in corners))


def profiles(layout, frame, height_at):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    fig, axes = plt.subplots(2, 1, figsize=(16, 10), dpi=110)
    fig.patch.set_facecolor("#1a1c20")
    vs = np.linspace(-80, 80, 321)
    lane_v = {}
    for lane in layout["lanes"]:
        pts = np.array(lane["points"], float)
        lane_v[lane["id"]] = pts
    ax = axes[0]
    for u, col in ((-50, "#3cc8ff"), (0, "#ffffff"), (50, "#ff5aaa")):
        hs = [float(height_at(*frame.world(u, v))) for v in vs]
        ax.plot(vs, hs, color=col, lw=2, label=f"u = {u:+d} m")
        for lid, pts in lane_v.items():
            lv = np.interp(u, pts[:, 0], pts[:, 1])
            ax.plot([lv], [float(height_at(*frame.world(u, lv)))], "o", color=col, ms=7)
    ax.axvspan(-60, 60, color="#ffffff", alpha=0.06)
    ax.set_title("Cross-sections south → north (uphill), real ground; dots = lanes (lower, main, upper)", color="w")
    ax.set_xlabel("v (m, frame: + = uphill)", color="w")
    ax.set_ylabel("m above sea level", color="w")
    ax.legend()
    ax2 = axes[1]
    us = np.linspace(-90, 90, 361)
    for lane, col in zip(layout["lanes"], ("#ffd060", "#ffffff", "#7fd0a0")):
        pts = np.array(lane["points"], float)
        hs = [float(height_at(*frame.world(u, np.interp(u, pts[:, 0], pts[:, 1])))) for u in us]
        ax2.plot(us, hs, color=col, lw=2, label=lane["name"])
    ax2.axvspan(-75, 75, color="#ffffff", alpha=0.06)
    ax2.axvline(5, color="#6fdc9a", ls="--", lw=1)
    ax2.text(6, ax2.get_ylim()[0] + 2, "ravine", color="#6fdc9a")
    ax2.set_title("Along each lane, west → east, real ground (before smoothing and terracing in Task 7)", color="w")
    ax2.set_xlabel("u (m, frame: + = east along the street)", color="w")
    ax2.set_ylabel("m above sea level", color="w")
    ax2.legend()
    for a in axes:
        a.set_facecolor("#24262c")
        a.tick_params(colors="w")
        a.grid(alpha=0.2)
    fig.tight_layout()
    fig.savefig(OUT / "task06_slope_profiles.png", facecolor=fig.get_facecolor())
    print("wrote", OUT / "task06_slope_profiles.png")


if __name__ == "__main__":
    main()
