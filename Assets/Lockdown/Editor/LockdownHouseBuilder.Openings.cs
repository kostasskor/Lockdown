using System.Collections.Generic;
using UnityEngine;

namespace Lockdown.EditorTools
{
    // Doors, glass and what is seen through them, after the village's real openings (03):
    //   - door leaves built from real door anatomy, by style: the white aluminium balcony door (profiles, recessed lower
    //     panel, rubber gasket, lever handle, hinges, threshold), the brown wooden glazed door (stiles and rails with
    //     upright grain, raised lower panels, glazing bars), the ledged plank door (loose boards, rusty strap hinges, hasp);
    //   - glass as one outward-facing pane (a transparent material that keeps its reflections), never a box;
    //   - door leaves stand open, swung inward about their hinges by a seeded angle, with an old brass lever handle on
    //     both faces (backplate, keyhole, screws, rose, curved lever sagging on a tired spring);
    //   - optional (OpeningSpec.interior, off for now: nothing inside the houses yet): a sealed shallow room behind the
    //     opening, fitted inside the house, darker with depth (vertex G), with a seeded curtain and sometimes a wardrobe.
    public static partial class LockdownHouseBuilder
    {
        // Rooms placed so far in this build (world-space boxes), so later rooms fit around them.
        static readonly List<(Vector3 min, Vector3 max)> Rooms = new();

        static DoorStyle Style(OpeningSpec o) => o.doorStyle != DoorStyle.Auto ? o.doorStyle : o.kind switch
        {
            OpeningKind.GlazedDoor => DoorStyle.AluminiumGlazed,
            OpeningKind.Door => DoorStyle.WoodPlank,
            OpeningKind.Void => DoorStyle.Open,
            _ => DoorStyle.Auto,
        };

        // ---------- Frame-space helpers (s along the wall, y up, d inward from the outer face) ----------

        static void BoxP(MeshPart part, Frame f, float s0, float s1, float y0, float y1, float d0, float d1, Slot slot)
        {
            Vector3 a = f.P(s0, y0, d0), b = f.P(s1, y1, d1);
            part.Box((a + b) * 0.5f, new Vector3(Mathf.Abs(s1 - s0), Mathf.Abs(y1 - y0), Mathf.Abs(d1 - d0)), f.Rot, slot);
        }

        static void ChamferP(MeshPart part, Frame f, float s0, float s1, float y0, float y1, float d0, float d1, float ch, Slot slot)
        {
            Vector3 a = f.P(s0, y0, d0), b = f.P(s1, y1, d1);
            part.ChamferBox((a + b) * 0.5f, new Vector3(Mathf.Abs(s1 - s0), Mathf.Abs(y1 - y0), Mathf.Abs(d1 - d0)), f.Rot, ch, slot);
        }

        // The leaf's own frame, swung open inward (into the house) about the vertical hinge line at (hingeS, pivotD).
        // Everything built in it in frame coordinates comes out rotated with the leaf.
        static Frame LeafFrame(Frame f, OpeningSpec o, float hingeS, float pivotD)
        {
            float a = o.swing * Mathf.Deg2Rad;
            Vector3 inward = -f.outward;
            Vector3 along = o.hingeLeft ? Mathf.Cos(a) * f.along + Mathf.Sin(a) * inward : Mathf.Cos(a) * f.along - Mathf.Sin(a) * inward;
            var g = new Frame { along = along, outward = Quaternion.FromToRotation(f.along, along) * f.outward };
            g.origin = f.P(hingeS, 0f, pivotD) - g.along * hingeS + g.outward * pivotD;
            return g;
        }

        static void QuadP(MeshPart part, Frame f, Vector2 sy0, Vector2 sy1, Vector2 sy2, Vector2 sy3, float d0, float d1, float d2, float d3, Vector3 facing, Slot slot) =>
            part.Quad(f.P(sy0.x, sy0.y, d0), f.P(sy1.x, sy1.y, d1), f.P(sy2.x, sy2.y, d2), f.P(sy3.x, sy3.y, d3), facing, slot);

        static void ColW(HouseMesh hm, Frame f, float s0, float s1, float y0, float y1, float d0, float d1)
        {
            Vector3 a = f.P(s0, y0, d0), b = f.P(s1, y1, d1);
            hm.Collision.Box((a + b) * 0.5f, Abs(b - a), Quaternion.identity, Slot.Render);
        }

