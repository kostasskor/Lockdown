"""Downloads the CC0 texture sets, decals and props the map uses into Assets/Lockdown/ThirdParty/,
and records each one in Assets/Lockdown/thirdparty_manifest.json.

    uv run --with requests --with pillow Tools/assets/lockdown_fetch.py

Re-running skips files that already exist. Sources: Poly Haven (api.polyhaven.com) and ambientCG, both CC0.
"""
import io
import json
import zipfile
from pathlib import Path

import requests
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
TP = ROOT / "Assets/Lockdown/ThirdParty"
MANIFEST = ROOT / "Assets/Lockdown/thirdparty_manifest.json"

# id: (resolution, folder, used in)
POLYHAVEN_TEXTURES = {
    "painted_plaster_wall": ("2k", "PolyHaven/Textures", "Whitewashed render (upper floors); blue paint over plaster"),
    "white_rough_plaster": ("2k", "PolyHaven/Textures", "Patchy, rough whitewash on ground floors and kerbs"),
    "concrete_wall_009": ("2k", "PolyHaven/Textures", "Bare concrete: rooftop room, sills, porch floor"),
    "stone_tiles_02": ("2k", "PolyHaven/Textures", "Grey stone tiles on outside stairs and balconies"),
    "asphalt_02": ("2k", "PolyHaven/Textures", "Village street surface"),
    "weathered_brown_planks": ("1k", "PolyHaven/Props", "Timber pergolas, wooden doors and shutters"),
    "white_planks_clean": ("1k", "PolyHaven/Props", "White painted shutters, window and door frames"),
    "rusty_metal_02": ("1k", "PolyHaven/Props", "Black painted iron railings and flues (darkened)"),
}
POLYHAVEN_MODELS = {
    "potted_plant_01": ("1k", "PolyHaven/Props", "Potted plants along house walls"),
    "potted_plant_02": ("1k", "PolyHaven/Props", "Potted plants along house walls"),
}
# id: (used in, decal colour baked into the texture; None keeps the scan's own colour)
AMBIENTCG_DECALS = {
    "Leaking009": ("Damp band at wall bases (decal)", (82, 82, 76)),
    "Leaking012A": ("Grime streaks down balcony edges and parapets (decal)", (84, 78, 68)),
    "Leaking016A": ("Rust streaks under railings and flues (decal)", (112, 62, 32)),
}
BAND_DECALS = {"Leaking009": 1.6}  # id: alpha boost
PH_MAPS = {"Diffuse": "diff", "nor_gl": "nor_gl", "Rough": "rough", "AO": "ao"}

session = requests.Session()
session.headers["User-Agent"] = "Lockdown-Lockdown-asset-fetch"


def get(url):
    r = session.get(url, timeout=120)
    r.raise_for_status()
    return r.content


def fetch_ph_texture(asset_id, res, folder):
    out = TP / folder / asset_id
    out.mkdir(parents=True, exist_ok=True)
    files = session.get(f"https://api.polyhaven.com/files/{asset_id}", timeout=60).json()
    for key, suffix in PH_MAPS.items():
        if key not in files:
            continue
        fmt = files[key][res]
        url = (fmt.get("jpg") or fmt.get("png"))["url"]
        dest = out / f"{asset_id}_{suffix}_{res}{Path(url).suffix}"
        if not dest.exists():
            dest.write_bytes(get(url))
            print("  ", dest.relative_to(ROOT))


def fetch_ph_model(asset_id, res, folder):
    out = TP / folder / asset_id
    out.mkdir(parents=True, exist_ok=True)
    files = session.get(f"https://api.polyhaven.com/files/{asset_id}", timeout=60).json()
    gltf = files["gltf"][res]["gltf"]
    dest = out / Path(gltf["url"]).name
    if not dest.exists():
        dest.write_bytes(get(gltf["url"]))
    for rel, info in gltf.get("include", {}).items():
        d = out / rel
        d.parent.mkdir(parents=True, exist_ok=True)
        if not d.exists():
            d.write_bytes(get(info["url"]))
    print("  ", dest.relative_to(ROOT))


