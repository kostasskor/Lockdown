using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lockdown.EditorTools
{
    // Material slots of a generated house. Each slot becomes one submesh and one shared material
    // (Assets/Lockdown/Materials/Houses/M_House_<Slot>.mat), so every house shares the same set.
    public enum Slot { Render, RenderOld, Concrete, PaintBlue, Stone, Wood, Tile, Metal, Glass, FrameWhite, Dark, GlassDusty, Aluminium, Hardware, Interior, Curtain, Brass, InteriorWall }

    // Accumulates flat-shaded geometry per material slot. UVs are projected in metres from the
    // house's local space, so every texture tiles at real-world scale whatever the part's size.
    // Every face is also recorded with the name of the part that made it, for the coplanar-overlap audit.
    public class MeshPart
    {
        public static readonly int SlotCount = System.Enum.GetValues(typeof(Slot)).Length;

        readonly List<Vector3> verts = new();
        readonly List<Vector3> normals = new();
        readonly List<Vector2> uvs = new();
        readonly List<Color> colors = new();
        readonly List<int>[] tris;

        public string Tag = "";                        // source part of the faces being added
        public System.Func<Vector3, Vector3, Color> ColorFn; // vertex colour by position and flat normal (null = black)
        public bool UvSwap;                             // horizontal faces: texture turned 90 degrees (grain along Z)
        public bool UvTurn;                             // vertical faces: texture turned 90 degrees (grain upright: stiles, boards)
        public System.Func<Vector3, Vector3, Slot, Slot> SlotFn; // per-face slot by centre and normal (null = as given), e.g. a wall's room side
        public int UvSeed;                              // per-house seed for the per-plane texture offset (0 = none)
        public readonly List<(Vector3 a, Vector3 b, Vector3 c, Vector3 n, string tag)> Faces = new(); // as built, before the warp

        // Hand-built imperfections (null field = not warped, e.g. the collision mesh). Faces are cut on a global
        // grid so neighbouring parts share their vertices, then every vertex moves by the field.
        public WarpField Field;
        public WarpProfile Profile;
        public float Grid = 1f, GridY = 2f;            // cut spacing across X/Z and up Y (lean is linear in Y)
        public float MaxWalkableDy;                     // largest vertical move on an upward tile face (must stay 0)
        public readonly Dictionary<string, int> TrisByPart = new(); // triangle budget per part (first tag segment)

        public MeshPart()
        {
            tris = new List<int>[SlotCount];
            for (int i = 0; i < SlotCount; i++) tris[i] = new List<int>();
        }

        public int VertexCount => verts.Count;

        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward, Slot slot)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (n.sqrMagnitude < 1e-12f) return;
            n.Normalize();
            if (Vector3.Dot(n, outward) < 0f) { (b, d) = (d, b); n = -n; }
            if (SlotFn != null) slot = SlotFn((a + b + c + d) * 0.25f, n, slot);
            faceOffset = FaceOffset((a + b + c + d) * 0.25f, n);
            Faces.Add((a, b, c, n, Tag));
            Faces.Add((a, c, d, n, Tag));
            if (Field != null) { EmitWarped(new List<Vector3> { a, b, c, d }, n, slot); return; }
            int i = verts.Count;
            Add(a, n); Add(b, n); Add(c, n); Add(d, n);
            tris[(int)slot].AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
        }

        public void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 outward, Slot slot)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (n.sqrMagnitude < 1e-12f) return;
            n.Normalize();
            if (Vector3.Dot(n, outward) < 0f) { (b, c) = (c, b); n = -n; }
            if (SlotFn != null) slot = SlotFn((a + b + c) / 3f, n, slot);
            faceOffset = FaceOffset((a + b + c) / 3f, n);
            Faces.Add((a, b, c, n, Tag));
            if (Field != null) { EmitWarped(new List<Vector3> { a, b, c }, n, slot); return; }
            int i = verts.Count;
            Add(a, n); Add(b, n); Add(c, n);
            tris[(int)slot].AddRange(new[] { i, i + 1, i + 2 });
        }

        void Add(Vector3 p, Vector3 n)
        {
            verts.Add(p);
            normals.Add(n);
            uvs.Add(UV(p, n));
            colors.Add(ColorFn != null ? ColorFn(p, n) : new Color(0f, 0f, 0f, 1f));
        }

        Vector2 UV(Vector3 p, Vector3 n)
        {
            Vector2 uv = Project(p, n) + faceOffset;
            bool horizontal = Mathf.Abs(n.y) >= Mathf.Max(Mathf.Abs(n.x), Mathf.Abs(n.z));
            if (horizontal ? UvSwap : UvTurn) uv = new Vector2(uv.y, uv.x);
            return uv;
        }

        // A convex planar polygon, cut by the planes x, y, z = k * Grid, each piece warped. UVs and vertex colours
        // come from the unwarped position, so textures never swim; normals follow the warped surface.
        void EmitWarped(List<Vector3> poly, Vector3 n, Slot slot)
        {
            var pieces = new List<List<Vector3>> { poly };
            Vector3 lo = poly[0], hi = poly[0];
            foreach (var p in poly) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
            Vector3 ext = hi - lo;
            // Small pieces (slats, bars, frames) are not cut: over < 0.75 m the field is as good as linear.
            bool small = Mathf.Max(ext.x, Mathf.Max(ext.y, ext.z)) < 0.75f;
            for (int axis = 0; axis < 3 && !small; axis++)
            {
                var next = new List<List<Vector3>>();
                foreach (var pc in pieces) Cut(pc, axis, next);
                pieces = next;
            }
            bool walkable = slot == Slot.Tile && n.y > 0.9f;
            foreach (var pc in pieces)
            {
                int i = verts.Count;
                foreach (var p in pc)
                {
                    Vector3 d = Field.Displace(p, Profile);
                    if (walkable) MaxWalkableDy = Mathf.Max(MaxWalkableDy, Mathf.Abs(d.y));
                    verts.Add(p + d);
                    normals.Add(Field.Normal(p, n, Profile));
                    uvs.Add(UV(p, n));
                    colors.Add(ColorFn != null ? ColorFn(p, n) : new Color(0f, 0f, 0f, 1f));
                }
                for (int k = 1; k < pc.Count - 1; k++) tris[(int)slot].AddRange(new[] { i, i + k, i + k + 1 });
                string part = Tag.Split('/')[0];
                TrisByPart[part] = (TrisByPart.TryGetValue(part, out int t) ? t : 0) + pc.Count - 2;
            }
        }

        void Cut(List<Vector3> poly, int axis, List<List<Vector3>> output)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var p in poly) { lo = Mathf.Min(lo, p[axis]); hi = Mathf.Max(hi, p[axis]); }
            const float eps = 0.02f; // no slivers thinner than 2 cm
            float g = axis == 1 ? GridY : Grid;
            int k0 = Mathf.CeilToInt((lo + eps) / g), k1 = Mathf.FloorToInt((hi - eps) / g);
            var rest = poly;
            for (int k = k0; k <= k1; k++)
            {
                float c = k * g;
                var below = new List<Vector3>();
                var above = new List<Vector3>();
                for (int i = 0; i < rest.Count; i++)
                {
                    Vector3 p = rest[i], q = rest[(i + 1) % rest.Count];
                    float dp = p[axis] - c, dq = q[axis] - c;
                    if (dp <= 0f) below.Add(p);
                    if (dp >= 0f) above.Add(p);
                    if ((dp < 0f && dq > 0f) || (dp > 0f && dq < 0f))
                    {
                        Vector3 x = Vector3.Lerp(p, q, dp / (dp - dq));
                        x[axis] = c;
                        below.Add(x);
                        above.Add(x);
                    }
                }
                if (below.Count >= 3) output.Add(below);
                rest = above;
                if (rest.Count < 3) return;
            }
            output.Add(rest);
        }

        // Prism along `axis` (a unit direction) from t0 to t1 with a convex cross-section given by 3D points at t = 0.
        // Used for worn, rounded edges: the cross-section is a rectangle with some corners rounded off.
        public void Extrude(IList<Vector3> section, Vector3 axis, float t0, float t1, Slot slot)
        {
            int n = section.Count;
            Vector3 c = Vector3.zero;
            foreach (var p in section) c += p;
            c /= n;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = section[i], q = section[(i + 1) % n];
                Vector3 mid = (p + q) * 0.5f - c;
                Vector3 outward = mid - axis * Vector3.Dot(mid, axis);
                Quad(p + axis * t0, q + axis * t0, q + axis * t1, p + axis * t1, outward, slot);
            }
            for (int i = 1; i < n - 1; i++)
            {
                Tri(section[0] + axis * t0, section[i] + axis * t0, section[i + 1] + axis * t0, -axis, slot);
                Tri(section[0] + axis * t1, section[i] + axis * t1, section[i + 1] + axis * t1, axis, slot);
            }
        }

        // Horizontal faces: planar (x, z). Vertical faces: u = x - z, one function of position for every wall
        // direction (plus the per-plane offset above). East- and north-facing faces get the texture mirrored, which
        // plaster and stone don't show; tangents follow the UVs per face, so normal maps stay correct.
        static Vector2 Project(Vector3 p, Vector3 n)
        {
            float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
            if (ay >= ax && ay >= az) return n.y > 0f ? new Vector2(p.x, p.z) : new Vector2(p.x, -p.z);
            return new Vector2(p.x - p.z, p.y);
        }

        // No two walls show the same patch of texture: every plane (main facing + distance along its normal, 2 cm bins)
        // gets its own seeded UV offset, up to 64 m (the tone map's size). Computed once per face from its centre, so
        // faces of one plane share it and a wall's pieces stay seamless; the change falls on corners, where the worn
        // edge and the light already change. Horizontal faces move by whole metres, so floor-tile joints stay put.
        Vector2 faceOffset;

        Vector2 FaceOffset(Vector3 centre, Vector3 n)
        {
            if (UvSeed == 0) return Vector2.zero;
            float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
            bool horizontal = ay >= ax && ay >= az;
            int dir = horizontal ? (n.y > 0f ? 4 : 5) : ax >= az ? (n.x > 0f ? 0 : 1) : (n.z > 0f ? 2 : 3);
            int plane = Mathf.FloorToInt(Vector3.Dot(centre, n) * 50f + 0.37f); // bin edges at odd values, never at 1 cm multiples
            uint h;
            unchecked
            {
                h = (uint)UvSeed * 0x9E3779B1u ^ (uint)(dir + 1) * 0x85EBCA77u ^ (uint)plane * 0xC2B2AE3Du;
                h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            }
            var o = new Vector2((h & 0xFFFF) / 65536f * 64f, (h >> 16) / 65536f * 64f);
            return horizontal ? new Vector2(Mathf.Round(o.x), Mathf.Round(o.y)) : o;
        }

        public void Box(Vector3 center, Vector3 size, Quaternion rot, Slot slot)
        {
            Vector3 h = size * 0.5f;
            Vector3 P(float x, float y, float z) => center + rot * new Vector3(x * h.x, y * h.y, z * h.z);
            for (int axis = 0; axis < 3; axis++)
            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 o = Vector3.zero; o[axis] = s;
                int u = (axis + 1) % 3, v = (axis + 2) % 3;
                Vector3 C(int su, int sv) { var q = Vector3.zero; q[axis] = s; q[u] = su; q[v] = sv; return P(q.x, q.y, q.z); }
                Quad(C(-1, -1), C(1, -1), C(1, 1), C(-1, 1), rot * o, slot);
            }
        }

        // Box with chamfered edges (slab edges, columns, copings): reads far less "CG" than knife edges.
        public void ChamferBox(Vector3 center, Vector3 size, Quaternion rot, float ch, Slot slot)
        {
            Vector3 h = size * 0.5f;
            ch = Mathf.Min(ch, Mathf.Min(h.x, Mathf.Min(h.y, h.z)) * 0.9f);
            Vector3 W(Vector3 l) => center + rot * l;
            Vector3 PX(int sx, int sy, int sz) => W(new Vector3(sx * h.x, sy * (h.y - ch), sz * (h.z - ch)));
            Vector3 PY(int sx, int sy, int sz) => W(new Vector3(sx * (h.x - ch), sy * h.y, sz * (h.z - ch)));
            Vector3 PZ(int sx, int sy, int sz) => W(new Vector3(sx * (h.x - ch), sy * (h.y - ch), sz * h.z));
            for (int s = -1; s <= 1; s += 2)
            {
                Quad(PX(s, -1, -1), PX(s, 1, -1), PX(s, 1, 1), PX(s, -1, 1), rot * new Vector3(s, 0, 0), slot);
                Quad(PY(-1, s, -1), PY(1, s, -1), PY(1, s, 1), PY(-1, s, 1), rot * new Vector3(0, s, 0), slot);
                Quad(PZ(-1, -1, s), PZ(1, -1, s), PZ(1, 1, s), PZ(-1, 1, s), rot * new Vector3(0, 0, s), slot);
            }
            for (int a = -1; a <= 1; a += 2)
            for (int b = -1; b <= 1; b += 2)
            {
                Quad(PX(a, b, -1), PX(a, b, 1), PY(a, b, 1), PY(a, b, -1), rot * new Vector3(a, b, 0), slot);
                Quad(PX(a, -1, b), PX(a, 1, b), PZ(a, 1, b), PZ(a, -1, b), rot * new Vector3(a, 0, b), slot);
                Quad(PY(-1, a, b), PY(1, a, b), PZ(1, a, b), PZ(-1, a, b), rot * new Vector3(0, a, b), slot);
                for (int c = -1; c <= 1; c += 2)
                    Tri(PX(a, b, c), PY(a, b, c), PZ(a, b, c), rot * new Vector3(a, b, c), slot);
            }
        }

        public void Cylinder(Vector3 a, Vector3 b, float radius, int sides, Slot slot, bool caps = true)
        {
            Vector3 axis = (b - a).normalized;
            Vector3 side = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 fwd = Vector3.Cross(axis, side);
            Vector3 R(int i) { float t = i * Mathf.PI * 2f / sides; return (side * Mathf.Cos(t) + fwd * Mathf.Sin(t)) * radius; }
            for (int i = 0; i < sides; i++)
            {
                Vector3 r0 = R(i), r1 = R(i + 1);
                Quad(a + r0, a + r1, b + r1, b + r0, (r0 + r1).normalized, slot);
                if (!caps) continue;
                Tri(a, a + r0, a + r1, -axis, slot);
                Tri(b, b + r0, b + r1, axis, slot);
            }
        }

        // Prism along X with a convex cross-section given in (z, y).
        public void PrismX(float x0, float x1, IList<Vector2> zy, Slot slot)
        {
            int n = zy.Count;
            Vector2 centroid = Vector2.zero;
            foreach (var p in zy) centroid += p;
            centroid /= n;
            for (int i = 0; i < n; i++)
            {
                Vector2 p = zy[i], q = zy[(i + 1) % n];
                Vector2 mid = (p + q) * 0.5f - centroid;
                Quad(new Vector3(x0, p.y, p.x), new Vector3(x1, p.y, p.x), new Vector3(x1, q.y, q.x), new Vector3(x0, q.y, q.x),
                    new Vector3(0f, mid.y, mid.x), slot);
            }
            for (int i = 1; i < n - 1; i++)
            {
                Tri(new Vector3(x0, zy[0].y, zy[0].x), new Vector3(x0, zy[i].y, zy[i].x), new Vector3(x0, zy[i + 1].y, zy[i + 1].x), Vector3.left, slot);
                Tri(new Vector3(x1, zy[0].y, zy[0].x), new Vector3(x1, zy[i].y, zy[i].x), new Vector3(x1, zy[i + 1].y, zy[i + 1].x), Vector3.right, slot);
            }
        }

        // Builds a mesh holding only the slots that received geometry; usedSlots maps submesh -> slot.
        public Mesh ToMesh(string name, out List<Slot> usedSlots)
        {
            usedSlots = new List<Slot>();
            for (int i = 0; i < SlotCount; i++) if (tris[i].Count > 0) usedSlots.Add((Slot)i);
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.subMeshCount = Mathf.Max(1, usedSlots.Count);
            for (int i = 0; i < usedSlots.Count; i++) mesh.SetTriangles(tris[(int)usedSlots[i]], i);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    // The three outputs of one house: LOD0 (everything), LOD1 (no small detail) and a box-only collision mesh.
    public class HouseMesh
    {
        public readonly MeshPart Lod0 = new(), Lod1 = new(), Collision = new();
        public bool Detail; // while true, geometry goes to LOD0 only

        public string Tag { set { Lod0.Tag = Lod1.Tag = Collision.Tag = value; } }
        public System.Func<Vector3, Vector3, Color> ColorFn { set { Lod0.ColorFn = Lod1.ColorFn = value; } }
        public bool UvSwap { set { Lod0.UvSwap = Lod1.UvSwap = value; } }
        public bool UvTurn { set { Lod0.UvTurn = Lod1.UvTurn = value; } }
        public System.Func<Vector3, Vector3, Slot, Slot> SlotFn { set { Lod0.SlotFn = Lod1.SlotFn = value; } }
        public int UvSeed { set { Lod0.UvSeed = Lod1.UvSeed = value; } }
        public WarpProfile Profile { set { Lod0.Profile = Lod1.Profile = value; } }

        public void SetWarp(WarpField field, ImperfectionSettings s)
        {
            Lod0.Field = Lod1.Field = field;
            Lod0.Grid = s.gridLod0;
            Lod0.GridY = s.gridLod0Y;
            Lod1.Grid = Lod1.GridY = s.gridLod1;
        }

        public void Box(Vector3 min, Vector3 max, Slot slot, bool collide = false) =>
            Box((min + max) * 0.5f, max - min, Quaternion.identity, slot, collide);

        public void Box(Vector3 center, Vector3 size, Quaternion rot, Slot slot, bool collide = false)
        {
            Lod0.Box(center, size, rot, slot);
            if (!Detail) Lod1.Box(center, size, rot, slot);
            if (collide) Collision.Box(center, size, rot, Slot.Render);
        }

        public void ChamferBox(Vector3 min, Vector3 max, float ch, Slot slot, bool collide = false)
        {
            Vector3 c = (min + max) * 0.5f, s = max - min;
            Lod0.ChamferBox(c, s, Quaternion.identity, ch, slot);
            if (!Detail) Lod1.Box(c, s, Quaternion.identity, slot);
            if (collide) Collision.Box(c, s, Quaternion.identity, Slot.Render);
        }

        public void Cylinder(Vector3 a, Vector3 b, float radius, Slot slot)
        {
            Lod0.Cylinder(a, b, radius, 12, slot);
            if (!Detail) Lod1.Cylinder(a, b, radius, 6, slot);
        }

        public void PrismX(float x0, float x1, IList<Vector2> zy, Slot slot, bool collide = false)
        {
            Lod0.PrismX(x0, x1, zy, slot);
            if (!Detail) Lod1.PrismX(x0, x1, zy, slot);
            if (collide) Collision.PrismX(x0, x1, zy, Slot.Render);
        }
    }
}