        // One pane in both LODs: clean or dusty by the seed. The glass material is single-sided, so the pane is two quads,
        // one facing out and one facing the room 2 mm further in, so it shows from both sides.
        static void GlassW(HouseMesh hm, Frame f, OpeningSpec o, float s0, float s1, float y0, float y1, float d)
        {
            Slot slot = o.dustyGlass ? Slot.GlassDusty : Slot.Glass;
            foreach (var part in new[] { hm.Lod0, hm.Lod1 })
            {
                part.Quad(f.P(s0, y0, d), f.P(s1, y0, d), f.P(s1, y1, d), f.P(s0, y1, d), f.outward, slot);
                float di = d + 0.002f;
                part.Quad(f.P(s0, y0, di), f.P(s1, y0, di), f.P(s1, y1, di), f.P(s0, y1, di), -f.outward, slot);
            }
        }

        // Old brass lever handle: a long chamfered backplate with its keyhole and two screws, a round rose, the neck, and a
        // lever curving down and back toward the door, ending in a small knob; the lever sags on its tired spring.
        // side -1 = outer face (protrudes outward), +1 = inner face. Polished where hands hold it, tarnished elsewhere.
        static void Handle(MeshPart p, Frame f, OpeningSpec o, float s, float y, float face, float side, float toHinge)
        {
            float D(float k) => face + side * k;
            Vector3 P(float ss, float yy, float k) => f.P(ss, yy, D(k));
            var keep = p.ColorFn;
            p.ColorFn = (q, _) => new Color(0.7f, 0f, 0f, 1f);
            ChamferP(p, f, s - 0.023f, s + 0.023f, y - 0.14f, y + 0.07f, D(0f), D(0.007f), 0.0025f, Slot.Brass);
            p.ColorFn = (q, _) => new Color(0.8f, 0f, 0f, 1f);
            foreach (float yy in new[] { y + 0.055f, y - 0.125f })
                p.Cylinder(P(s, yy, 0.0065f), P(s, yy, 0.0085f), 0.0032f, 6, Slot.Brass);
            float keyY = y - 0.09f;
            BoxP(p, f, s - 0.0028f, s + 0.0028f, keyY - 0.016f, keyY, D(0.0066f), D(0.0076f), Slot.Dark);
            p.Cylinder(P(s, keyY + 0.002f, 0.0066f), P(s, keyY + 0.002f, 0.0076f), 0.0052f, 8, Slot.Dark);
            p.ColorFn = (q, _) => new Color(0.5f, 0f, 0f, 1f);
            p.Cylinder(P(s, y, 0.0068f), P(s, y, 0.016f), 0.014f, 10, Slot.Brass);
            p.Cylinder(P(s, y, 0.016f), P(s, y, 0.045f), 0.0075f, 8, Slot.Brass);
            p.ColorFn = (q, _) => new Color(0.25f, 0f, 0f, 1f);
            float dr = o.handleDroop;
            var pts = new[] { P(s, y, 0.045f), P(s + toHinge * 0.03f, y - 0.002f - dr * 0.25f, 0.052f),
                              P(s + toHinge * 0.075f, y - 0.006f - dr * 0.6f, 0.054f), P(s + toHinge * 0.115f, y - 0.012f - dr, 0.046f) };
            float[] r = { 0.0088f, 0.008f, 0.0074f };
            for (int k = 0; k < 3; k++) p.Cylinder(pts[k], pts[k + 1], r[k], 8, Slot.Brass);
            Vector3 dir = (pts[3] - pts[2]).normalized;
            p.Cylinder(pts[3] - dir * 0.004f, pts[3] + dir * 0.008f, 0.0095f, 8, Slot.Brass);
            p.ColorFn = keep;
        }

        // Hinge knuckles on the hinge line (the leaf turns about them).
        static void Hinges(MeshPart p, Frame f, float s, float y0, float y1, float pivotD, int count, Slot slot)
        {
            for (int i = 0; i < count; i++)
            {
                float y = Mathf.Lerp(y0 + 0.22f, y1 - 0.22f, count == 1 ? 0.5f : i / (count - 1f));
                p.Cylinder(f.P(s, y - 0.05f, pivotD), f.P(s, y + 0.05f, pivotD), 0.011f, 8, slot);
                p.Cylinder(f.P(s, y - 0.056f, pivotD), f.P(s, y - 0.05f, pivotD), 0.007f, 6, slot); // pin head
            }
        }

        // ---------- Doors ----------

