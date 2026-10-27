"""Greybox meta diff: which blocks a data change added, removed or moved, by section.

Run from the project root:
    uv run Tools/review/greybox_diff.py OLD_greybox_meta.json [NEW_greybox_meta.json]
(NEW defaults to SourceData/playground/greybox_meta.json). Blocks are keyed by (section, name, occurrence); a block
counts as changed when its centre, size or yaw moves by more than 1 mm / 0.01 deg. Also diffs `replaces`, the
routes, cameras and closures by name. Nothing is written.
"""
import json
import sys
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
old = json.loads(Path(sys.argv[1]).read_text())
new = json.loads(Path(sys.argv[2] if len(sys.argv) > 2 else ROOT / "SourceData" / "playground" / "greybox_meta.json").read_text())


def keyed(boxes):
    seen, out = Counter(), {}
    for b in boxes:
        k = (b["section"], b["name"])
        out[k + (seen[k],)] = b
        seen[k] += 1
    return out


def moved(a, b):
    return any(abs(a[f] - b[f]) > 1e-3 for f in ("x", "y", "z", "sx", "sy", "sz")) or abs(a["yaw"] - b["yaw"]) > 0.01


A, B = keyed(old["boxes"]), keyed(new["boxes"])
stats = defaultdict(lambda: Counter())
for k in A.keys() | B.keys():
    sec = k[0]
    if k not in B:
        stats[sec]["removed"] += 1
    elif k not in A:
        stats[sec]["added"] += 1
    elif moved(A[k], B[k]):
        stats[sec]["changed"] += 1
    else:
        stats[sec]["same"] += 1
print(f"boxes: {len(A)} -> {len(B)}")
for sec in sorted(stats):
    c = stats[sec]
    print(f"  {sec:16s} same {c['same']:4d}  added {c['added']:4d}  removed {c['removed']:4d}  changed {c['changed']:4d}")
for k in sorted(k for k in A.keys() | B.keys() if k in A and k in B and moved(A[k], B[k]))[:20]:
    print("  CHANGED", k)
ro, rn = set(old.get("replaces", [])), set(new.get("replaces", []))
print(f"replaces: +{sorted(rn - ro)}  -{sorted(ro - rn)}")
for part in ("routes", "cameras", "closures"):
    no, nn = {r["name"] for r in old.get(part, [])}, {r["name"] for r in new.get(part, [])}
    print(f"{part}: +{sorted(nn - no)}  -{sorted(no - nn)}")
