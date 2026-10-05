using UnityEngine;

namespace Lockdown.EditorTools
{
    // Hand-built imperfections, part 1: the smooth displacement field that takes the generated house out of true.
    // Every rendered vertex moves by a function of its position only, so parts that touch move together and no
    // cracks open. Horizontal moves (lean growing with height, low bulges) apply everywhere; vertical moves only
    // inside a sag profile (parapet tops, roof upstands, slab edges, the pergola), which is pinned where a part
    // rests on something. Floors, treads and the tile layers never get a profile, so walkable surfaces stay flat.
    // The collision mesh is never warped.

    public enum SagKind { None, Top, Bottom } // which edge of a part sags: Top = pinned at the base, Bottom = pinned at the top

    // A sag along a horizontal line a -> b (zero at both ends), weighted 0 at yPin and 1 at yFree.
    public struct WarpProfile
    {
        public bool active;
        public Vector3 a, b;
        public float yPin, yFree, amp;
        public int seed;
    }

    public class WarpField
    {
        readonly ImperfectionSettings s;
        readonly int seed;

        public WarpField(ImperfectionSettings settings, int seed)
        {
            s = settings;
            this.seed = seed;
        }

        public Vector3 Displace(Vector3 p, in WarpProfile prof) => Horizontal(p) + Vector3.up * Vertical(p, prof);

        Vector3 Horizontal(Vector3 p)
        {
            // Out of plumb: a lean that grows with height; its direction drifts slowly across the plan, so every wall
            // leans its own way. Plus low, soft bulges of hand-laid walls.
            float k = Mathf.Clamp(p.y, 0f, 2f * s.leanHeight) / s.leanHeight;
            Vector3 q = new Vector3(p.x, 0f, p.z) / s.leanWavelength;
            Vector3 lean = new Vector3(Noise.Signed(q, seed + 1), 0f, Noise.Signed(q, seed + 2)) * (s.leanMax * k);
            Vector3 r = new Vector3(p.x / s.bulgeWavelength, p.y / (2f * s.bulgeWavelength), p.z / s.bulgeWavelength);
            Vector3 bulge = new Vector3(Noise.Signed(r, seed + 3), 0f, Noise.Signed(r, seed + 4)) * s.bulgeAmp;
            return lean + bulge;
        }

        static float Vertical(Vector3 p, in WarpProfile prof)
        {
            if (!prof.active) return 0f;
            Vector3 ab = prof.b - prof.a;
            ab.y = 0f;
            float len = ab.magnitude;
            float t = len > 1e-4f ? Mathf.Clamp01(Vector3.Dot(new Vector3(p.x - prof.a.x, 0f, p.z - prof.a.z), ab) / (len * len)) : 0.5f;
            float shape = Mathf.Sin(Mathf.PI * t) * (0.6f + 0.4f * Noise.Value(new Vector3(t * len / 1.3f, 0.37f, 0.71f), prof.seed));
            float w = Mathf.Approximately(prof.yFree, prof.yPin) ? 1f : Mathf.Clamp01((p.y - prof.yPin) / (prof.yFree - prof.yPin));
            return -prof.amp * shape * w;
        }

        public static float VerticalOf(Vector3 p, in WarpProfile prof) => Vertical(p, prof);

        // Normal of the warped surface: the flat normal through the field's Jacobian (central differences).
        public Vector3 Normal(Vector3 p, Vector3 n, in WarpProfile prof)
        {
            const float h = 0.02f;
            Vector3 res = n;
            for (int i = 0; i < 3; i++)
            {
                Vector3 e = Vector3.zero;
                e[i] = h;
                Vector3 dD = (Displace(p + e, prof) - Displace(p - e, prof)) / (2f * h); // column i of grad D
                res[i] -= Vector3.Dot(dD, n);
            }
            return res.normalized;
        }
    }

    // Smooth value noise in [0, 1] (and [-1, 1]) on the integer lattice, hashed per seed.
    public static class Noise
    {
        static float Hash(int x, int y, int z, int seed)
        {
            unchecked
            {
                uint h = (uint)x * 374761393u + (uint)y * 668265263u + (uint)z * 2246822519u + (uint)seed * 3266489917u;
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xffffff) / 16777215f;
            }
        }

        public static float Value(Vector3 p, int seed)
        {
            int x = Mathf.FloorToInt(p.x), y = Mathf.FloorToInt(p.y), z = Mathf.FloorToInt(p.z);
            float fx = S(p.x - x), fy = S(p.y - y), fz = S(p.z - z);
            float L(int i, int j, int k) => Hash(x + i, y + j, z + k, seed);
            float x00 = Mathf.Lerp(L(0, 0, 0), L(1, 0, 0), fx), x10 = Mathf.Lerp(L(0, 1, 0), L(1, 1, 0), fx);
            float x01 = Mathf.Lerp(L(0, 0, 1), L(1, 0, 1), fx), x11 = Mathf.Lerp(L(0, 1, 1), L(1, 1, 1), fx);
            return Mathf.Lerp(Mathf.Lerp(x00, x10, fy), Mathf.Lerp(x01, x11, fy), fz);
            static float S(float t) => t * t * (3f - 2f * t);
        }

        public static float Signed(Vector3 p, int seed) => Value(p, seed) * 2f - 1f;
    }
}