        // White aluminium balcony door (03): leaf of 7 cm stiles, 7/10/14 cm rails, a smooth infill panel set back below
        // the mid rail, glass above in a black rubber gasket, old brass lever both sides; aluminium threshold and hinges.
        // The leaf stands open inward; the threshold and hinge knuckles stay on the fixed frame.
        static void BuildAluminiumDoor(HouseMesh hm, Frame f, OpeningSpec o, float s0, float s1, float ls0, float ls1, float y0, float ly1, float d0, float d1)
        {
            float ly0 = y0 + 0.02f;
            float e0 = d0 + 0.008f, e1 = d0 + 0.052f;
            const float st = 0.07f, topRail = 0.07f, midRail = 0.10f, botRail = 0.14f;
            float ym = y0 + 0.95f;
            float hingeS = o.hingeLeft ? ls0 : ls1;
            var p0 = hm.Lod0;
            BoxP(p0, f, s0 - 0.01f, s1 + 0.01f, y0 + 0.001f, ly0, d0 - 0.025f, d1 + 0.01f, Slot.Hardware); // aluminium threshold
            Hinges(p0, f, hingeS, ly0, ly1, e1, 3, Slot.Hardware);
            Frame g = LeafFrame(f, o, hingeS, e1);
            p0.UvTurn = true;
            BoxP(p0, g, ls0, ls0 + st, ly0, ly1, e0, e1, Slot.Aluminium);
            BoxP(p0, g, ls1 - st, ls1, ly0, ly1, e0, e1, Slot.Aluminium);
            p0.UvTurn = false;
            BoxP(p0, g, ls0 + st, ls1 - st, ly1 - topRail, ly1, e0, e1, Slot.Aluminium);
            BoxP(p0, g, ls0 + st, ls1 - st, ym - midRail * 0.5f, ym + midRail * 0.5f, e0, e1, Slot.Aluminium);
            BoxP(p0, g, ls0 + st, ls1 - st, ly0, ly0 + botRail, e0, e1, Slot.Aluminium);
            BoxP(p0, g, ls0 + st, ls1 - st, ly0 + botRail, ym - midRail * 0.5f, e0 + 0.014f, e0 + 0.024f, Slot.Aluminium); // infill panel
            // Glass in its gasket (6 mm black rubber lip on the outer side).
            float g0 = ls0 + st, g1 = ls1 - st, gy0 = ym + midRail * 0.5f, gy1 = ly1 - topRail;
            const float lip = 0.006f;
            BoxP(p0, g, g0, g0 + lip, gy0, gy1, e0 + 0.012f, e0 + 0.02f, Slot.Dark);
            BoxP(p0, g, g1 - lip, g1, gy0, gy1, e0 + 0.012f, e0 + 0.02f, Slot.Dark);
            BoxP(p0, g, g0 + lip, g1 - lip, gy0, gy0 + lip, e0 + 0.012f, e0 + 0.02f, Slot.Dark);
            BoxP(p0, g, g0 + lip, g1 - lip, gy1 - lip, gy1, e0 + 0.012f, e0 + 0.02f, Slot.Dark);
            GlassW(hm, g, o, g0, g1, gy0, gy1, e0 + 0.022f);
            float lockS = o.hingeLeft ? ls1 - 0.035f : ls0 + 0.035f, toHinge = o.hingeLeft ? -1f : 1f;
            Handle(p0, g, o, lockS, y0 + 1.05f, e0, -1f, toHinge);
            Handle(p0, g, o, lockS, y0 + 1.05f, e1, 1f, toHinge);
            BoxP(hm.Collision, g, ls0, ls1, ly0, ly1, e0, e1, Slot.Render); // the open leaf blocks the walker
            // LOD1: the leaf as four boxes around the same pane.
            var p1 = hm.Lod1;
            BoxP(p1, g, ls0, ls1, ly0, gy0, e0, e1, Slot.Aluminium);
            BoxP(p1, g, ls0, ls1, gy1, ly1, e0, e1, Slot.Aluminium);
            BoxP(p1, g, ls0, g0, gy0, gy1, e0, e1, Slot.Aluminium);
            BoxP(p1, g, g1, ls1, gy0, gy1, e0, e1, Slot.Aluminium);
        }

