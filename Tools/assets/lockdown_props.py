"""Game-ready props from the Poly Haven CC0 scans (no Blender needed): every mesh of a prop is simplified with
meshoptimizer, attribute-aware (UVs and normals weigh in, UV seams stay intact, kept vertices keep their exact
position, normal and UV), into an LOD0 and an LOD1 glTF that reuse the original textures.

    uv run --with meshoptimizer --with numpy --with scipy --with pillow Tools/assets/lockdown_props.py

Sources: Assets/Lockdown/ThirdParty/PolyHaven/Props/<name>/<name>_1k.gltf (never modified).
Output:  Assets/Lockdown/Models/Generated/Props/<name>/<name>_LOD0.gltf, _LOD1.gltf (+ .bin, textures/ copied).
Budgets are vertex counts per mesh (the unit Unity's stats and the brief use); the project prop rule sets them.
"""
import copy
import ctypes  # the binding hands target_error to C untyped: it must be a c_float
import json
import os
from pathlib import Path

import meshoptimizer as mo
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Assets/Lockdown/ThirdParty/PolyHaven/Props"
OUT = ROOT / "Assets/Lockdown/Models/Generated/Props"

# Per prop: node name -> LOD0 vertex budget, or ("bake", (rings, segments) LOD0, (rings, segments) LOD1) for a loose
# layer of hundreds of stones or clumps inside a pot: each piece needs at least a tetrahedron, so they can't simplify
# under ~2k verts; seen only from above, they become a baked, displaced disc.
# LOD1 is never pruned and never budget-driven: it is simplified until its shape error reaches one pixel at the
# distance where it takes over (LOD1_DISTANCE, the same as LockdownPropBuilder.Lod1Distance), so the switch can't show.
PROPS = {
    "potted_plant_01": {
        "potted_plant_01_leaves": 4300,
        "potted_plant_01_pebbles": ("bake", (8, 32), (3, 16)),
        "potted_plant_01_stem": 500,
        "potted_plant_01_pot": 900,
    },
    "potted_plant_02": {
        "potted_plant_02_leaves": 2200,
        "potted_plant_02_dirt": ("bake", (8, 32), (3, 16)),
        "potted_plant_02_pot": 800,
    },
}
LOD1_DISTANCE = {"potted_plant_01": 12.0, "potted_plant_02": 12.0}  # metres
PIXEL_AT_1M = 2 * np.tan(np.radians(32)) / 1080  # one pixel at 1 m: player FOV 64, 1080p
UV_WEIGHT, NORMAL_WEIGHT = 1.0, 0.3

CT = {5126: np.float32, 5125: np.uint32, 5123: np.uint16, 5121: np.uint8}
NC = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}


def load(path):
    g = json.loads(path.read_text())
    bufs = [(path.parent / b["uri"]).read_bytes() for b in g["buffers"]]

    def acc(i):
        a = g["accessors"][i]
        bv = g["bufferViews"][a["bufferView"]]
        n = NC[a["type"]]
        arr = np.frombuffer(bufs[bv["buffer"]], dtype=CT[a["componentType"]], count=a["count"] * n,
                            offset=bv.get("byteOffset", 0) + a.get("byteOffset", 0))
        return arr.reshape(-1, n) if n > 1 else arr
    return g, acc


def weld(P, N, UV, I):
    """Merge exact duplicate vertices (same position, normal and UV). The scans carry thousands of them as an export
    artifact, and the simplifier treats each as a seam it may not collapse across, so they block reduction."""
    key = np.round(np.hstack([P, N, UV]).astype(np.float64), 6)
    _, first, inv = np.unique(key, axis=0, return_index=True, return_inverse=True)
    return P[first], N[first], UV[first], inv.ravel()[I].astype(np.uint32)


