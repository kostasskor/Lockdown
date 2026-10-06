"""Brightness check for the review lighting (brief Phase 6, "Shade"): mean linear luminance of image regions,
for captures rendered with the measurement profile (no grade, no tonemapping, post-exposure -2, see
LockdownReviewLighting.SetMeasureMode), so the sRGB values convert straight back to scene light.

    uv run --with pillow --with numpy Tools/review/luminance.py <image.png> name=x0,y0,x1,y1 [name=...] [--ratio a/b ...]

Regions are pixel rectangles in the 1920x1080 capture. Prints each region's mean luminance (linear, exposure undone),
its share of clipped pixels (any channel at 255: the measurement exposure was too high there), and the asked ratios.
Targets: sunlit / shaded ground 4-6x, sunlit / shaded wall ~10x.
"""
import sys

import numpy as np
from PIL import Image

MEASURE_EXPOSURE = -2.0  # stops, LockdownReviewLighting.MeasureExposure


def linear(srgb):
    c = srgb / 255.0
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def main():
    img = np.asarray(Image.open(sys.argv[1]).convert("RGB")).astype(np.float64)
    lin = linear(img) * 2.0 ** -MEASURE_EXPOSURE
    lum = 0.2126 * lin[..., 0] + 0.7152 * lin[..., 1] + 0.0722 * lin[..., 2]
    clipped = (img >= 254.5).any(axis=2)
    values, ratios = {}, []
    args = sys.argv[2:]
    i = 0
    while i < len(args):
        if args[i] == "--ratio":
            ratios.append(args[i + 1])
            i += 2
            continue
        name, rect = args[i].split("=")
        x0, y0, x1, y1 = map(int, rect.split(","))
        values[name] = lum[y0:y1, x0:x1].mean()
        print(f"  {name:20s} L={values[name]:7.3f}  clipped {clipped[y0:y1, x0:x1].mean() * 100:5.1f} %")
        i += 1
    for r in ratios:
        a, b = r.split("/")
        print(f"  {a} / {b} = {values[a] / values[b]:.1f}x  ({np.log2(values[a] / values[b]):.1f} stops)")


if __name__ == "__main__":
    main()