        // Brown wooden glazed door (03): 10 cm stiles with the grain upright, 10/16/20 cm rails, two raised panels below
        // the lock rail, the glass above split by a glazing bar each way, old brass lever both sides and brass hinges.
        static void BuildWoodGlazedDoor(HouseMesh hm, Frame f, OpeningSpec o, float ls0, float ls1, float y0, float ly1, float d0)
        {
            float ly0 = y0 + 0.006f;
            float e0 = d0 + 0.008f, e1 = d0 + 0.052f;
            const float st = 0.10f, topRail = 0.10f, lockRail = 0.16f, botRail = 0.20f, muntin = 0.08f, bar = 0.035f;
            float ym = y0 + 1.0f;
            float hingeS = o.hingeLeft ? ls0 : ls1;
            var p0 = hm.Lod0;
            Hinges(p0, f, hingeS, ly0, ly1, e1, 3, Slot.Brass);
            Frame g = LeafFrame(f, o, hingeS, e1);
            p0.UvTurn = true;
            BoxP(p0, g, ls0, ls0 + st, ly0, ly1, e0, e1, Slot.Wood);
            BoxP(p0, g, ls1 - st, ls1, ly0, ly1, e0, e1, Slot.Wood);
            float mid = (ls0 + ls1) * 0.5f;
            BoxP(p0, g, mid - muntin * 0.5f, mid + muntin * 0.5f, ly0 + botRail, ym - lockRail * 0.5f, e0, e1, Slot.Wood);
            p0.UvTurn = false;
            BoxP(p0, g, ls0 + st, ls1 - st, ly1 - topRail, ly1, e0, e1, Slot.Wood);
            BoxP(p0, g, ls0 + st, ls1 - st, ym - lockRail * 0.5f, ym + lockRail * 0.5f, e0, e1, Slot.Wood);
            BoxP(p0, g, ls0 + st, ls1 - st, ly0, ly0 + botRail, e0, e1, Slot.Wood);
            // Two fields, each a thin flat panel with a raised, chamfered centre on both faces.
            float py0 = ly0 + botRail, py1 = ym - lockRail * 0.5f;
            foreach (var (a, b) in new[] { (ls0 + st, mid - muntin * 0.5f), (mid + muntin * 0.5f, ls1 - st) })
            {
                BoxP(p0, g, a, b, py0, py1, e0 + 0.016f, e0 + 0.026f, Slot.Wood);
                ChamferP(p0, g, a + 0.045f, b - 0.045f, py0 + 0.045f, py1 - 0.045f, e0 + 0.004f, e0 + 0.016f, 0.01f, Slot.Wood);
                ChamferP(p0, g, a + 0.045f, b - 0.045f, py0 + 0.045f, py1 - 0.045f, e0 + 0.026f, e0 + 0.038f, 0.01f, Slot.Wood);
            }
            float g0 = ls0 + st, g1 = ls1 - st, gy0 = ym + lockRail * 0.5f, gy1 = ly1 - topRail;
            GlassW(hm, g, o, g0, g1, gy0, gy1, e0 + 0.026f);
            float gm = (gy0 + gy1) * 0.5f;
            p0.UvTurn = true;
            BoxP(p0, g, mid - bar * 0.5f, mid + bar * 0.5f, gy0, gy1, e0 + 0.006f, e0 + 0.046f, Slot.Wood);
            p0.UvTurn = false;
            BoxP(p0, g, g0, mid - bar * 0.5f, gm - bar * 0.5f, gm + bar * 0.5f, e0 + 0.006f, e0 + 0.046f, Slot.Wood);
            BoxP(p0, g, mid + bar * 0.5f, g1, gm - bar * 0.5f, gm + bar * 0.5f, e0 + 0.006f, e0 + 0.046f, Slot.Wood);
            float lockS = o.hingeLeft ? ls1 - 0.05f : ls0 + 0.05f, toHinge = o.hingeLeft ? -1f : 1f;
            Handle(p0, g, o, lockS, y0 + 1.05f, e0, -1f, toHinge);
            Handle(p0, g, o, lockS, y0 + 1.05f, e1, 1f, toHinge);
            BoxP(hm.Collision, g, ls0, ls1, ly0, ly1, e0, e1, Slot.Render);
            var p1 = hm.Lod1;
            BoxP(p1, g, ls0, ls1, ly0, gy0, e0, e1, Slot.Wood);
            BoxP(p1, g, ls0, ls1, gy1, ly1, e0, e1, Slot.Wood);
            BoxP(p1, g, ls0, g0, gy0, gy1, e0, e1, Slot.Wood);
            BoxP(p1, g, g1, ls1, gy0, gy1, e0, e1, Slot.Wood);
        }