def simplify_to_error(P, N, UV, I, max_error):
    """As simple as possible while the shape error stays under max_error (metres)."""
    P = np.ascontiguousarray(P, np.float32)
    attrs = np.ascontiguousarray(np.hstack([UV, N]).astype(np.float32))
    weights = np.array([UV_WEIGHT] * 2 + [NORMAL_WEIGHT] * 3, np.float32)
    I = np.ascontiguousarray(I.astype(np.uint32))
    dst = np.zeros(len(I), np.uint32)
    err = np.zeros(1, np.float32)
    n = mo.simplify_with_attributes(dst, I, P, attrs, weights, vertex_count=len(P), target_index_count=3,
                                    target_error=ctypes.c_float(max_error), options=mo.SIMPLIFY_ERROR_ABSOLUTE,
                                    result_error=err)
    out = dst[:n]
    return out, len(np.unique(out)), float(err[0])


def simplify(P, N, UV, I, budget):
    """Smallest error whose result fits the vertex budget (binary search on the target index count)."""
    P = np.ascontiguousarray(P, np.float32)
    attrs = np.ascontiguousarray(np.hstack([UV, N]).astype(np.float32))
    weights = np.array([UV_WEIGHT] * 2 + [NORMAL_WEIGHT] * 3, np.float32)
    I = np.ascontiguousarray(I.astype(np.uint32))

    def run(target):
        dst = np.zeros(len(I), np.uint32)
        err = np.zeros(1, np.float32)
        n = mo.simplify_with_attributes(dst, I, P, attrs, weights, vertex_count=len(P), target_index_count=target,
                                        target_error=ctypes.c_float(1.0), result_error=err)
        out = dst[:n]
        return out, len(np.unique(out)), float(err[0])

    lo, hi = 3, len(I)
    best = run(lo)
    while hi - lo > 3:
        mid = (lo + hi) // 2 // 3 * 3
        res = run(mid)
        if res[1] <= budget:
            best, lo = res, mid
        else:
            hi = mid
    return best


def sample(img, uv):
    """Bilinear lookup of a float image (H, W, C) at glTF UVs (origin top-left), wrapping."""
    h, w = img.shape[:2]
    x, y = uv[..., 0] * w - 0.5, uv[..., 1] * h - 0.5
    x0, y0 = np.floor(x).astype(int), np.floor(y).astype(int)
    fx, fy = (x - x0)[..., None], (y - y0)[..., None]
    g = lambda yy, xx: img[yy % h, xx % w]
    return (g(y0, x0) * (1 - fx) + g(y0, x0 + 1) * fx) * (1 - fy) + (g(y0 + 1, x0) * (1 - fx) + g(y0 + 1, x0 + 1) * fx) * fy


