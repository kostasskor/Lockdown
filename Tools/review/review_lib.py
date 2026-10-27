"""Shared loader for the layout reviews (docs/reviews/review_2_task9.md).

Reuses the ground, blocker, line-of-sight and pathing code of Tools/review/playground_checks.py (same raster, same
occluders, same eye heights) by running it silently and handing back its namespace. Nothing in the project is written.

    from review_lib import load
    G = load()                      # current scene data
    G = load("path/to/other/Tools/review/playground_checks.py")   # another tree (e.g. a pre-Task-9 copy)
"""
import contextlib
import io
import runpy
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def load(checks=None):
    checks = Path(checks) if checks else ROOT / "Tools" / "review" / "playground_checks.py"
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        g = runpy.run_path(str(checks), run_name="checks")
    g["_checks_output"] = buf.getvalue()
    return g
