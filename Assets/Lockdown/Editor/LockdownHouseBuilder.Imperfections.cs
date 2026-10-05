using System.Collections.Generic;
using UnityEngine;

namespace Lockdown.EditorTools
{
    // Hand-built imperfections, part 2: what the generator changes in the house's data before building it.
    // Openings come out slightly different in size and position, the pergola is cut and laid by hand, and small
    // weathering decals are placed where real houses get them: chipped plaster on exposed corners, hairline cracks
    // from opening corners, the parapet top and the slab edge, rust stains under the pergola's nails.
    // Everything is seeded from the house id, so a rebuild gives the same house.
    public static partial class LockdownHouseBuilder
    {
        static int Seed(string text)
        {
            unchecked
            {
                int h = 17;
                foreach (char c in text) h = h * 31 + c;
                return h & 0x7fffffff;
            }
        }

        static void ApplyImperfections(HouseSpec spec, ImperfectionSettings imp, int seed)
        {
            var rnd = new System.Random(seed);
            float U() => (float)rnd.NextDouble();
            float J(float amp) => (U() * 2f - 1f) * amp;

            // ---- Openings: hand-set frames, none quite the same; thresholds stay put so doors stay walkable ----
            foreach (var w in spec.walls)
            foreach (var o in w.openings)
            {
                if (o.kind is not (OpeningKind.Window or OpeningKind.GlazedDoor or OpeningKind.Door or OpeningKind.Void)) continue;
                o.w0 += J(imp.openingJitter);
                o.w1 += J(imp.openingJitter);
                o.y1 += J(imp.openingJitter);
                if (o.kind == OpeningKind.Window) o.y0 += J(imp.openingJitter * 0.7f);
            }

            // ---- Pergola: uneven rafter spacing, a few twisted, each its own shade ----
            foreach (var p in spec.pergolas)
            {
                int n = Mathf.Max(2, Mathf.RoundToInt((p.x1 - p.x0 - 0.1f) / p.rafterSpacing) + 1);
                p.rafterX = new List<float>();
                p.rafterTwist = new List<float>();
                p.rafterShade = new List<float>();
                for (int i = 0; i < n; i++)
                {
                    float x = Mathf.Lerp(p.x0 + 0.05f, p.x1 - 0.05f, i / (n - 1f));
                    if (i > 0 && i < n - 1) x += J(imp.rafterSpacingJitter);
                    p.rafterX.Add(x);
                    p.rafterTwist.Add(U() < imp.twistedRafterShare ? Mathf.Sign(J(1f)) * Mathf.Lerp(0.5f, 1f, U()) * imp.rafterTwistDeg : 0f);
                    float r = U();
                    p.rafterShade.Add(r < 0.15f ? 0.3f : r > 0.85f ? 0.95f : 0.6f + 0.2f * U()); // mostly brown, a few greyer or darker
                }
                // Rusty nail stains: down the beam's street face under every rafter, and on the rafter sides.
                for (int i = 0; i < n; i++)
                {
                    float x = p.rafterX[i];
                    spec.decals.Add(new DecalSpec
                    {
                        name = $"Nail_{p.name}_beam{i}", kind = DecalKind.Rust, facing = Facing.South, center = new Vector3(x, p.yUnder - 0.07f, p.z0),
                        width = 0.05f, height = 0.12f, opacity = 0.75f, drawDistance = 25f,
                    });
                    bool east = U() < 0.5f;
                    spec.decals.Add(new DecalSpec
                    {
                        name = $"Nail_{p.name}_rafter{i}", kind = DecalKind.Rust, facing = east ? Facing.East : Facing.West,
                        center = new Vector3(x + (east ? 0.04f : -0.04f), p.yUnder + 0.1f, Mathf.Lerp(p.z0 + 0.3f, p.z1 - 0.3f, U())),
                        width = 0.06f, height = 0.1f, opacity = 0.7f, drawDistance = 25f,
                    });
                }
            }

            foreach (var w in spec.walls)
            {
                if (w.slot != Slot.Render) continue; // bare block (rooftop room) has no plaster to chip or crack
                Frame f = GetFrame(w, out float length, out var toS);

                // ---- Chipped plaster on exposed corners, projected diagonally onto both faces ----
                foreach (var (sEnd, dir) in new[] { (0f, -1f), (length, 1f) })
                {
                    var runs = new List<(float a, float b)>();
                    float? start = null;
                    for (float y = w.y0 + 0.6f; y <= w.y1 - 0.25f; y += 0.1f)
                    {
                        bool exposed = EndExposed(spec, w, f, sEnd, dir, y);
                        if (exposed && start == null) start = y;
                        if (!exposed && start != null) { runs.Add((start.Value, y - 0.1f)); start = null; }
                    }
                    if (start != null) runs.Add((start.Value, w.y1 - 0.25f));
                    Vector3 outward = (f.outward + f.along * dir).normalized;
                    foreach (var (a, b) in runs)
                    {
                        int count = Mathf.FloorToInt((b - a) / 3f * imp.chipsPer3m + U());
                        for (int k = 0; k < count; k++)
                        {
                            float size = 0.28f + 0.14f * U();
                            spec.decals.Add(new DecalSpec
                            {
                                name = $"Chip_{w.name}_{(dir < 0 ? "start" : "end")}_{k}", kind = DecalKind.Chip, direction = outward,
                                center = f.P(sEnd, Mathf.Lerp(a, b, U()), 0f), width = size, height = size, depth = size,
                                variant = rnd.Next(4), flipX = U() < 0.5f, angleFadeStart = 65f, angleFadeEnd = 89f, drawDistance = 35f,
                            });
                        }
                    }
                }

                // ---- Hairline cracks from opening corners, running diagonally away from the opening ----
                int c = 0;
                foreach (var o in w.openings)
                {
                    if (o.kind is not (OpeningKind.Window or OpeningKind.GlazedDoor or OpeningKind.Door or OpeningKind.Void)) continue;
                    float s0 = Mathf.Min(toS(o.w0), toS(o.w1)), s1 = Mathf.Max(toS(o.w0), toS(o.w1));
                    float m = o.surround ? o.surroundWidth : 0f;
                    var corners = new List<(float s, float y, float sx, float sy)> { (s1 + m, o.y1 + m, 1f, 1f), (s0 - m, o.y1 + m, -1f, 1f) };
                    if (o.kind == OpeningKind.Window)
                    {
                        float yb = o.sill ? o.y0 - 0.05f : o.y0 - m;
                        float sb = o.sill ? 0.06f : m;
                        corners.Add((s1 + sb, yb, 1f, -1f));
                        corners.Add((s0 - sb, yb, -1f, -1f));
                    }
                    foreach (var (cs, cy, sx, sy) in corners)
                    {
                        if (U() >= imp.crackChance) continue;
                        float size = 0.2f + 0.15f * U();
                        spec.decals.Add(new DecalSpec
                        {
                            name = $"Crack_{w.name}_{c++}", kind = DecalKind.Crack, facing = w.facing,
                            center = f.P(cs + sx * size * 0.5f, cy + sy * size * 0.5f, 0f), width = size, height = size, depth = 0.1f,
                            variant = rnd.Next(4), flipX = sx < 0f, flipY = sy < 0f, opacity = 0.9f, drawDistance = 30f,
                        });
                    }
                }

                // ---- Cracks down from a free parapet top ----
                if (w.topFree)
                {
                    int n = Mathf.Max(1, Mathf.RoundToInt(length / 3f));
                    for (int k = 0; k < n; k++)
                    {
                        float size = 0.2f + 0.1f * U();
                        spec.decals.Add(new DecalSpec
                        {
                            name = $"Crack_{w.name}_top{k}", kind = DecalKind.Crack, facing = w.facing,
                            center = f.P(Mathf.Lerp(0.4f, length - 0.4f, (k + U()) / n), w.y1 - size * 0.5f, 0f), width = size, height = size, depth = 0.1f,
                            variant = rnd.Next(4), flipX = U() < 0.5f, flipY = true, opacity = 0.9f, drawDistance = 30f,
                        });
                    }
                }
            }

            // ---- Cracks in slab edges (sag groups): on the face away from the house ----
            var done = new HashSet<string>();
            Vector3 centre = HouseCentre(spec);
            foreach (var b in spec.boxes)
            {
                if (b.sag != SagKind.Bottom || string.IsNullOrEmpty(b.sagGroup) || !done.Add(b.sagGroup)) continue;
                Vector3 min = b.min, max = b.max;
                foreach (var o in spec.boxes) if (o.sagGroup == b.sagGroup) { min = Vector3.Min(min, o.min); max = Vector3.Max(max, o.max); }
                bool alongX = max.x - min.x >= max.z - min.z;
                float len = alongX ? max.x - min.x : max.z - min.z;
                int n = Mathf.Max(1, Mathf.RoundToInt(len / 3f));
                for (int k = 0; k < n; k++)
                {
                    float t = Mathf.Lerp(0.05f, 0.95f, (k + U()) / n), size = 0.25f;
                    Vector3 at;
                    Facing facing;
                    if (alongX)
                    {
                        bool south = Mathf.Abs(min.z - centre.z) > Mathf.Abs(max.z - centre.z);
                        at = new Vector3(Mathf.Lerp(min.x, max.x, t), b.max.y - 0.15f, south ? b.min.z : b.max.z);
                        facing = south ? Facing.South : Facing.North;
                    }
                    else
                    {
                        bool west = Mathf.Abs(min.x - centre.x) > Mathf.Abs(max.x - centre.x);
                        at = new Vector3(west ? b.min.x : b.max.x, b.max.y - 0.15f, Mathf.Lerp(min.z, max.z, t));
                        facing = west ? Facing.West : Facing.East;
                    }
                    spec.decals.Add(new DecalSpec
                    {
                        name = $"Crack_{b.sagGroup}_{k}", kind = DecalKind.Crack, facing = facing, center = at, width = size, height = size, depth = 0.1f,
                        variant = rnd.Next(4), flipX = U() < 0.5f, flipY = U() < 0.5f, opacity = 0.9f, drawDistance = 30f,
                    });
                }
            }
        }