def bake_layer(P, N, UV, I, diff, rough, res=512):
    """A loose layer (pebbles, dirt clumps) inside a pot is only ever seen from above, so it becomes a displaced disc:
    the layer is rasterised top-down (highest surface wins) into albedo, normal and roughness maps sampled from its
    own textures, with contact shadow between the stones from the height map. Returns the maps and a disc builder."""
    c = np.array([P[:, 0].mean(), P[:, 2].mean()])
    R = np.hypot(P[:, 0] - c[0], P[:, 2] - c[1]).max()
    to_px = lambda x, z: np.stack([((x - c[0]) / (2 * R) + 0.5) * res, ((z - c[1]) / (2 * R) + 0.5) * res], -1)
    depth = np.full((res, res), -np.inf)
    uvbuf = np.zeros((res, res, 2))
    nbuf = np.zeros((res, res, 3))
    T = I.reshape(-1, 3)
    pp = to_px(P[:, 0], P[:, 2])
    for t in T:
        a, b, cc = pp[t]
        lo = np.floor(np.minimum(np.minimum(a, b), cc)).astype(int)
        hi = np.ceil(np.maximum(np.maximum(a, b), cc)).astype(int)
        lo, hi = np.clip(lo, 0, res - 1), np.clip(hi, 0, res - 1)
        xs, ys = np.meshgrid(np.arange(lo[0], hi[0] + 1) + 0.5, np.arange(lo[1], hi[1] + 1) + 0.5)
        d = (b[1] - cc[1]) * (a[0] - cc[0]) + (cc[0] - b[0]) * (a[1] - cc[1])
        if abs(d) < 1e-12:
            continue
        w0 = ((b[1] - cc[1]) * (xs - cc[0]) + (cc[0] - b[0]) * (ys - cc[1])) / d
        w1 = ((cc[1] - a[1]) * (xs - cc[0]) + (a[0] - cc[0]) * (ys - cc[1])) / d
        w2 = 1 - w0 - w1
        inside = (w0 >= -1e-6) & (w1 >= -1e-6) & (w2 >= -1e-6)
        if not inside.any():
            continue
        W = np.stack([w0, w1, w2], -1)[inside]
        y = W @ P[t, 1]
        iy, ix = (ys[inside] - 0.5).astype(int), (xs[inside] - 0.5).astype(int)
        better = y > depth[iy, ix]
        if not better.any():
            continue
        iy, ix, W, y = iy[better], ix[better], W[better], y[better]
        depth[iy, ix] = y
        uvbuf[iy, ix] = W @ UV[t]
        nbuf[iy, ix] = W @ N[t]
    covered = np.isfinite(depth)
    # Fill uncovered pixels (between stones seen through to nothing, and outside the disc) from the nearest covered.
    from scipy import ndimage
    idx = ndimage.distance_transform_edt(~covered, return_distances=False, return_indices=True)
    depth, uvbuf, nbuf = depth[tuple(idx)], uvbuf[tuple(idx)], nbuf[tuple(idx)]
    albedo = sample(diff, uvbuf)
    rgh = sample(rough, uvbuf)
    # Contact shadow: how far a pixel sits below its surroundings (gaps between pebbles), as in a cavity map.
    cavity = np.clip((ndimage.gaussian_filter(depth, 6) - depth) / 0.006, 0, 1)
    albedo = albedo * (1 - 0.45 * cavity[..., None])
    n = nbuf / np.maximum(np.linalg.norm(nbuf, axis=-1, keepdims=True), 1e-6)
    # Tangent space of the disc: u runs along +x, image up (v decreasing) along -z, the disc normal is +y.
    nts = np.stack([n[..., 0], -n[..., 2], np.maximum(n[..., 1], 0.05)], -1)
    nts /= np.linalg.norm(nts, axis=-1, keepdims=True)
    smooth_h = ndimage.gaussian_filter(depth, res / 40)

    def disc(rings, segs):
        verts, uvs = [np.array([c[0], 0, c[1]])], [np.array([0.5, 0.5])]
        for i in range(1, rings + 1):
            r = R * i / rings
            for k in range(segs):
                ang = 2 * np.pi * k / segs
                verts.append(np.array([c[0] + r * np.cos(ang), 0, c[1] + r * np.sin(ang)]))
                uvs.append(np.array([0.5 + r * np.cos(ang) / (2 * R), 0.5 + r * np.sin(ang) / (2 * R)]))
        V, U = np.array(verts), np.array(uvs)
        h = sample(smooth_h[..., None], U)[:, 0]
        V[:, 1] = h
        idx = []
        for k in range(segs):
            idx += [0, 1 + (k + 1) % segs, 1 + k]
        for i in range(rings - 1):
            a0, b0 = 1 + i * segs, 1 + (i + 1) * segs
            for k in range(segs):
                k1 = (k + 1) % segs
                idx += [a0 + k, a0 + k1, b0 + k, a0 + k1, b0 + k1, b0 + k]
        # Smooth normals from the displaced surface.
        F = np.array(idx).reshape(-1, 3)
        fn = np.cross(V[F[:, 1]] - V[F[:, 0]], V[F[:, 2]] - V[F[:, 0]])
        if fn[:, 1].mean() < 0:
            F, fn = F[:, ::-1], -fn
        Nv = np.zeros_like(V)
        for j in range(3):
            np.add.at(Nv, F[:, j], fn)
        Nv /= np.linalg.norm(Nv, axis=1, keepdims=True)
        return V.astype(np.float32), Nv.astype(np.float32), U.astype(np.float32), F.ravel().astype(np.uint32)

    maps = {
        "diff": np.clip(albedo, 0, 255).astype(np.uint8),
        "nor_gl": np.clip((nts * 0.5 + 0.5) * 255, 0, 255).astype(np.uint8),
        "rough": np.clip(rgh, 0, 255).astype(np.uint8),
    }
    return maps, disc


