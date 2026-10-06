"""Side-by-side review board: a capture next to its reference photos, all at one height, each labelled.

    uv run --with pillow Tools/review/compare.py OUT.png [--height 540] IMAGE[=LABEL] IMAGE[=LABEL] ...

Paths are relative to the project root. Without a label, the file name is used. Writes OUT into Screenshots/.
"""
import sys
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[2]


def main():
    args = sys.argv[1:]
    out = args.pop(0)
    height = 540
    if args and args[0] == "--height":
        height = int(args[1])
        args = args[2:]
    tiles = []
    for a in args:
        path, _, label = a.partition("=")
        im = Image.open(ROOT / path).convert("RGB")
        im = im.resize((int(im.width * height / im.height), height), Image.LANCZOS)
        tiles.append((im, label or Path(path).name))
    bar = 24
    sheet = Image.new("RGB", (sum(t.width for t, _ in tiles) + 8 * (len(tiles) - 1), height + bar), (20, 20, 20))
    draw = ImageDraw.Draw(sheet)
    x = 0
    for im, label in tiles:
        sheet.paste(im, (x, bar))
        draw.text((x + 6, 5), label, fill=(235, 235, 235))
        x += im.width + 8
    dest = ROOT / "Screenshots" / out
    dest.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(dest)
    print(dest)


if __name__ == "__main__":
    main()
