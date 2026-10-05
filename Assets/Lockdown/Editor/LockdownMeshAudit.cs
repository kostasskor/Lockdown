using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Lockdown.EditorTools
{
    // Finds z-fighting in generated meshes: faces from different parts that lie in the same plane, face the same
    // way and overlap. (Opposite-facing coplanar faces are fine: one of them is always back-face culled.)
    public static class LockdownMeshAudit
    {
        const float NormalQuant = 1000f;   // normals equal to 0.001
        const float PlaneQuant = 10000f;   // plane distance equal to 0.1 mm
        const float MinArea = 1e-5f;       // 0.1 cm^2

        public static List<string> FindCoplanarOverlaps(MeshPart part)
        {
            var groups = new Dictionary<(int, int, int, long), List<int>>();
            var faces = part.Faces;
            for (int i = 0; i < faces.Count; i++)
            {
                var f = faces[i];
                var key = (Mathf.RoundToInt(f.n.x * NormalQuant), Mathf.RoundToInt(f.n.y * NormalQuant), Mathf.RoundToInt(f.n.z * NormalQuant),
                    (long)System.Math.Round(Vector3.Dot(f.n, f.a) * PlaneQuant));
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<int>();
                list.Add(i);
            }

            var hits = new Dictionary<(string, string), (float area, Vector3 at)>();
            foreach (var list in groups.Values)
            {
                if (list.Count < 2 || list.Select(i => faces[i].tag).Distinct().Count() < 2) continue;
                Vector3 n = faces[list[0]].n;
                int drop = Mathf.Abs(n.x) > Mathf.Abs(n.y) ? (Mathf.Abs(n.x) > Mathf.Abs(n.z) ? 0 : 2) : (Mathf.Abs(n.y) > Mathf.Abs(n.z) ? 1 : 2);
                var tri2 = list.Select(i => new[] { To2(faces[i].a, drop), To2(faces[i].b, drop), To2(faces[i].c, drop) }).ToList();
                for (int x = 0; x < list.Count; x++)
                for (int y = x + 1; y < list.Count; y++)
                {
                    string ta = faces[list[x]].tag, tb = faces[list[y]].tag;
                    if (ta == tb) continue;
                    float area = OverlapArea(tri2[x], tri2[y]);
                    if (area < MinArea) continue;
                    var k = string.CompareOrdinal(ta, tb) < 0 ? (ta, tb) : (tb, ta);
                    hits.TryGetValue(k, out var prev);
                    hits[k] = (prev.area + area, faces[list[x]].a);
                }
            }
            return hits.OrderByDescending(h => h.Value.area)
                .Select(h => $"{h.Key.Item1}  <->  {h.Key.Item2}   {h.Value.area * 10000f:0.#} cm2 near {h.Value.at}").ToList();
        }

        static Vector2 To2(Vector3 p, int drop) => drop == 0 ? new Vector2(p.y, p.z) : drop == 1 ? new Vector2(p.x, p.z) : new Vector2(p.x, p.y);

        // Area of the intersection of two triangles (Sutherland-Hodgman clipping).
        static float OverlapArea(Vector2[] a, Vector2[] b)
        {
            Vector2 aMin = Vector2.Min(a[0], Vector2.Min(a[1], a[2])), aMax = Vector2.Max(a[0], Vector2.Max(a[1], a[2]));
            Vector2 bMin = Vector2.Min(b[0], Vector2.Min(b[1], b[2])), bMax = Vector2.Max(b[0], Vector2.Max(b[1], b[2]));
            if (aMax.x <= bMin.x || bMax.x <= aMin.x || aMax.y <= bMin.y || bMax.y <= aMin.y) return 0f;

            var clip = SignedArea(b) < 0f ? new[] { b[0], b[2], b[1] } : b;
            var poly = new List<Vector2>(a);
            for (int e = 0; e < 3 && poly.Count > 0; e++)
            {
                Vector2 p0 = clip[e], p1 = clip[(e + 1) % 3];
                var input = poly;
                poly = new List<Vector2>();
                for (int i = 0; i < input.Count; i++)
                {
                    Vector2 cur = input[i], prev = input[(i + input.Count - 1) % input.Count];
                    bool curIn = Side(p0, p1, cur) >= 0f, prevIn = Side(p0, p1, prev) >= 0f;
                    if (curIn)
                    {
                        if (!prevIn) poly.Add(Intersect(prev, cur, p0, p1));
                        poly.Add(cur);
                    }
                    else if (prevIn) poly.Add(Intersect(prev, cur, p0, p1));
                }
            }
            return poly.Count < 3 ? 0f : Mathf.Abs(SignedArea(poly.ToArray()));
        }

        static float Side(Vector2 a, Vector2 b, Vector2 p) => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

        static Vector2 Intersect(Vector2 p, Vector2 q, Vector2 a, Vector2 b)
        {
            float sp = Side(a, b, p), sq = Side(a, b, q);
            return p + (q - p) * (sp / (sp - sq));
        }

        static float SignedArea(Vector2[] poly)
        {
            float s = 0f;
            for (int i = 0; i < poly.Length; i++)
            {
                Vector2 p = poly[i], q = poly[(i + 1) % poly.Length];
                s += p.x * q.y - q.x * p.y;
            }
            return s * 0.5f;
        }
    }
}