def compact(I, *attrs):
    """Keep only used vertices, in first-use order (vertex-fetch friendly), after a vertex-cache pass."""
    vc = np.zeros(len(I), np.uint32)
    mo.optimize_vertex_cache(vc, I, vertex_count=int(I.max()) + 1)
    _, first = np.unique(vc, return_index=True)
    order = vc[np.sort(first)]
    remap = np.full(int(I.max()) + 1, -1, np.int64)
    remap[order] = np.arange(len(order))
    return remap[vc].astype(np.uint32), [a[order] for a in attrs]


def write(src_g, meshes, out_path):
    """New glTF with the source's nodes, materials and textures; one primitive per mesh from `meshes`."""
    g = {k: v for k, v in src_g.items() if k not in ("buffers", "bufferViews", "accessors", "meshes")}
    g["asset"] = {"version": "2.0", "generator": "lockdown_props.py (meshoptimizer)"}
    blob, views, accs, out_meshes = bytearray(), [], [], []

    def add(arr, target, kind, minmax=False):
        nonlocal blob
        while len(blob) % 4:
            blob += b"\0"
        data = arr.tobytes()
        views.append({"buffer": 0, "byteOffset": len(blob), "byteLength": len(data), **({"target": target} if target else {})})
        blob += data
        a = {"bufferView": len(views) - 1, "componentType": 5125 if arr.dtype == np.uint32 else 5126,
             "count": len(arr), "type": kind}
        if minmax:
            a["min"], a["max"] = arr.min(0).tolist(), arr.max(0).tolist()
        accs.append(a)
        return len(accs) - 1

    for m in meshes:
        prim = {
            "attributes": {
                "POSITION": add(m["P"].astype(np.float32), 34962, "VEC3", True),
                "NORMAL": add(m["N"].astype(np.float32), 34962, "VEC3"),
                "TEXCOORD_0": add(m["UV"].astype(np.float32), 34962, "VEC2"),
            },
            "indices": add(m["I"].astype(np.uint32), 34963, "SCALAR"),
            "material": m["material"],
        }
        out_meshes.append({"name": m["name"], "primitives": [prim]})
    g["meshes"] = out_meshes
    g["bufferViews"], g["accessors"] = views, accs
    bin_name = out_path.with_suffix(".bin").name
    g["buffers"] = [{"uri": bin_name, "byteLength": len(blob)}]
    out_path.with_suffix(".bin").write_bytes(bytes(blob))
    out_path.write_text(json.dumps(g, indent=1))


