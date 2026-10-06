using System.Collections.Generic;
using UnityEngine;

namespace Lockdown.EditorTools
{
    // Weathering placed by logic, never by symmetry (project rule: no visible repetition or symmetry in textures,
    // dirt or decals). Every stain follows where water and dirt really go, and gets its own atlas variant, size,
    // offset, roll and flip from the house seed:
    //   - streaks under each window sill, each window rolling how stained it is (some almost clean, a few heavy);
    //   - drips under the roof edge and the parapet coping, unevenly spaced, strongest at the sag's low points,
    //     faint where an overhang shelters the wall, stopping at a lintel instead of running over glass;
    //   - rising damp in overlapping pieces, taller at wall corners and at the downpipe's foot;
    //   - road splash by how close the wall base is to traffic (street side heaviest, back lightest);
    //   - peeling whitewash in a few pieces, never along a whole wall.
    // It also dresses every opening differently (shutter angles, paint fade, dirt) and gives each wall a tone bias.
    public static partial class LockdownHouseBuilder
    {
        static void ApplyWeathering(HouseSpec spec, ImperfectionSettings imp, int seed)
        {
            var rnd = new System.Random(seed ^ 0x5eed);
            float U() => (float)rnd.NextDouble();
            float J(float amp) => (U() * 2f - 1f) * amp;
            float R(float a, float b) => Mathf.Lerp(a, b, U());
            var layers = new Dictionary<DecalKind, int>();

            void Add(DecalKind kind, string name, Facing facing, Vector3 center, float width, float height, float opacity, float roll, float draw = 120f)
            {
                int k = layers.TryGetValue(kind, out int v) ? v : 0;
                layers[kind] = k + 1;
                spec.decals.Add(new DecalSpec
                {
                    name = name, kind = kind, facing = facing, center = center, width = width, height = height,
                    opacity = Mathf.Clamp01(opacity), variant = rnd.Next(4), flipX = U() < 0.5f, roll = roll, layer = k % 2, drawDistance = draw,
                });
            }

            // Pipes that bring water down to the ground: their foot gets the worst damp.
            var downpipes = new List<Vector3>();
            foreach (var c in spec.cylinders)
                if (Mathf.Abs(c.a.x - c.b.x) < 0.01f && Mathf.Abs(c.a.z - c.b.z) < 0.01f && Mathf.Min(c.a.y, c.b.y) < 0.5f && Mathf.Abs(c.a.y - c.b.y) > 2f)
                    downpipes.Add(new Vector3(c.a.x, 0f, c.a.z));

            // One leaf per house is left much more faded than the rest: on the side the afternoon sun bakes (west,
            // then south), where paint really goes first.
            var leaves = new List<(OpeningSpec o, int leaf)>();
            foreach (var sunSide in new[] { Facing.West, Facing.South, Facing.East, Facing.North })
            {
                foreach (var w in spec.walls)
                foreach (var o in w.openings)
                    if (w.facing == sunSide && o.shutters != ShutterKind.None && o.shutterSlot == Slot.FrameWhite) { leaves.Add((o, 0)); leaves.Add((o, 1)); }
                if (leaves.Count > 0) break;
            }
            var faded = leaves.Count > 0 ? leaves[rnd.Next(leaves.Count)] : default;
            if (faded.o != null)
                foreach (var w in spec.walls)
                    if (w.openings.Contains(faded.o)) Debug.Log($"[Lockdown] {spec.id}: most faded shutter = {w.name} wall, opening at {faded.o.w0:0.0}-{faded.o.w1:0.0} m, leaf {faded.leaf}");

            foreach (var w in spec.walls)
            {
                Frame f = GetFrame(w, out float length, out var toS);
                bool plaster = w.slot == Slot.Render;
                w.dirt = R(0f, 0.45f);
                float traffic = w.facing == spec.streetFacing ? 1f : Opposite(w.facing) == spec.streetFacing ? 0.25f : 0.55f;

                // ---- Openings: each dressed and stained differently ----
                var dressed = w.openings.FindAll(o => o.kind is OpeningKind.Window or OpeningKind.GlazedDoor or OpeningKind.Door);
                foreach (var o in dressed)
                {
                    float r = U();
                    o.stain = r < 0.3f ? R(0f, 0.12f) : r > 0.78f ? R(0.75f, 1f) : R(0.3f, 0.65f);
                }
                // Side by side, one window is always much dirtier than another (a leaking sill, a sheltered one).
                var sills = dressed.FindAll(o => o.kind == OpeningKind.Window && o.sill);
                var pool = sills.Count >= 2 ? sills : dressed; // prefer windows whose sill streak shows it
                if (pool.Count >= 2)
                {
                    int dirty = rnd.Next(pool.Count), clean = (dirty + 1 + rnd.Next(pool.Count - 1)) % pool.Count;
                    pool[dirty].stain = Mathf.Max(pool[dirty].stain, R(0.8f, 1f));
                    pool[clean].stain = Mathf.Min(pool[clean].stain, R(0f, 0.1f));
                }
                foreach (var o in dressed)
                {
                    o.frameFade = 0.28f + 0.1f * o.stain + J(0.02f);
                    if (o.shutters != ShutterKind.None)
                    {
                        float open = U() < 0.2f ? R(18f, 32f) : R(3f, 14f); // most pushed back to the wall, a few left half open
                        o.shutterAngle = new[] { Mathf.Max(1f, open + J(6f)), Mathf.Max(1f, open + J(6f)) };
                        o.shutterFade = new float[2];
                        for (int i = 0; i < 2; i++)
                            o.shutterFade[i] = faded.o == o && faded.leaf == i ? 0.49f : 0.28f + 0.12f * o.stain + J(0.03f);
                    }
                    if (!plaster || o.kind != OpeningKind.Window || !o.sill) continue;

                    // Sill streaks: water runs off the sill ends and the middle, carrying dirt down the wall.
                    float s0 = Mathf.Min(toS(o.w0), toS(o.w1)), s1 = Mathf.Max(toS(o.w0), toS(o.w1));
                    float top = o.y0 - 0.05f;
                    float room = top - Mathf.Max(w.y0, 0f) - 0.25f;
                    foreach (var below in w.openings) // stop above a window or door underneath
                        if (below.y1 < top && Mathf.Min(toS(below.w1), toS(below.w0)) < s1 + 0.3f && Mathf.Max(toS(below.w1), toS(below.w0)) > s0 - 0.3f)
                            room = Mathf.Min(room, top - below.y1 - 0.15f);
                    int n = o.stain < 0.12f ? (U() < 0.5f ? 1 : 0) : o.stain > 0.75f ? 2 + rnd.Next(2) : 1;
                    for (int k = 0; k < n && room > 0.3f; k++)
                    {
                        float pick = U();
                        float s = pick < 0.35f ? s0 + R(0f, 0.2f) : pick < 0.7f ? s1 - R(0f, 0.2f) : Mathf.Lerp(s0, s1, R(0.3f, 0.7f));
                        float len = Mathf.Min(room, R(0.5f, 1.6f) * Mathf.Lerp(0.7f, 1.15f, o.stain));
                        float width = R(0.2f, 0.45f);
                        float strength = o.stain < 0.12f ? R(0.15f, 0.3f) : R(0.4f, 0.6f) + 0.4f * o.stain;
                        Add(DecalKind.Streak, $"Streak_{w.name}_{o.w0:0.0}_{k}", w.facing, f.P(s, top - len * 0.5f, 0f), width, len, strength, J(3f), 60f);
                    }
                }

                bool stone = w.slot == Slot.StoneRubble; // rubble walls: drips from the top and damp at the base, nothing to peel
                if (!plaster && w.slot != Slot.Concrete && !stone) continue;

                // ---- Drips from the wall top: under the roof edge / coping, unevenly spaced ----
                var prof = w.topFree && imp != null ? new WarpProfile
                {
                    active = true, a = f.P(0f, 0f, 0f), b = f.P(length, 0f, 0f), yPin = w.y0, yFree = w.y1, amp = imp.parapetSag, seed = Seed(w.name),
                } : default;
                var spots = new List<float>();
                if (prof.active) // the sag's low points collect the water
                {
                    float best = float.MaxValue, bestS = 0f;
                    for (float s = 0.3f; s < length - 0.3f; s += 0.05f)
                    {
                        float dy = WarpField.VerticalOf(f.P(s, w.y1, 0f), prof);
                        if (dy < best) { best = dy; bestS = s; }
                    }
                    spots.Add(bestS);
                }
                for (float s = R(0.2f, 1.2f); s < length - 0.3f; s += R(0.7f, 2.6f)) spots.Add(s);
                int d = 0;
                foreach (float s in spots)
                {
                    bool sheltered = Covered(spec, w, f.P(s, w.y1 + 0.08f, -0.7f)); // an overhang keeps the rain off
                    if (sheltered && U() < 0.6f) continue;
                    float len = R(0.6f, 2.0f) * (w.topFree ? 0.55f : 1f);
                    foreach (var o in w.openings) // a lintel turns the water aside
                    {
                        float a = Mathf.Min(toS(o.w0), toS(o.w1)) - 0.1f, b = Mathf.Max(toS(o.w0), toS(o.w1)) + 0.1f;
                        if (s > a && s < b && o.y1 < w.y1) len = Mathf.Min(len, w.y1 - o.y1 - 0.12f);
                    }
                    len = Mathf.Min(len, w.y1 - Mathf.Max(w.y0, 0f) - 0.1f);
                    if (len < 0.25f) continue;
                    float width = R(0.5f, 1.3f);
                    float strength = (sheltered ? 0.25f : R(0.4f, 0.75f)) * (w.slot == Slot.Concrete || stone ? 0.7f : 1f) + (d == 0 && prof.active ? 0.2f : 0f);
                    float sc = Mathf.Clamp(s + J(0.1f), width * 0.5f, length - width * 0.5f);
                    Add(DecalKind.Grime, $"Drip_{w.name}_{d++}", w.facing, f.P(sc, w.y1 - 0.02f - len * 0.5f, 0f), width, len, strength, J(2f));
                }

                // ---- Base of the wall: rising damp, road splash, peeling whitewash ----
                if (!plaster && !stone) continue;
                int p = 0;
                for (float s = R(-0.3f, 0.2f); s < length; p++)
                {
                    float width = R(1.4f, 3.0f);
                    float mid = Mathf.Clamp(s + width * 0.5f, 0f, length);
                    float ground = GroundAt(spec, f.P(mid, 0f, -0.15f));
                    if (ground >= w.y1 - 0.5f || ground < w.y0 - 0.05f || Covered(spec, w, f.P(mid, ground + 0.3f, -0.05f))) { s += width; continue; }
                    Vector3 at = f.P(mid, 0f, 0f);
                    float wet = 0f; // wall corners and the downpipe foot stay wet longest
                    if (mid < 1.2f || mid > length - 1.2f) wet = 0.5f;
                    foreach (var dp in downpipes)
                        wet = Mathf.Max(wet, 1f - new Vector2(at.x - dp.x, at.z - dp.z).magnitude / 2f);
                    float h = R(0.55f, 1.0f) + 0.6f * wet;
                    float x0 = Mathf.Max(s, 0f), x1 = Mathf.Min(s + width, length);
                    if (x1 - x0 > 0.3f)
                        Add(DecalKind.Damp, $"Damp_{w.name}_{p}", w.facing, f.P((x0 + x1) * 0.5f, ground - 0.05f + h * 0.5f, 0f), x1 - x0, h,
                            R(0.45f, 0.7f) + 0.25f * wet, 0f);
                    // Splash from the road: a low, darker band, only where traffic passes close.
                    if (U() < 0.4f + 0.6f * traffic)
                    {
                        float sh = R(0.3f, 0.5f);
                        Add(DecalKind.Damp, $"Splash_{w.name}_{p}", w.facing, f.P(Mathf.Clamp(mid + J(0.4f), 0.4f, length - 0.4f), ground - 0.04f + sh * 0.5f, 0f),
                            Mathf.Min(R(1.0f, 2.2f), length), sh, (0.3f + 0.6f * traffic) * R(0.75f, 1f), 0f);
                    }
                    s += width - R(0.3f, 0.6f);
                }
                // Peeling: one or two patches per wall, never a band along it.
                if (plaster && w.y0 < 0.5f)
                {
                    int count = length > 4f ? 1 + rnd.Next(2) : rnd.Next(2);
                    for (int k = 0; k < count; k++)
                    {
                        float width = Mathf.Min(R(1.0f, 2.4f), length - 0.4f), h = R(0.7f, 1.4f);
                        float s = R(width * 0.5f + 0.1f, length - width * 0.5f - 0.1f);
                        float ground = GroundAt(spec, f.P(s, 0f, -0.15f));
                        if (ground >= w.y1 - 1f || Covered(spec, w, f.P(s, ground + 0.3f, -0.05f))) continue;
                        Add(DecalKind.Peel, $"Peel_{w.name}_{k}", w.facing, f.P(s, ground + h * 0.5f + R(0f, 0.15f), 0f), width, h, R(0.55f, 0.95f), J(2f));
                    }
                }
            }

            // ---- Splash on low street-side kerbs and steps ----
            foreach (var b in spec.boxes)
            {
                if (!b.collide || b.max.y > 0.5f || b.max.y < 0.05f) continue;
                Facing facing = spec.streetFacing;
                bool alongX = facing is Facing.South or Facing.North;
                if ((alongX ? b.max.x - b.min.x : b.max.z - b.min.z) < 1.5f) continue;
                float plane = facing switch { Facing.South => b.min.z, Facing.North => b.max.z, Facing.West => b.min.x, _ => b.max.x };
                float lo = alongX ? b.min.x : b.min.z, hi = alongX ? b.max.x : b.max.z;
                float mc = (lo + hi) * 0.5f, my = (b.min.y + b.max.y) * 0.5f;
                float outSign = facing is Facing.South or Facing.West ? -1f : 1f;
                Vector3 front = alongX ? new Vector3(mc, my, plane + outSign * 0.05f) : new Vector3(plane + outSign * 0.05f, my, mc);
                bool hidden = false; // another box (a kerb) stands in front of this face
                foreach (var o in spec.boxes) if (o != b && Inside(front, o.min, o.max)) hidden = true;
                if (hidden) continue;
                int k = 0;
                for (float s = lo + R(0f, 0.5f); s < hi - 0.4f; s += R(1.2f, 2.4f))
                {
                    float width = Mathf.Min(R(1.2f, 2.6f), hi - s);
                    float c = s + width * 0.5f, h = b.max.y - Mathf.Max(b.min.y, 0f) + 0.05f;
                    Vector3 at = alongX ? new Vector3(c, Mathf.Max(b.min.y, 0f) + h * 0.5f - 0.03f, plane) : new Vector3(plane, Mathf.Max(b.min.y, 0f) + h * 0.5f - 0.03f, c);
                    Add(DecalKind.Damp, $"Splash_{b.name}_{k++}", facing, at, width, h, R(0.6f, 0.95f), 0f);
                }
            }
        }

        static Facing Opposite(Facing f) => f switch
        {
            Facing.South => Facing.North, Facing.North => Facing.South, Facing.West => Facing.East, _ => Facing.West,
        };

        // Height of whatever the wall stands on at this point (street 0, or a porch / terrace floor).
        static float GroundAt(HouseSpec spec, Vector3 p)
        {
            float g = 0f;
            foreach (var b in spec.boxes)
                if (b.collide && b.max.y < 1.2f && p.x > b.min.x - 1e-3f && p.x < b.max.x + 1e-3f && p.z > b.min.z - 1e-3f && p.z < b.max.z + 1e-3f)
                    g = Mathf.Max(g, b.max.y);
            return g;
        }
    }
}