def fetch_acg_decal(asset_id, tint):
    out = TP / "AmbientCG/Decals" / asset_id
    out.mkdir(parents=True, exist_ok=True)
    diff = out / f"{asset_id.lower()}_diff.png"
    if diff.exists():
        return
    res = "2K" if asset_id in BAND_DECALS else "1K"  # the band is cropped to a strip, so start from 2K
    z = zipfile.ZipFile(io.BytesIO(get(f"https://ambientcg.com/get?file={asset_id}_{res}-PNG.zip")))
    names = {n.split("_")[-1].split(".")[0].lower(): n for n in z.namelist() if n.lower().endswith(".png")}
    color = Image.open(z.open(names["color"])).convert("RGB")
    if "opacity" in names:
        alpha = Image.open(z.open(names["opacity"])).convert("L")
    else:
        # Streak sheets: the streaks only live in the roughness map (wet = low roughness), so
        # opacity comes from there and the colour is a neutral grime tone.
        rough = Image.open(z.open(names["roughness"])).convert("L")
        alpha = rough.point(lambda v: max(0, min(255, int((34 - v) * 14))))
    if tint is not None:
        # Keep the scan's light/dark detail, recoloured to the village's grime tone.
        lum = color.convert("L").point(lambda v: int(170 + v / 255 * 85))
        color = Image.merge("RGB", [lum.point(lambda v, c=c: int(v / 255 * c)) for c in tint])
    rgba = color.copy()
    rgba.putalpha(alpha.resize(color.size))
    if asset_id in BAND_DECALS:
        # The scan's grime sits in a thin strip at the bottom: crop to it and strengthen it so the
        # band fills the projector instead of hiding under the plinth.
        import numpy as np
        arr = np.asarray(rgba).astype(np.float32)
        rows = np.where((arr[..., 3] > 32).mean(axis=1) > 0.05)[0]
        arr = arr[max(0, rows[0] - 8):rows[-1] + 1]
        arr[..., 3] = np.clip(arr[..., 3] * BAND_DECALS[asset_id], 0, 255)
        # Keep the strip at its native resolution: resizing only the height stretched the drips ~3.7x.
        # The decal builder sets the tiling from this aspect, so texels stay square on the wall.
        rgba = Image.fromarray(arr.astype(np.uint8), "RGBA")
    rgba.save(diff)
    if "normalgl" in names and asset_id not in BAND_DECALS:  # a cropped band has no matching normal map
        Image.open(z.open(names["normalgl"])).convert("RGB").save(out / f"{asset_id.lower()}_nor_gl.png")
    print("  ", diff.relative_to(ROOT))


def make_peel_decal():
    """Peeling-whitewash decal derived from Poly Haven's white_rough_plaster: its exposed grey patches become
    the decal, fading out upward so the peeling sits at the wall base like in houses/.../01."""
    import numpy as np
    src = TP / "PolyHaven/Textures/white_rough_plaster/white_rough_plaster_diff_2k.jpg"
    out = ROOT / "Assets/Lockdown/Textures/Decals/T_Decal_Peel_diff.png"
    if out.exists():
        return
    out.parent.mkdir(parents=True, exist_ok=True)
    im = Image.open(src).convert("RGB").resize((2048, 2048))
    im = im.crop((0, 0, 2048, 1024)).resize((1024, 512))
    a = np.asarray(im).astype(np.float32)
    lum = a.mean(axis=2)
    patch = np.clip((200.0 - lum) / 22.0, 0.0, 1.0)          # exposed plaster = darker than the whitewash
    ramp = np.linspace(0.0, 1.0, patch.shape[0])[:, None] ** 1.1  # 0 at the top, 1 at the bottom
    alpha = (patch * ramp * 255).astype(np.uint8)
    a = a * np.array([0.72, 0.70, 0.66])                         # bare lime-cement plaster, a little darker and warmer
    rgba = np.dstack([np.clip(a, 0, 255).astype(np.uint8), alpha])
    Image.fromarray(rgba, "RGBA").save(out)
    print("  ", out.relative_to(ROOT))


def record(entries):
    data = json.loads(MANIFEST.read_text(encoding="utf-8"))
    known = {a["source"] for a in data["assets"]}
    for e in entries:
        if e["source"] not in known:
            data["assets"].append(e)
    MANIFEST.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def main():
    entries = []
    for aid, (res, folder, used) in POLYHAVEN_TEXTURES.items():
        print(aid)
        fetch_ph_texture(aid, res, folder)
        entries.append({"name": aid, "source": f"https://polyhaven.com/a/{aid}", "license": "CC0", "author": "Poly Haven",
                        "files": f"{folder}/{aid}", "usedIn": used})
    for aid, (res, folder, used) in POLYHAVEN_MODELS.items():
        print(aid)
        fetch_ph_model(aid, res, folder)
        entries.append({"name": aid, "source": f"https://polyhaven.com/a/{aid}", "license": "CC0", "author": "Poly Haven",
                        "files": f"{folder}/{aid}", "usedIn": used})
    for aid, (used, tint) in AMBIENTCG_DECALS.items():
        print(aid)
        fetch_acg_decal(aid, tint)
        entries.append({"name": aid, "source": f"https://ambientcg.com/view?id={aid}", "license": "CC0", "author": "ambientCG",
                        "files": f"AmbientCG/Decals/{aid}", "usedIn": used})
    make_peel_decal()
    record(entries)


if __name__ == "__main__":
    main()
