"""BEFORE | AFTER pair for one review view, optionally cropped and labelled.

    uv run --with pillow Tools/review/pair.py PREFIX VIEW OUT [x0 y0 x1 y1] [height]

Reads Screenshots/{PREFIX}_before_{VIEW}.png and {PREFIX}_after_{VIEW}.png, writes Screenshots/{OUT}.png.
"""
import sys
from pathlib import Path

from PIL import Image, ImageDraw

SHOTS = Path(__file__).resolve().parents[2] / "Screenshots"


def main():
    prefix, view, out = sys.argv[1:4]
    box = tuple(int(v) for v in sys.argv[4:8]) if len(sys.argv) >= 8 else None
    height = int(sys.argv[8]) if len(sys.argv) >= 9 else 540
    pair = []
    for state in ("before", "after"):
        im = Image.open(SHOTS / f"{prefix}_{state}_{view}.png").convert("RGB")
        if box:
            im = im.crop(box)
        pair.append(im.resize((int(im.width * height / im.height), height), Image.LANCZOS))
    sheet = Image.new("RGB", (pair[0].width * 2 + 10, height), (20, 20, 20))
    sheet.paste(pair[0], (0, 0))
    sheet.paste(pair[1], (pair[0].width + 10, 0))
    d = ImageDraw.Draw(sheet)
    for x, label in ((8, f"BEFORE (view {view})"), (pair[0].width + 18, f"AFTER (view {view})")):
        d.rectangle((x - 4, 6, x + 140, 24), fill=(0, 0, 0))
        d.text((x, 9), label, fill=(255, 230, 80))
    sheet.save(SHOTS / f"{out}.png")
    print(out, sheet.size)


if __name__ == "__main__":
    main()
