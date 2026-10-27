"""Kalderimi (stone path) palette and soil strip, for the flagstone paths built by Tools/layout/lockdown_stones.py.

    uv run --with numpy --with pillow Tools/assets/lockdown_kalderimi_palette.py

Writes Assets/Lockdown/Textures/Ground/
    T_KalderimiStones_pal.png  32 swatches of 8 px, two rows: a neutral grey per stone (face) and a slightly darker rim
"""
import sys
from pathlib import Path

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "layout"))
import lockdown_stones as vs

OUT = Path(__file__).resolve().parents[2] / "Assets" / "Lockdown" / "Textures" / "Ground"
pal = np.zeros((16, 256, 3), np.uint8)
for i in range(32):
    c = np.array(vs.TONES[i % len(vs.TONES)], float)
    # image rows run top-down but UV v runs bottom-up: the bottom rows (v 0..0.5) are the face, the top rows the rim
    pal[8:, i * 8:(i + 1) * 8] = c.round()                 # v 0.25: the stone's face
    pal[:8, i * 8:(i + 1) * 8] = (c * 0.72).round()        # v 0.75: darker rim (bevel, side)
Image.fromarray(pal).save(OUT / "T_KalderimiStones_pal.png")

print("wrote", OUT)