        // Ledged plank door: loose vertical boards (~11 cm, 4 mm gaps) each faded, wetted and set a little differently,
        // ledges behind, two rusty strap hinges, a hasp and an old brass lever; stands open inward.
        static void BuildPlankDoor(HouseMesh hm, Frame f, OpeningSpec o, float ls0, float ls1, float y0, float ly1, float d0)
        {
            float e0 = d0 + 0.006f, e1 = e0 + 0.05f;
            var rnd = new System.Random(Seed($"{o.w0:0.00}{o.y0:0.00}plank"));
            float U() => (float)rnd.NextDouble();
            int n = Mathf.Max(3, Mathf.RoundToInt((ls1 - ls0) / 0.11f));
            float pitch = (ls1 - ls0) / n;
            float hingeS = o.hingeLeft ? ls0 : ls1, dir = o.hingeLeft ? 1f : -1f;
            Frame g = LeafFrame(f, o, hingeS, e1);
            var p0 = hm.Lod0;
            p0.UvTurn = true;
            for (int i = 0; i < n; i++)
            {
                float a = ls0 + pitch * i + 0.002f, b = ls0 + pitch * (i + 1) - 0.002f;
                float bleach = Mathf.Lerp(0.2f, 0.65f, U()), wet = Mathf.Lerp(0.3f, 0.7f, U());
                float bottom = y0 + 0.012f + U() * 0.015f;                 // ragged, rotted board ends
                float dz = (U() - 0.5f) * 0.004f;
                p0.ColorFn = (q, _) => new Color(bleach, Mathf.Clamp01(wet * (1f - (q.y - y0) / 0.6f)), 0f, 1f);
                BoxP(p0, g, a, b, bottom, ly1 - U() * 0.004f, e0 + dz, e0 + 0.024f + dz, Slot.Wood);
            }
            p0.UvTurn = false;
            // Ledges (top, middle, bottom) behind the boards; they show through the gaps and on the open inner face.
            p0.ColorFn = (q, _) => new Color(0.7f, 0.5f, 0f, 1f);
            foreach (float yl in new[] { y0 + 0.2f, (y0 + ly1) * 0.5f, ly1 - 0.2f })
                BoxP(p0, g, ls0 + 0.03f, ls1 - 0.03f, yl - 0.06f, yl + 0.06f, e0 + 0.026f, e1, Slot.Wood);
            // Strap hinges and hasp: painted iron mostly rusted (Metal vertex R = rust).
            p0.ColorFn = (q, _) => new Color(0.75f, 0.4f, 0f, 1f);
            foreach (float y in new[] { y0 + 0.32f, ly1 - 0.3f })
            {
                BoxP(p0, g, Mathf.Min(hingeS, hingeS + dir * 0.42f), Mathf.Max(hingeS, hingeS + dir * 0.42f), y - 0.02f, y + 0.02f, e0 - 0.006f, e0 - 0.002f, Slot.Metal);
                p0.Cylinder(f.P(hingeS, y - 0.04f, e1), f.P(hingeS, y + 0.04f, e1), 0.008f, 6, Slot.Metal);
            }
            float lockS = o.hingeLeft ? ls1 : ls0, yh = y0 + 1.0f;
            BoxP(p0, g, Mathf.Min(lockS, lockS - dir * 0.14f), Mathf.Max(lockS, lockS - dir * 0.14f), yh - 0.018f, yh + 0.018f, e0 - 0.006f, e0 - 0.002f, Slot.Metal); // hasp
            p0.ColorFn = null;
            Handle(p0, g, o, lockS - dir * 0.06f, yh - 0.12f, e0, -1f, -dir);
            BoxP(hm.Collision, g, ls0, ls1, y0, ly1, e0, e1, Slot.Render);
            hm.Lod1.ColorFn = (q, _) => new Color(0.45f, 0.4f, 0f, 1f);
            BoxP(hm.Lod1, g, ls0, ls1, y0, ly1, e0, e1, Slot.Wood);
            hm.Lod1.ColorFn = null;
        }

        // ---------- Rooms behind the openings ----------