def main():
    for name, parts in PROPS.items():
        src = SRC / name / f"{name}_1k.gltf"
        g, acc = load(src)
        dst = OUT / name
        dst.mkdir(parents=True, exist_ok=True)
        (dst / "textures").mkdir(exist_ok=True)
        g = copy.deepcopy(g)
        # The source textures are used where they are (no copies: no duplicate files or asset GUIDs in the repo).
        for im in g["images"]:
            im["uri"] = os.path.relpath(SRC / name / im["uri"], dst).replace("\\", "/")
        node_of_mesh = {n["mesh"]: n["name"] for n in g["nodes"] if "mesh" in n}
        lods = {0: [], 1: []}
        rows = []
        for mi, mesh in enumerate(g["meshes"]):
            node = node_of_mesh[mi]
            pr = mesh["primitives"][0]
            P, N, UV = (acc(pr["attributes"][k]) for k in ("POSITION", "NORMAL", "TEXCOORD_0"))
            I = acc(pr["indices"])
            raw = len(P)
            P, N, UV, I = weld(P, N, UV, I)
            cfg = parts[node]
            row = [node, raw]
            if isinstance(cfg, tuple) and cfg[0] == "bake":
                mat = g["materials"][pr["material"]]
                img = lambda tex: np.asarray(Image.open(dst / g["images"][g["textures"][tex["index"]]["source"]]["uri"]).convert("RGB"), np.float32)
                diff = img(mat["pbrMetallicRoughness"]["baseColorTexture"])
                rough = img(mat["pbrMetallicRoughness"]["metallicRoughnessTexture"])[..., 1:2]
                maps, disc = bake_layer(P.astype(np.float64), N.astype(np.float64), UV.astype(np.float64), I, diff, rough)
                # A material of its own with the baked maps (glTF: roughness read from G, so a grey image works).
                tex_ids = {}
                for key, data in maps.items():
                    fname = f"textures/{node}_baked_{key}.png"
                    Image.fromarray(data if data.shape[-1] != 1 else data[..., 0]).save(dst / fname)
                    g["images"].append({"mimeType": "image/png", "name": f"{node}_baked_{key}", "uri": fname})
                    g["textures"].append({"sampler": 0, "source": len(g["images"]) - 1})
                    tex_ids[key] = len(g["textures"]) - 1
                baked = copy.deepcopy(mat)
                baked["name"] = f"{node}_baked"
                baked["normalTexture"] = {"index": tex_ids["nor_gl"]}
                baked["pbrMetallicRoughness"]["baseColorTexture"] = {"index": tex_ids["diff"]}
                baked["pbrMetallicRoughness"]["metallicRoughnessTexture"] = {"index": tex_ids["rough"]}
                g["materials"].append(baked)
                for lod, (rings, segs) in ((0, cfg[1]), (1, cfg[2])):
                    Pc, Nc, UVc, Ic = disc(rings, segs)
                    lods[lod].append({"name": mesh["name"], "P": Pc, "N": Nc, "UV": UVc, "I": Ic, "material": len(g["materials"]) - 1})
                    row += [len(Pc), 0.0]
            else:
                ext = float((P.max(0) - P.min(0)).max())
                for lod in (0, 1):
                    if lod == 0:
                        Is, nv, err = simplify(P, N, UV, I, cfg)
                        err *= ext  # relative -> metres
                    else:
                        Is, nv, err = simplify_to_error(P, N, UV, I, PIXEL_AT_1M * LOD1_DISTANCE[name])
                    Ic, (Pc, Nc, UVc) = compact(Is, P, N, UV)
                    lods[lod].append({"name": mesh["name"], "P": Pc, "N": Nc, "UV": UVc, "I": Ic, "material": pr["material"]})
                    row += [len(Pc), err]
            rows.append(row)
        for lod in (0, 1):
            write(g, lods[lod], dst / f"{name}_LOD{lod}.gltf")
        print(f"\n{name}")
        print(f"  {'part':28s} {'source':>8s} {'LOD0':>7s} {'err0 mm':>8s} {'LOD1':>7s} {'err1 mm':>8s}")
        for r in rows:
            print(f"  {r[0]:28s} {r[1]:8d} {r[2]:7d} {r[3] * 1000:8.1f} {r[4]:7d} {r[5] * 1000:8.1f}")
        tot = [sum(r[i] for r in rows) for i in (1, 2, 4)]
        print(f"  {'TOTAL':28s} {tot[0]:8d} {tot[1]:7d} {'':8s} {tot[2]:7d}")


if __name__ == "__main__":
    main()