        static Vector3 HouseCentre(HouseSpec spec)
        {
            Vector3 sum = Vector3.zero;
            foreach (var w in spec.walls)
            {
                var g = GetFrame(w, out float len, out _);
                sum += (g.P(0f, w.y0, 0f) + g.P(len, w.y1, w.thickness)) * 0.5f;
            }
            return spec.walls.Count > 0 ? sum / spec.walls.Count : Vector3.zero;
        }

        // A sag shared by every box in the same group, along the group's long horizontal axis.
        static WarpProfile BoxSag(HouseSpec spec, BoxSpec b, ImperfectionSettings imp)
        {
            if (b.sag == SagKind.None) return default;
            Vector3 min = b.min, max = b.max;
            if (!string.IsNullOrEmpty(b.sagGroup))
                foreach (var o in spec.boxes) if (o.sagGroup == b.sagGroup) { min = Vector3.Min(min, o.min); max = Vector3.Max(max, o.max); }
            bool alongX = max.x - min.x >= max.z - min.z;
            float mx = (min.x + max.x) * 0.5f, mz = (min.z + max.z) * 0.5f;
            bool top = b.sag == SagKind.Top;
            return new WarpProfile
            {
                active = true,
                a = alongX ? new Vector3(min.x, 0f, mz) : new Vector3(mx, 0f, min.z),
                b = alongX ? new Vector3(max.x, 0f, mz) : new Vector3(mx, 0f, max.z),
                yPin = top ? min.y : max.y, yFree = top ? max.y : min.y,
                amp = top ? imp.roofEdgeSag : imp.slabSag,
                seed = Seed(string.IsNullOrEmpty(b.sagGroup) ? b.name : b.sagGroup),
            };
        }