        // A sealed room behind the opening: floor, ceiling, back and side walls facing in (the facade's own inner face
        // closes the front). It is fitted inside the house: any wall, slab or earlier room it would cut into pushes the
        // nearest face back (never past the opening itself). Darker with depth (vertex G), tone per room (vertex R).
        static void BuildInterior(HouseMesh hm, Frame f, WallSpec w, OpeningSpec o, float s0, float s1, HouseSpec spec)
        {
            float t = w.thickness;
            bool door = o.kind != OpeningKind.Window;
            float depth = o.interiorDepth > 0f ? o.interiorDepth : door ? 2.2f : 1.6f;
            float yf = door ? o.y0 : Mathf.Max(w.y0 + 0.2f, o.y0 - 0.95f);
            float yc = Mathf.Max(o.y1 + 0.08f, Mathf.Min(yf + 2.75f, w.y1 - 0.02f));
            Vector3 rMin = Vector3.Min(f.P(s0 - 0.4f, yf, t), f.P(s1 + 0.4f, yc, t + depth));
            Vector3 rMax = Vector3.Max(f.P(s0 - 0.4f, yf, t), f.P(s1 + 0.4f, yc, t + depth));
            // What the room must always contain: the opening and 0.6 m behind it.
            Vector3 kMin = Vector3.Min(f.P(s0, o.y0, t), f.P(s1, o.y1, t + 0.6f)), kMax = Vector3.Max(f.P(s0, o.y0, t), f.P(s1, o.y1, t + 0.6f));
            kMin.y = Mathf.Max(kMin.y, yf); kMax.y = Mathf.Min(kMax.y, yc);

            var obstacles = new List<(Vector3 min, Vector3 max, string name)>();
            foreach (var w2 in spec.walls)
            {
                if (w2 == w) continue;
                Frame f2 = GetFrame(w2, out float len2, out _);
                obstacles.Add((Vector3.Min(f2.P(0f, w2.y0, 0f), f2.P(len2, w2.y1, w2.thickness)), Vector3.Max(f2.P(0f, w2.y0, 0f), f2.P(len2, w2.y1, w2.thickness)), w2.name));
            }
            foreach (var bx in spec.boxes) if (!bx.detail) obstacles.Add((bx.min, bx.max, bx.name));
            foreach (var r in Rooms) obstacles.Add((r.min, r.max, "room"));
            const float eps = 0.004f, gap = 0.01f;
            foreach (var (oMin, oMax, name) in obstacles)
            {
                bool hit = true;
                for (int ax = 0; ax < 3; ax++) hit &= rMax[ax] > oMin[ax] + eps && rMin[ax] < oMax[ax] - eps;
                if (!hit) continue;
                // Push the face that needs the smallest move, as long as the room still holds the opening.
                var moves = new List<(float pen, int ax, bool max)>();
                for (int ax = 0; ax < 3; ax++)
                {
                    if (oMin[ax] - gap >= kMax[ax]) moves.Add((rMax[ax] - (oMin[ax] - gap), ax, true));
                    if (oMax[ax] + gap <= kMin[ax]) moves.Add((oMax[ax] + gap - rMin[ax], ax, false));
                }
                if (moves.Count == 0) { Debug.LogWarning($"[Lockdown] Room behind {w.name} opening at {o.w0:0.0} m cannot clear {name}."); continue; }
                moves.Sort((x, y) => x.pen.CompareTo(y.pen));
                var m = moves[0];
                if (m.max) rMax[m.ax] = oMin[m.ax] - gap; else rMin[m.ax] = oMax[m.ax] + gap;
            }
            Rooms.Add((rMin, rMax));

            // Back to frame space.
            float Sof(Vector3 p) => Vector3.Dot(p - f.origin, f.along);
            float Dof(Vector3 p) => -Vector3.Dot(p - f.origin, f.outward);
            float a = Mathf.Min(Sof(rMin), Sof(rMax)), b = Mathf.Max(Sof(rMin), Sof(rMax));
            float dBack = Mathf.Max(Dof(rMin), Dof(rMax));
            yf = rMin.y; yc = rMax.y;
            depth = dBack - t;

            bool store = o.kind == OpeningKind.Void;
            float tone = store ? 1f : o.roomTone;
            float g0 = store ? 0.85f : 0.75f, g1 = store ? 1f : 0.98f;
            hm.ColorFn = (q, n) =>
            {
                float k = Mathf.Clamp01((Dof(q) - t) / Mathf.Max(0.1f, depth));
                float g = Mathf.Lerp(g0, g1, k) + (n.y > 0.5f ? 0.08f : n.y < -0.5f ? 0.05f : 0f);
                return new Color(tone, Mathf.Clamp01(g), 0f, 1f);
            };
            Vector2 V(float s, float y) => new(s, y);
            foreach (var part in new[] { hm.Lod0, hm.Lod1 })
            {
                QuadP(part, f, V(a, yf), V(b, yf), V(b, yf), V(a, yf), t, t, dBack, dBack, Vector3.up, Slot.Interior);   // floor
                QuadP(part, f, V(a, yc), V(b, yc), V(b, yc), V(a, yc), t, t, dBack, dBack, Vector3.down, Slot.Interior); // ceiling
                QuadP(part, f, V(a, yf), V(b, yf), V(b, yc), V(a, yc), dBack, dBack, dBack, dBack, f.outward, Slot.Interior); // back
                QuadP(part, f, V(a, yf), V(a, yf), V(a, yc), V(a, yc), t, dBack, dBack, t, f.along, Slot.Interior);        // side
                QuadP(part, f, V(b, yf), V(b, yf), V(b, yc), V(b, yc), t, dBack, dBack, t, -f.along, Slot.Interior);       // side
            }
            if (store)
            {
                // Walkable: a floor and walls the walker can't pass.
                ColW(hm, f, a, b, yf - 0.2f, yf, t, dBack);
                ColW(hm, f, a - 0.1f, a, yf, yc, t, dBack);
                ColW(hm, f, b, b + 0.1f, yf, yc, t, dBack);
                ColW(hm, f, a, b, yf, yc, dBack, dBack + 0.1f);
            }

            // A wardrobe against the back wall in some rooms: a depth cue through the glass.
            if (o.roomCabinet && b - a > 1.3f && depth > 1.0f)
            {
                float c = Mathf.Clamp((s0 + s1) * 0.5f + (o.hingeLeft ? -0.35f : 0.35f), a + 0.5f, b - 0.5f);
                hm.ColorFn = (q, _) => new Color(0.85f, 0.55f, 0f, 1f);
                hm.Box(Vector3.Min(f.P(c - 0.45f, yf, dBack - 0.55f), f.P(c + 0.45f, Mathf.Min(yc - 0.15f, yf + 1.95f), dBack - 0.01f)),
                       Vector3.Max(f.P(c - 0.45f, yf, dBack - 0.55f), f.P(c + 0.45f, Mathf.Min(yc - 0.15f, yf + 1.95f), dBack - 0.01f)), Slot.Wood);
            }
            if (!store) BuildCurtain(hm, f, o, s0, s1, a, b, yf, yc, t + 0.05f);
        }

