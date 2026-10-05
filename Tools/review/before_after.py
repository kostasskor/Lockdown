"""Builds one BEFORE | AFTER sheet per review fix from the t4c_before_XX / t4c_after_XX captures.

    uv run --with pillow Tools/review/before_after.py
"""
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[2]
SHOTS = ROOT / "Screenshots"

# fix name: [(view, crop box in 1920x1080 or None), ...]
FIXES = {
    "1_stair": [("12", None), ("03", (760, 0, 1920, 1080))],
    "3_decal_stretch": [("05", (300, 200, 1500, 1080)), ("06", (500, 150, 1700, 1080)), ("08", (500, 450, 1750, 1000))],
    "4_plaster_blend": [("01", None), ("02", None)],
    "5_corner_seams": [("10", (480, 0, 1440, 1080)), ("07", (500, 0, 1460, 1080))],
    "6_flagstones": [("11", (0, 450, 1000, 1080)), ("12", (500, 550, 1600, 1080))],
    "7_quoins": [("11", (760, 0, 1660, 1080))],
}
ROW_H = 460


def main():
    for name, items in FIXES.items():
        rows = []
        for view, box in items:
            pair = []
            for state in ("before", "after"):
                im = Image.open(SHOTS / f"t4c_{state}_{view}.png").convert("RGB")
                if box:
                    im = im.crop(box)
                pair.append(im.resize((int(im.width * ROW_H / im.height), ROW_H)))
            row = Image.new("RGB", (pair[0].width * 2 + 12, ROW_H), (20, 20, 20))
            row.paste(pair[0], (0, 0))
            row.paste(pair[1], (pair[0].width + 12, 0))
            d = ImageDraw.Draw(row)
            for x, label in ((8, f"BEFORE  (view {view})"), (pair[0].width + 20, f"AFTER  (view {view})")):
                d.rectangle((x - 4, 6, x + 150, 26), fill=(0, 0, 0))
                d.text((x, 10), label, fill=(255, 230, 80))
            rows.append(row)
        w = max(r.width for r in rows)
        sheet = Image.new("RGB", (w, sum(r.height + 10 for r in rows)), (20, 20, 20))
        y = 0
        for r in rows:
            sheet.paste(r, (0, y))
            y += r.height + 10
        out = SHOTS / f"t4c_fix{name}.png"
        sheet.save(out)
        print(out.name, sheet.size)


if __name__ == "__main__":
    main()