        // A wall end is exposed (a convex corner) at height y if nothing continues past it and nothing stands
        // against its outer face next to it.
        static bool EndExposed(HouseSpec spec, WallSpec w, Frame f, float sEnd, float dir, float y) =>
            !Covered(spec, w, f.P(sEnd + dir * 0.05f, y, 0.02f)) && !Covered(spec, w, f.P(sEnd - dir * 0.05f, y, -0.05f));

        // Heights between lo and hi where a wall end turns from covered to exposed or back.
        static List<float> ExposureBreaks(HouseSpec spec, WallSpec w, Frame f, float sEnd, float dir, float lo, float hi)
        {
            var res = new List<float>();
            const float step = 0.05f;
            bool prev = EndExposed(spec, w, f, sEnd, dir, lo + 1e-3f);
            for (float y = lo + step; y < hi; y += step)
            {
                bool cur = EndExposed(spec, w, f, sEnd, dir, y);
                if (cur == prev) continue;
                float a = y - step, b = y; // bisect to the exact edge of whatever covers it
                for (int i = 0; i < 16; i++)
                {
                    float m = (a + b) * 0.5f;
                    if (EndExposed(spec, w, f, sEnd, dir, m) == prev) a = m; else b = m;
                }
                float yb = Mathf.Round(b * 1000f) / 1000f;
                if (yb > lo + 1e-3f && yb < hi - 1e-3f) res.Add(yb);
                prev = cur;
            }
            return res;
        }

        static bool Covered(HouseSpec spec, WallSpec self, Vector3 p)
        {
            foreach (var o in spec.walls)
            {
                if (o == self) continue;
                var g = GetFrame(o, out float len, out _);
                if (Inside(p, g.P(0f, o.y0, 0f), g.P(len, o.y1, o.thickness))) return true;
            }
            foreach (var b in spec.boxes) if (Inside(p, b.min, b.max)) return true;
            foreach (var st in spec.stairs) if (Inside(p, new Vector3(st.x0, st.yBase - 0.2f, st.zStart), new Vector3(st.x1, st.yTop, st.zTop))) return true;
            foreach (var q in spec.quoins) // dressed stones already cover that corner
                if (Inside(p, q.corner - (q.dirA + q.dirB) * 0.06f + Vector3.up * q.y0, q.corner + (q.dirA + q.dirB) * 0.5f + Vector3.up * q.y1)) return true;
            return false;
        }

        static bool Inside(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 min = Vector3.Min(a, b), max = Vector3.Max(a, b);
            const float e = 1e-3f;
            return p.x > min.x - e && p.x < max.x + e && p.y > min.y - e && p.y < max.y + e && p.z > min.z - e && p.z < max.z + e;
        }
    }
}