        // Curtains hang just behind the facade's inner face. Pleats are a zig-zag strip (columns alternate 2.5 cm in
        // depth), flat shaded so each fold catches the light; LOD1 gets the same outline flat.
        static void BuildCurtain(HouseMesh hm, Frame f, OpeningSpec o, float s0, float s1, float a, float b, float yf, float yc, float d)
        {
            var kind = o.curtainKind;
            if (kind == CurtainKind.None || kind == CurtainKind.Auto) return;
            float rodL = Mathf.Max(a + 0.02f, s0 - 0.12f), rodR = Mathf.Min(b - 0.02f, s1 + 0.12f);
            float top = Mathf.Min(o.y1 + 0.1f, yc - 0.03f);
            bool door = o.kind != OpeningKind.Window;
            float bottom = door ? yf + 0.015f : o.y0 - 0.04f;
            float h = o.y1 - o.y0, span = rodR - rodL;
            float tone = o.curtainTone;
            hm.ColorFn = (q, _) => new Color(tone, 0.3f, 0f, 1f);

            // rows: (y, left s, right s) from top to bottom; the strip is pleated between left and right on each row.
            void Strip((float y, float l, float r)[] rows)
            {
                float width = Mathf.Max(rows[0].r - rows[0].l, 0.1f);
                int cols = Mathf.Max(4, Mathf.RoundToInt(width / 0.05f));
                for (int lod = 0; lod < 2; lod++)
                {
                    var part = lod == 0 ? hm.Lod0 : hm.Lod1;
                    int n = lod == 0 ? cols : 1;
                    float amp = lod == 0 ? 0.025f : 0f;
                    for (int r = 0; r < rows.Length - 1; r++)
                    for (int k = 0; k < n; k++)
                    {
                        var (ya, la, ra) = rows[r];
                        var (yb, lb, rb) = rows[r + 1];
                        float u0 = k / (float)n, u1 = (k + 1) / (float)n;
                        // Gathered rows (narrower) fold deeper.
                        float ampA = amp * Mathf.Clamp(width / Mathf.Max(ra - la, 0.05f), 1f, 2.2f) * 0.6f + amp * 0.4f;
                        float ampB = amp * Mathf.Clamp(width / Mathf.Max(rb - lb, 0.05f), 1f, 2.2f) * 0.6f + amp * 0.4f;
                        float dA0 = d + (k % 2 == 0 ? 0f : ampA), dA1 = d + (k % 2 == 0 ? ampA : 0f);
                        float dB0 = d + (k % 2 == 0 ? 0f : ampB), dB1 = d + (k % 2 == 0 ? ampB : 0f);
                        QuadP(part, f, new Vector2(Mathf.Lerp(lb, rb, u0), yb), new Vector2(Mathf.Lerp(lb, rb, u1), yb),
                              new Vector2(Mathf.Lerp(la, ra, u1), ya), new Vector2(Mathf.Lerp(la, ra, u0), ya),
                              dB0, dB1, dA1, dA0, f.outward, Slot.Curtain);
                    }
                }
            }

            switch (kind)
            {
                case CurtainKind.Cafe: // lower half on a rod across the glass
                {
                    float yt = o.y0 + h * 0.48f;
                    Strip(new[] { (yt, s0 - 0.02f, s1 + 0.02f), (o.y0 + 0.01f, s0 - 0.02f, s1 + 0.02f) });
                    break;
                }
                case CurtainKind.TiedLeft:
                case CurtainKind.TiedRight: // one panel drawn across the top, gathered to the side by a tie
                {
                    bool left = kind == CurtainKind.TiedLeft;
                    float reach = span * o.curtainReach, tieY = o.y0 + h * (door ? 0.42f : 0.35f);
                    float side = left ? rodL : rodR, sgn = left ? 1f : -1f;
                    (float, float, float) Row(float y, float wdt) => left ? (y, side, side + sgn * wdt) : (y, side + sgn * wdt, side);
                    Strip(new[] { Row(top, reach), Row(tieY, 0.17f), Row(bottom, 0.24f) });
                    break;
                }
                case CurtainKind.DrawnPair: // two panels, a gap left between them
                {
                    float half = span * 0.5f * o.curtainReach;
                    Strip(new[] { (top, rodL, rodL + half), (bottom, rodL, rodL + half) });
                    Strip(new[] { (top, rodR - half, rodR), (bottom, rodR - half, rodR) });
                    break;
                }
            }
        }

        // Seeded dressing of what shows through each opening (own random stream: the weathering's sequence, and so the
        // approved stains, stay as they were). No two neighbouring openings get the same curtain.
        static void DressInteriors(HouseSpec spec, int seed)
        {
            var rnd = new System.Random(seed ^ 0x1A7E);
            float U() => (float)rnd.NextDouble();
            var last = CurtainKind.Auto;
            foreach (var w in spec.walls)
            foreach (var o in w.openings)
            {
                if (o.kind is not (OpeningKind.Window or OpeningKind.GlazedDoor or OpeningKind.Void)) continue;
                var pool = o.kind == OpeningKind.Window
                    ? new[] { CurtainKind.None, CurtainKind.Cafe, CurtainKind.TiedLeft, CurtainKind.TiedRight, CurtainKind.DrawnPair, CurtainKind.TiedLeft }
                    : new[] { CurtainKind.None, CurtainKind.TiedLeft, CurtainKind.TiedRight, CurtainKind.DrawnPair };
                var pick = pool[rnd.Next(pool.Length)];
                if (pick == last) pick = pool[(System.Array.IndexOf(pool, pick) + 1 + rnd.Next(pool.Length - 1)) % pool.Length];
                o.curtainKind = o.curtain != CurtainKind.Auto ? o.curtain : pick;
                last = o.curtainKind;
                o.curtainTone = U() < 0.6f ? 0f : 1f;
                o.curtainReach = Mathf.Lerp(0.45f, 0.8f, U());
                o.dustyGlass = U() < 0.4f;
                o.roomTone = U() < 0.7f ? 0f : 1f;
                o.roomCabinet = U() < 0.5f;
            }
            foreach (var w in spec.walls)
            foreach (var o in w.openings)
            {
                o.swing = o.openAngle >= 0f ? o.openAngle : Mathf.Lerp(88f, 100f, U()); // pushed back to the reveal: the doorway stays wide enough to walk through
                o.handleDroop = Mathf.Lerp(0.002f, 0.012f, U());
            }
        }
    }
}
