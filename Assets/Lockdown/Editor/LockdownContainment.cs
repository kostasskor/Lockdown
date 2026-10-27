using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Lockdown.EditorTools
{
    // Physics containment test (Task 9 r10, Task 16 pulled forward). Floods everything the real walker can reach from
    // the 12 start points, in two worlds: "build" (colliders of EditorOnly objects switched off, as a build strips them)
    // and "editor" (everything on, the Task 7 placeholders standing in for later buildings). Every move is the
    // walker's own physics: a CharacterController copy walks to each of the 8 neighbour cells (36 deg slope, 0.4 m
    // step, standing or crouched, stepping off a ledge falls to whatever is below), and jumps where a walk failed or
    // dropped (a real 1.0 m sprint jump arc, with a mantle onto any top up to 1.4 m above the feet at the apex).
    // Nodes are made where the controller lands (multi-level: roofs, decks, bridges). The grid is the play tile's
    // 513 grid, so the Python checks can index it. Runs a slice per editor update; progress in
    // Screenshots/task09/containment_status.txt. Output: report + map in Screenshots/task09/containment_<label>_<world>,
    // graph in SourceData/playground/reach_physics_<world>.{json,bin} (read by Tools/review/playground_checks.py).
    [InitializeOnLoad]
    public static class LockdownContainment
    {
        // The walker sets these in LockdownWalker.Awake (hard-coded there, mirrored here).
        const float SlopeLimit = 36f, StepOffset = 0.4f, Skin = 0.03f;
        const float Sprint = 6.5f, CrouchSpeed = 2.2f, Gravity = 20f, MantleReach = 1.4f;
        const float MergeY = 0.3f;        // two landings in one column closer than this are one node
        const float OutsideCap = 25f;     // nodes farther than this beyond the frame are kept but not expanded
        const int ProbeLayer = 2;         // Ignore Raycast: the probe controllers never see themselves
        const double SliceMs = 150.0;
        const string EditorOnlyTag = "EditorOnly";

        enum Move : byte { Walk = 0, Crouch = 1, Drop = 2, Jump = 3, Mantle = 4 }

        [Serializable] class Point { public string name, kind; public float x, y, z; }
        [Serializable] class PlayMeta
        {
            public string file;
            public int resolution;
            public float size, x0, z0, frame_rotation_deg, flag_b_height;
            public float[] frame_center, frame_size;
            public List<Point> stakes;
        }
        [Serializable] class Line { public string name; public float[] xs, zs; }
        [Serializable] class Extension { public string name; public float[] u, v; }
        [Serializable] class GreyMeta { public List<Line> closures; public List<Extension> extensions; }

        static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string PlayDir => Path.Combine(Root, "SourceData", "playground");
        static string ShotDir => Path.Combine(Root, "Screenshots", "task09");
        static string StatusPath => Path.Combine(ShotDir, "containment_status.txt");
        static string DisabledList => Path.Combine(Root, "Temp", "lockdown_containment_disabled.txt");

        // A run cut off by a domain reload leaves colliders switched off: put them back on the next load.
        static LockdownContainment()
        {
            EditorApplication.delayCall += () => { if (File.Exists(DisabledList)) Restore(); };
        }

        [MenuItem("Lockdown/Review/Containment Test")]
        public static void RunMenu() => Start(EditorPrefs.GetString("Lockdown.ContainmentLabel", "latest"));

        [MenuItem("Lockdown/Review/Containment Restore")]
        public static void Restore()
        {
            if (!File.Exists(DisabledList)) { Debug.Log("[Lockdown] Containment: nothing to restore."); return; }
            int n = 0;
            foreach (var line in File.ReadAllLines(DisabledList))
                if (GlobalObjectId.TryParse(line, out var id) && GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) is Collider c) { c.enabled = true; n++; }
            File.Delete(DisabledList);
            Debug.Log($"[Lockdown] Containment: {n} colliders switched back on.");
        }

        // ---------------------------------------------------------------- job state

        class Graph
        {
            public readonly List<Vector3> pos = new List<Vector3>();
            public readonly List<byte> low = new List<byte>();          // bit 0: no standing headroom (crouched only); bit 1: on an EditorOnly top
            public readonly List<int> edgeStart = new List<int>(), edgeCount = new List<int>();
            public readonly List<int> eTo = new List<int>();
            public readonly List<float> eCost = new List<float>();      // metres at sprint (seconds x 6.5)
            public readonly List<byte> eType = new List<byte>();
            public readonly List<int> eFrom = new List<int>();
            public readonly Dictionary<int, List<int>> byCol = new Dictionary<int, List<int>>();
            public readonly Queue<int> queue = new Queue<int>();
            public readonly List<int> starts = new List<int>();
            public int tileEdgeHits, capped;
        }

        static Job _job;

        class Job
        {
            public string label;
            public string[] worlds;
            public int world;
            public PlayMeta meta;
            public List<Line> closures;
            public List<Extension> extensions;   // boundary jogs: frame-coordinate rectangles that count as inside
            public float cos, sin, cx, cz, hu, hv, step;
            public int res;
            public Graph g;
            public CharacterController stand, crouch;
            public GameObject probe;
            public List<Collider> disabled = new List<Collider>();
            public CharacterController walkerCc;
            public bool walkerOn;
            public Stopwatch clock = Stopwatch.StartNew();
            public double lastStatus;
            public int mask;
            public float standH, crouchH, radius, jumpV;
            public List<string> summary = new List<string>();
        }

        public static void Start(string label, bool buildOnly = false)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogError("[Lockdown] Containment: exit Play mode first."); return; }
            if (_job != null) { Debug.LogError("[Lockdown] Containment: a run is already going."); return; }
            Directory.CreateDirectory(ShotDir);
            File.WriteAllText(StatusPath, $"STARTING {label}\n");   // never leave the last run's DONE for a poller
            var j = new Job { label = label, worlds = buildOnly ? new[] { "build" } : new[] { "build", "editor" } };
            j.meta = JsonUtility.FromJson<PlayMeta>(File.ReadAllText(Path.Combine(PlayDir, "play_meta.json")));
            j.closures = File.Exists(LockdownGreybox.MetaPath)
                ? JsonUtility.FromJson<GreyMeta>(File.ReadAllText(LockdownGreybox.MetaPath)).closures ?? new List<Line>()
                : new List<Line>();
            j.extensions = Extensions();
            float rot = j.meta.frame_rotation_deg * Mathf.Deg2Rad;
            j.cos = Mathf.Cos(rot); j.sin = Mathf.Sin(rot);
            j.cx = j.meta.frame_center[0]; j.cz = j.meta.frame_center[1];
            j.hu = j.meta.frame_size[0] / 2f; j.hv = j.meta.frame_size[1] / 2f;
            j.res = j.meta.resolution;
            j.step = j.meta.size / (j.res - 1);
            var w = Object.FindFirstObjectByType<Lockdown.LockdownWalker>();
            j.radius = w != null ? w.radius : 0.35f;
            j.standH = w != null ? w.standHeight : 1.8f;
            j.crouchH = w != null ? w.crouchHeight : 1.2f;
            float jumpHeight = w != null ? w.jumpHeight : 1.0f;
            j.jumpV = Mathf.Sqrt(2f * jumpHeight * Gravity);
            j.walkerCc = w != null ? w.GetComponent<CharacterController>() : null;
            j.walkerOn = j.walkerCc != null && j.walkerCc.enabled;
            if (j.walkerCc != null) j.walkerCc.enabled = false;
            j.mask = ~(1 << ProbeLayer);
            j.probe = new GameObject("ContainmentProbe") { hideFlags = HideFlags.DontSave, layer = ProbeLayer };
            var crouchGo = new GameObject("ContainmentProbeCrouch") { hideFlags = HideFlags.DontSave, layer = ProbeLayer };
            j.stand = MakeCc(j.probe, j.standH, j.radius);
            j.crouch = MakeCc(crouchGo, j.crouchH, j.radius);
            j.crouch.enabled = false;
            _job = j;
            BeginWorld(j);
            EditorApplication.update += Tick;
            Debug.Log($"[Lockdown] Containment test '{label}' started ({string.Join(", ", j.worlds)}). Progress: {StatusPath}");
        }

        static CharacterController MakeCc(GameObject go, float h, float r)
        {
            var cc = go.AddComponent<CharacterController>();
            cc.radius = r;
            cc.height = h;
            cc.center = new Vector3(0f, h * 0.5f, 0f);
            cc.slopeLimit = SlopeLimit;
            cc.stepOffset = StepOffset;
            cc.skinWidth = Skin;
            cc.minMoveDistance = 0f;
            return cc;
        }

        static bool IsEditorOnly(Transform t)
        {
            for (; t != null; t = t.parent) if (t.CompareTag(EditorOnlyTag)) return true;
            return false;
        }

        static void BeginWorld(Job j)
        {
            string world = j.worlds[j.world];
            if (world == "build")
            {
                var ids = new List<string>();
                foreach (var c in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
                {
                    if (!c.enabled || c is CharacterController || !IsEditorOnly(c.transform)) continue;
                    c.enabled = false;
                    j.disabled.Add(c);
                    ids.Add(GlobalObjectId.GetGlobalObjectIdSlow(c).ToString());
                }
                Directory.CreateDirectory(Path.GetDirectoryName(DisabledList));
                File.WriteAllLines(DisabledList, ids);
            }
            Physics.SyncTransforms();
            j.g = new Graph();
            foreach (var s in j.meta.stakes)
            {
                if (!s.kind.StartsWith("start_", StringComparison.Ordinal)) continue;
                var top = new Vector3(s.x, s.y + 3f, s.z);
                Vector3 p = Physics.Raycast(top, Vector3.down, out var hit, 10f, j.mask, QueryTriggerInteraction.Ignore) ? hit.point : new Vector3(s.x, s.y, s.z);
                Place(j.stand, p + Vector3.up * 0.05f);
                Settle(j.stand);
                int sid = Node(j, j.stand.transform.position);
                if (sid >= 0 && !j.g.starts.Contains(sid)) j.g.starts.Add(sid);
            }
        }

        static void EndWorld(Job j)
        {
            string world = j.worlds[j.world];
            try { Report(j, world); }
            finally
            {
                foreach (var c in j.disabled) if (c != null) c.enabled = true;
                j.disabled.Clear();
                if (File.Exists(DisabledList)) File.Delete(DisabledList);
                Physics.SyncTransforms();
            }
        }

        static void Finish(Job j, string error = null)
        {
            EditorApplication.update -= Tick;
            foreach (var c in j.disabled) if (c != null) c.enabled = true;
            if (File.Exists(DisabledList)) File.Delete(DisabledList);
            if (j.walkerCc != null) j.walkerCc.enabled = j.walkerOn;
            if (j.probe != null) Object.DestroyImmediate(j.probe);
            if (j.crouch != null) Object.DestroyImmediate(j.crouch.gameObject);
            _job = null;
            string msg = error ?? $"DONE {j.label} in {j.clock.Elapsed.TotalSeconds:0} s\n" + string.Join("\n", j.summary);
            File.WriteAllText(StatusPath, msg + "\n");
            if (error != null) Debug.LogError("[Lockdown] Containment: " + error);
            else Debug.Log("[Lockdown] Containment " + msg);
        }

        static void Tick()
        {
            var j = _job;
            if (j == null) { EditorApplication.update -= Tick; return; }
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Finish(j, "stopped: Play mode"); return; }
            var culture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;   // decimal points in the report and the JSON
            try
            {
                var slice = Stopwatch.StartNew();
                while (j.g.queue.Count > 0 && slice.Elapsed.TotalMilliseconds < SliceMs) Expand(j, j.g.queue.Dequeue());
                if (j.clock.Elapsed.TotalSeconds - j.lastStatus > 2.0)
                {
                    j.lastStatus = j.clock.Elapsed.TotalSeconds;
                    File.WriteAllText(StatusPath, $"RUNNING {j.label}: world {j.worlds[j.world]} ({j.world + 1}/{j.worlds.Length}), " +
                        $"nodes {j.g.pos.Count}, queue {j.g.queue.Count}, edges {j.g.eTo.Count}, {j.clock.Elapsed.TotalSeconds:0} s\n");
                }
                if (j.g.queue.Count > 0) return;
                EndWorld(j);
                j.world++;
                if (j.world >= j.worlds.Length) { Finish(j); return; }
                BeginWorld(j);
            }
            catch (Exception e)
            {
                Finish(j, "failed: " + e);
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
            }
        }

        // ---------------------------------------------------------------- grid and frame

        static int Col(Job j, Vector3 p)
        {
            int c = Mathf.RoundToInt((p.x - j.meta.x0) / j.step), r = Mathf.RoundToInt((p.z - j.meta.z0) / j.step);
            if (c < 0 || r < 0 || c >= j.res || r >= j.res) return -1;
            return r * j.res + c;
        }

        static Vector3 Centre(Job j, int col, float y) => new Vector3(j.meta.x0 + (col % j.res) * j.step, y, j.meta.z0 + (col / j.res) * j.step);

        static Vector2 Frame(Job j, float x, float z)
        {
            float dx = x - j.cx, dz = z - j.cz;
            return new Vector2(dx * j.cos + dz * j.sin, -dx * j.sin + dz * j.cos);
        }

        static float Outside(Job j, Vector3 p) => Outside(Frame(j, p.x, p.z), j.hu, j.hv, j.extensions);

        // Distance outside the frame (frame coordinates), where a declared yard beyond the frame line counts as inside too.
        static float Outside(Vector2 f, float hu, float hv, List<Extension> extensions)
        {
            float du = Mathf.Max(Mathf.Abs(f.x) - hu, 0f), dv = Mathf.Max(Mathf.Abs(f.y) - hv, 0f);
            float d = Mathf.Sqrt(du * du + dv * dv);
            foreach (var e in extensions)
            {
                float eu = Mathf.Max(Mathf.Max(e.u[0] - f.x, f.x - e.u[1]), 0f), ev = Mathf.Max(Mathf.Max(e.v[0] - f.y, f.y - e.v[1]), 0f);
                d = Mathf.Min(d, Mathf.Sqrt(eu * eu + ev * ev));
            }
            return d;
        }

        static List<Extension> Extensions() =>
            (File.Exists(LockdownGreybox.MetaPath) ? JsonUtility.FromJson<GreyMeta>(File.ReadAllText(LockdownGreybox.MetaPath))?.extensions : null) ?? new List<Extension>();

        // The node at this landing (an existing one in the column within MergeY, else a new one), or -1 off the tile.
        static int Node(Job j, Vector3 p)
        {
            var g = j.g;
            int col = Col(j, p);
            if (col < 0) { g.tileEdgeHits++; return -1; }
            if (!g.byCol.TryGetValue(col, out var list)) g.byCol[col] = list = new List<int>();
            foreach (int n in list) if (Mathf.Abs(g.pos[n].y - p.y) < MergeY) return n;
            int id = g.pos.Count;
            g.pos.Add(p);
            g.low.Add((byte)((Clear(j, p, j.standH) ? 0 : 1) | (StandsOnEditorOnly(j, p) != null ? 2 : 0)));
            g.edgeStart.Add(0);
            g.edgeCount.Add(0);
            list.Add(id);
            if (Outside(j, p) > OutsideCap) g.capped++;
            else g.queue.Enqueue(id);
            return id;
        }

        static bool Clear(Job j, Vector3 foot, float h)
        {
            float r = j.radius * 0.95f;
            return !Physics.CheckCapsule(foot + Vector3.up * (r + 0.06f), foot + Vector3.up * (h - r), r, j.mask, QueryTriggerInteraction.Ignore);
        }

        static void Place(CharacterController cc, Vector3 foot)
        {
            cc.transform.position = foot;
            Physics.SyncTransforms();
        }

        static bool Settle(CharacterController cc, int maxCalls = 100)
        {
            for (int i = 0; i < maxCalls && !cc.isGrounded; i++) cc.Move(Vector3.down * 0.5f);
            return cc.isGrounded;
        }

        // ---------------------------------------------------------------- moves

        static readonly Vector2Int[] Dirs =
        {
            new Vector2Int(1, 0), new Vector2Int(1, 1), new Vector2Int(0, 1), new Vector2Int(-1, 1),
            new Vector2Int(-1, 0), new Vector2Int(-1, -1), new Vector2Int(0, -1), new Vector2Int(1, -1),
        };

        static void Expand(Job j, int n)
        {
            var g = j.g;
            g.edgeStart[n] = g.eTo.Count;
            Vector3 a = g.pos[n];
            int col = Col(j, a);
            int r0 = col / j.res, c0 = col % j.res;
            bool low = (g.low[n] & 1) != 0;
            for (int d = 0; d < Dirs.Length; d++)
            {
                int rr = r0 + Dirs[d].y, cc = c0 + Dirs[d].x;
                Vector3 target;
                if (rr < 0 || cc < 0 || rr >= j.res || cc >= j.res) { g.tileEdgeHits++; continue; }
                target = Centre(j, rr * j.res + cc, a.y);
                bool crouched = low;
                bool ok = TryWalk(j, low ? j.crouch : j.stand, a, target, out Vector3 end);
                if (!ok && !low) { ok = TryWalk(j, j.crouch, a, target, out end); crouched = ok; }
                float drop = a.y - end.y;
                if (ok)
                {
                    int m = Node(j, end);
                    if (m >= 0 && m != n)
                    {
                        float dist = Horizontal(a, end);
                        var type = drop > 0.6f ? Move.Drop : crouched ? Move.Crouch : Move.Walk;
                        float cost = crouched ? dist * Sprint / CrouchSpeed : dist + (drop > 1f ? 0.15f * Sprint : 0f);
                        AddEdge(g, n, m, cost, type);
                    }
                }
                // Jump where the walk was blocked or fell off something: over gaps, onto walls, boxes and barriers.
                if (!low && (!ok || drop > 0.6f))
                {
                    var dir = new Vector3(Dirs[d].x, 0f, Dirs[d].y).normalized;
                    if (TryJump(j, a, dir, out Vector3 land, out float t, out bool mantle))
                    {
                        int m = Node(j, land);
                        if (m >= 0 && m != n) AddEdge(g, n, m, (t + 0.3f) * Sprint, mantle ? Move.Mantle : Move.Jump);
                    }
                }
            }
            g.edgeCount[n] = g.eTo.Count - g.edgeStart[n];
        }

        static void AddEdge(Graph g, int from, int to, float cost, Move type)
        {
            g.eFrom.Add(from);
            g.eTo.Add(to);
            g.eCost.Add(cost);
            g.eType.Add((byte)type);
        }

        static float Horizontal(Vector3 a, Vector3 b) => new Vector2(b.x - a.x, b.z - a.z).magnitude;

        // Walk from a toward target's xz with the controller (steps and slopes as the walker), then fall until grounded.
        static bool TryWalk(Job j, CharacterController cc, Vector3 a, Vector3 target, out Vector3 end)
        {
            bool swap = cc == j.crouch;
            if (swap) { j.stand.enabled = false; j.crouch.enabled = true; }
            try
            {
                Place(cc, a + Vector3.up * 0.02f);
                var delta = new Vector3(target.x - a.x, 0f, target.z - a.z);
                int k = Mathf.Max(1, Mathf.CeilToInt(delta.magnitude / 0.18f));
                for (int i = 0; i < k; i++) cc.Move(delta / k + Vector3.down * 0.06f);
                Settle(cc);
                end = cc.transform.position;
                return cc.isGrounded && Horizontal(end, target) <= 0.2f;
            }
            finally
            {
                if (swap) { j.crouch.enabled = false; j.stand.enabled = true; }
            }
        }

        // A sprint jump along dir: the controller flies the real arc; pressed against something near the apex it
        // mantles onto a top up to 1.4 m above the take-off feet if the walker fits there.
        static bool TryJump(Job j, Vector3 a, Vector3 dir, out Vector3 land, out float t, out bool mantle)
        {
            var cc = j.stand;
            Place(cc, a + Vector3.up * 0.02f);
            float vy = j.jumpV;
            const float dt = 0.03f;
            bool blocked = false, tried = false;
            mantle = false;
            t = 0f;
            land = a;
            while (t < 2.0f)
            {
                Vector3 before = cc.transform.position;
                cc.Move(dir * (Sprint * dt) + Vector3.up * (vy * dt));
                vy -= Gravity * dt;
                t += dt;
                Vector3 p = cc.transform.position;
                if (Horizontal(before, p) < 0.5f * Sprint * dt) blocked = true;
                if (blocked && !tried && vy <= 0.6f)
                {
                    tried = true;
                    if (Mantle(j, a.y, p, dir, out land)) { mantle = true; t += 0.25f; return true; }
                }
                if (vy < 0f && cc.isGrounded) { land = p; break; }
                if (p.y < a.y - 40f) return false;
            }
            if (!cc.isGrounded) return false;
            return Horizontal(a, land) > 0.3f || Mathf.Abs(land.y - a.y) > MergeY;
        }

        static bool Mantle(Job j, float footY, Vector3 p, Vector3 dir, out Vector3 land)
        {
            land = p;
            float r = j.radius * 0.95f;
            foreach (float d in new[] { 0.35f, 0.6f, 0.9f })
            {
                Vector3 q = p + dir * d;
                var origin = new Vector3(q.x, footY + MantleReach + 0.02f, q.z);
                if (!Physics.Raycast(origin, Vector3.down, out var hit, MantleReach - StepOffset + 0.02f, j.mask, QueryTriggerInteraction.Ignore)) continue;
                if (hit.normal.y < 0.6f) continue;
                Vector3 top = hit.point;
                if (!Clear(j, top, j.crouchH)) continue;
                // the body has to pass over the edge at the top's height
                Vector3 lift = new Vector3(p.x, top.y + 0.05f, p.z);
                float h = j.crouchH;
                if (Physics.CheckCapsule(lift + Vector3.up * (r + 0.01f), lift + Vector3.up * (h - r), r, j.mask, QueryTriggerInteraction.Ignore)) continue;
                if (Physics.CapsuleCast(lift + Vector3.up * (r + 0.01f), lift + Vector3.up * (h - r), r, dir, out _, d, j.mask, QueryTriggerInteraction.Ignore)) continue;
                land = top;
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- roadblock seal test

        // Runs the real walker controller at every roadblock: sprinting at the clip along its whole length (and the
        // flanks), straight and +-20 deg, bunny-hopping or walking, mantling whatever it presses against. Fails if
        // the body gets past the outer face of the invisible wall that sits on the outer barrier row.
        [MenuItem("Lockdown/Review/Roadblock Seal Test")]
        public static void SealMenu() => SealTest(EditorPrefs.GetString("Lockdown.ContainmentLabel", "latest"));

        public static void SealTest(string label)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogError("[Lockdown] Seal test: exit Play mode first."); return; }
            if (_job != null) { Debug.LogError("[Lockdown] Seal test: a containment run is going."); return; }
            var meta = JsonUtility.FromJson<PlayMeta>(File.ReadAllText(Path.Combine(PlayDir, "play_meta.json")));
            float rot = meta.frame_rotation_deg * Mathf.Deg2Rad;
            Vector3 uAxis = new Vector3(Mathf.Cos(rot), 0f, Mathf.Sin(rot)), vAxis = new Vector3(-Mathf.Sin(rot), 0f, Mathf.Cos(rot));
            float cx = meta.frame_center[0], cz = meta.frame_center[1];
            var exts = Extensions();
            var w = Object.FindFirstObjectByType<Lockdown.LockdownWalker>();
            var j = new Job { label = label, mask = ~(1 << ProbeLayer), radius = w != null ? w.radius : 0.35f, standH = w != null ? w.standHeight : 1.8f, crouchH = w != null ? w.crouchHeight : 1.2f };
            j.jumpV = Mathf.Sqrt(2f * (w != null ? w.jumpHeight : 1.0f) * Gravity);
            var wcc = w != null ? w.GetComponent<CharacterController>() : null;
            bool wasOn = wcc != null && wcc.enabled;
            if (wcc != null) wcc.enabled = false;
            var probe = new GameObject("SealProbe") { hideFlags = HideFlags.DontSave, layer = ProbeLayer };
            j.stand = MakeCc(probe, j.standH, j.radius);

            // the invisible wall boxes of every roadblock, from the scene
            var groups = new Dictionary<string, List<Transform>>();
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                int i = t.name.IndexOf(" roadblock wall ", StringComparison.Ordinal);
                if (i < 0) continue;
                string rb = t.name.Substring(0, i);
                if (!groups.TryGetValue(rb, out var l)) groups[rb] = l = new List<Transform>();
                l.Add(t);
            }
            var lines = new List<string> { $"Roadblock seal test '{label}'  (walker r {j.radius:0.00}, jump v {j.jumpV:0.0}, sprint {Sprint})" };
            int leaks = 0, runs = 0;
            var disabled = new List<Collider>();
            try
            {
                foreach (string world in new[] { "build", "editor" })
                {
                    if (world == "build")
                        foreach (var c in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
                            if (c.enabled && !(c is CharacterController) && IsEditorOnly(c.transform)) { c.enabled = false; disabled.Add(c); }
                    Physics.SyncTransforms();
                    foreach (var kv in groups)
                    {
                        // outward sign from the wall's frame-u, outer face and corridor extent in frame coordinates
                        float sgn = 0f, face = 0f, v0 = 1e9f, v1 = -1e9f, vm0 = 1e9f, vm1 = -1e9f, topY = -1e9f;
                        foreach (var t in kv.Value)
                        {
                            var col = t.GetComponent<Collider>();
                            if (col != null) topY = Mathf.Max(topY, col.bounds.max.y);
                            var d = t.position - new Vector3(cx, 0f, cz);
                            float u = Vector3.Dot(d, uAxis), v = Vector3.Dot(d, vAxis);
                            sgn = u < 0f ? -1f : 1f;
                            var bc = t.GetComponent<BoxCollider>();
                            float halfU = bc != null ? Mathf.Abs(Vector3.Dot(t.rotation * Vector3.forward, uAxis)) * bc.size.z * 0.5f * t.lossyScale.z
                                                       + Mathf.Abs(Vector3.Dot(t.rotation * Vector3.right, uAxis)) * bc.size.x * 0.5f * t.lossyScale.x : 0.15f;
                            float halfV = bc != null ? Mathf.Abs(Vector3.Dot(t.rotation * Vector3.forward, vAxis)) * bc.size.z * 0.5f * t.lossyScale.z
                                                       + Mathf.Abs(Vector3.Dot(t.rotation * Vector3.right, vAxis)) * bc.size.x * 0.5f * t.lossyScale.x : 1f;
                            face = Mathf.Max(face, Mathf.Abs(u) + halfU);
                            v0 = Mathf.Min(v0, v - halfV); v1 = Mathf.Max(v1, v + halfV);
                            if (!t.name.EndsWith(" N", StringComparison.Ordinal) && !t.name.EndsWith(" S", StringComparison.Ordinal))
                            { vm0 = Mathf.Min(vm0, v - halfV); vm1 = Mathf.Max(vm1, v + halfV); }   // the run of walls on the barriers
                        }
                        int worst = 0; float reach = -1e9f; Vector2 worstAt = Vector2.zero; string how = "";
                        int n = 0;
                        for (int lat = 0; lat <= 40; lat++)
                        {
                            float v = Mathf.Lerp(v0 - 1.0f, v1 + 1.0f, lat / 40f);
                            if (Mathf.Abs(v) > meta.frame_size[1] / 2f - 0.8f) continue;     // not a start inside the frame
                            float limit = meta.frame_size[0] / 2f + 0.3f;                    // never beyond the frame edge (or a declared yard)
                            foreach (float ang in new[] { 0f, 20f, -20f })
                                foreach (bool hop in new[] { true, false })
                                {
                                    var dirU = Quaternion.AngleAxis(ang, Vector3.up) * (uAxis * sgn);
                                    var start = new Vector3(cx, 0f, cz) + uAxis * (sgn * (face - 12f)) + vAxis * v;
                                    start.y = topY + 5f;
                                    if (!Physics.Raycast(start, Vector3.down, out var hit, 200f, j.mask, QueryTriggerInteraction.Ignore)) continue;
                                    Place(j.stand, hit.point + Vector3.up * 0.05f);
                                    if (!Settle(j.stand)) continue;
                                    n++; runs++;
                                    float vy = 0f, best = -1e9f, outBy = 0f; bool tried = false; int mantles = 0; float crossV = -1e9f;
                                    for (int s = 0; s < 330; s++)
                                    {
                                        Vector3 before = j.stand.transform.position;
                                        bool grounded = j.stand.isGrounded;
                                        if (grounded && vy <= 0f) { vy = hop ? j.jumpV : -1f; tried = false; }
                                        j.stand.Move(dirU * (Sprint * 0.03f) + Vector3.up * (vy * 0.03f));
                                        vy -= Gravity * 0.03f;
                                        Vector3 p = j.stand.transform.position;
                                        bool blocked = Horizontal(before, p) < 0.5f * Sprint * 0.03f;
                                        if (blocked && !tried && !grounded && vy <= 0.6f)
                                        {
                                            tried = true;
                                            if (Mantle(j, before.y, p, dirU, out var land)) { Place(j.stand, land + Vector3.up * 0.02f); vy = 0f; mantles++; }
                                        }
                                        if (p.y < hit.point.y - 80f) break;
                                        float u = Vector3.Dot(j.stand.transform.position - new Vector3(cx, 0f, cz), uAxis) * sgn;
                                        if (u > best) best = u;
                                        var fp = j.stand.transform.position - new Vector3(cx, 0f, cz);
                                        outBy = Mathf.Max(outBy, Outside(new Vector2(Vector3.Dot(fp, uAxis), Vector3.Dot(fp, vAxis)), meta.frame_size[0] / 2f, meta.frame_size[1] / 2f, exts));
                                        if (u > face + 0.05f && crossV < -1e8f) crossV = Vector3.Dot(j.stand.transform.position - new Vector3(cx, 0f, cz), vAxis);
                                    }
                                    if (best > reach) { reach = best; worstAt = new Vector2(v, best); how = $"{(hop ? "hop" : "walk")} {ang:0}deg"; }
                                    // a leak: past the wall line while in front of the walls (their whole run, laps included),
                                    // or past the frame edge anywhere (a declared yard beyond it is inside)
                                    bool through = crossV > -1e8f && crossV >= v0 + 0.1f && crossV <= v1 - 0.1f;
                                    if (through || outBy > 0.3f)
                                    {
                                        worst++;
                                        if (worst <= 4) lines.Add($"    leak run: start v {v:0.0} {(hop ? "hop" : "walk")} {ang:0}deg crossed the wall line at v {crossV:0.0} reached u {best:0.00} (frame limit {limit:0.00}) mantles {mantles} end y {j.stand.transform.position.y - hit.point.y:+0.0}");
                                    }
                                }
                        }
                        leaks += worst;
                        lines.Add($"{world,-6} {kv.Key,-34} walls {kv.Value.Count,2}  face u {face:0.00}  furthest foot u {reach:0.00} ({how} at v {worstAt.x:0.0})  margin {face - reach:+0.00;-0.00} m  runs {n}  {(n == 0 ? "NO RUNS (no ground found)" : worst == 0 ? "SEALED" : "LEAK x" + worst)}");
                        if (n == 0) leaks++;
                    }
                }
            }
            finally
            {
                foreach (var c in disabled) if (c != null) c.enabled = true;
                Physics.SyncTransforms();
                Object.DestroyImmediate(probe);
                if (wcc != null) wcc.enabled = wasOn;
            }
            lines.Add($"TOTAL runs {runs}, leaks {leaks}");
            Directory.CreateDirectory(ShotDir);
            File.WriteAllLines(Path.Combine(ShotDir, $"seal_{label}.txt"), lines);
            foreach (var l in lines) Debug.Log("[Lockdown] " + l);
        }

        // ---------------------------------------------------------------- report, map, export

        class Exit { public Vector3 at; public Vector2 from, to; public float length; public int count, comp; public HashSet<Move> types = new HashSet<Move>(); public float width = -1f; public string names = "", side = ""; public string placeholder; public List<int> moves = new List<int>(); }

        static void Report(Job j, string world)
        {
            var g = j.g;
            int N = g.pos.Count;
            float cell = j.step * j.step;
            var outsideCols = new HashSet<int>();
            var insideCols = new HashSet<int>();
            var outside = new bool[N];
            for (int n = 0; n < N; n++)
            {
                outside[n] = Outside(j, g.pos[n]) > 0f;
                (outside[n] ? outsideCols : insideCols).Add(Col(j, g.pos[n]));
            }
            // leaks: 8-connected groups of outside columns
            var comp = new Dictionary<int, int>();
            var compArea = new List<int>();
            var compFar = new List<float>();
            foreach (int c0 in outsideCols)
            {
                if (comp.ContainsKey(c0)) continue;
                int id = compArea.Count;
                compArea.Add(0); compFar.Add(0f);
                var st = new Stack<int>();
                st.Push(c0); comp[c0] = id;
                while (st.Count > 0)
                {
                    int c = st.Pop();
                    compArea[id]++;
                    compFar[id] = Mathf.Max(compFar[id], Outside(j, Centre(j, c, 0f)));
                    int r = c / j.res, k = c % j.res;
                    for (int dr = -1; dr <= 1; dr++)
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        int rr = r + dr, cc = k + dc;
                        if (rr < 0 || cc < 0 || rr >= j.res || cc >= j.res) continue;
                        int q = rr * j.res + cc;
                        if (outsideCols.Contains(q) && !comp.ContainsKey(q)) { comp[q] = id; st.Push(q); }
                    }
                }
            }
            // exits: edges from inside to outside, grouped into runs (single linkage, 1.5 m) along the frame edge
            var cross = new List<int>();
            for (int e = 0; e < g.eTo.Count; e++)
                if (!outside[g.eFrom[e]] && outside[g.eTo[e]]) cross.Add(e);
            var mids = cross.Select(e => (g.pos[g.eFrom[e]] + g.pos[g.eTo[e]]) * 0.5f).ToList();
            var parent = Enumerable.Range(0, cross.Count).ToArray();
            int FindRoot(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            for (int a = 0; a < cross.Count; a++)
                for (int b = a + 1; b < cross.Count; b++)
                    if (Horizontal(mids[a], mids[b]) < 1.5f) parent[FindRoot(a)] = FindRoot(b);
            var runs = new Dictionary<int, List<int>>();
            for (int a = 0; a < cross.Count; a++)
            {
                int root = FindRoot(a);
                if (!runs.TryGetValue(root, out var l)) runs[root] = l = new List<int>();
                l.Add(a);
            }
            var exits = new List<Exit>();
            foreach (var run in runs.Values)
            {
                // the run's middle member stands for it; its extent is the farthest pair of members along the edge
                var sorted = run.OrderBy(i => { var f = Frame(j, mids[i].x, mids[i].z); return Mathf.Abs(f.x) - j.hu > Mathf.Abs(f.y) - j.hv ? f.y : f.x; }).ToList();
                int k0 = sorted[sorted.Count / 2];
                int e0 = cross[k0];
                var ex = new Exit { at = mids[k0], comp = comp[Col(j, g.pos[g.eTo[e0]])], count = run.Count };
                ex.length = Horizontal(mids[sorted[0]], mids[sorted[sorted.Count - 1]]);
                ex.from = Frame(j, mids[sorted[0]].x, mids[sorted[0]].z);
                ex.to = Frame(j, mids[sorted[sorted.Count - 1]].x, mids[sorted[sorted.Count - 1]].z);
                foreach (int i in run) ex.types.Add((Move)g.eType[cross[i]]);
                Describe(j, ex, g.pos[g.eFrom[e0]], g.pos[g.eTo[e0]]);
                exits.Add(ex);
            }
            exits = exits.OrderBy(x => x.side).ThenBy(x => Mathf.Atan2(Frame(j, x.at.x, x.at.z).y, Frame(j, x.at.x, x.at.z).x)).ToList();
            // closure crossings: any move whose segment crosses a closure line
            var crossings = new List<Exit>();
            foreach (var cl in j.closures)
                for (int e = 0; e < g.eTo.Count; e++)
                {
                    Vector3 a = g.pos[g.eFrom[e]], b = g.pos[g.eTo[e]];
                    for (int i = 0; i + 1 < cl.xs.Length; i++)
                    {
                        if (!Cross(a.x, a.z, b.x, b.z, cl.xs[i], cl.zs[i], cl.xs[i + 1], cl.zs[i + 1])) continue;
                        Vector3 mid = (a + b) * 0.5f;
                        var ex = crossings.FirstOrDefault(x => x.names.StartsWith(cl.name, StringComparison.Ordinal) && Horizontal(x.at, mid) < 4f);
                        if (ex == null)
                        {
                            ex = new Exit { at = mid, placeholder = StandsOnEditorOnly(j, a) };   // a name for the report
                            Describe(j, ex, a, b);
                            ex.names = cl.name + " | " + ex.names;
                            crossings.Add(ex);
                        }
                        ex.moves.Add(e);
                        ex.count++;
                        ex.types.Add((Move)g.eType[e]);
                    }
                }

            var sb = new StringBuilder();
            sb.AppendLine($"=== CONTAINMENT (physics, world: {world}) — {j.label}, {DateTime.Now:yyyy-MM-dd HH:mm} ===");
            sb.AppendLine($"walker: r {j.radius} m, stand {j.standH} / crouch {j.crouchH} m, slope {SlopeLimit} deg, step {StepOffset} m, jump {j.jumpV * j.jumpV / (2 * Gravity):0.0} m (mantle <= {MantleReach} m), drops any height; grid {j.step:0.000} m (play tile 513)");
            if (world == "build") sb.AppendLine($"EditorOnly colliders switched off: {j.disabled.Count}");
            sb.AppendLine($"nodes {N}, moves {g.eTo.Count}, reachable inside the frame {insideCols.Count * cell:0} m2");
            sb.AppendLine($"REACHABLE OUTSIDE the 150x104 frame: {outsideCols.Count * cell:0} m2 ({outsideCols.Count} cells) in {compArea.Count} leak(s); " +
                          $"nodes beyond {OutsideCap} m not expanded: {g.capped}; moves off the play tile: {g.tileEdgeHits}");
            for (int c = 0; c < compArea.Count; c++)
            {
                sb.AppendLine($"  leak {c + 1}: {compArea[c] * cell:0} m2, reaches {compFar[c]:0.0} m beyond the frame");
                foreach (var ex in exits.Where(x => x.comp == c)) sb.AppendLine("    exit " + ExitLine(j, ex));
            }
            var slits = exits.Concat(crossings).Where(x => x.width > 0f && x.width < 1.5f).ToList();
            sb.AppendLine($"SLITS (passages 0.7-1.5 m wide on an exit or a closure crossing): {slits.Count}");
            foreach (var s in slits) sb.AppendLine("  " + ExitLine(j, s));
            // A crossing is placeholder-dependent when no move of it can be reached without standing on an EditorOnly top.
            var clean = new bool[N];
            var bfs = new Queue<int>();
            foreach (int s0 in g.starts) { clean[s0] = true; bfs.Enqueue(s0); }
            while (bfs.Count > 0)
            {
                int n = bfs.Dequeue();
                for (int e = g.edgeStart[n]; e < g.edgeStart[n] + g.edgeCount[n]; e++)
                {
                    int m = g.eTo[e];
                    if (!clean[m] && (g.low[m] & 2) == 0) { clean[m] = true; bfs.Enqueue(m); }
                }
            }
            foreach (var x in crossings) x.placeholder = x.moves.Any(e => clean[g.eFrom[e]] && clean[g.eTo[e]]) ? null : (x.placeholder ?? "placeholder");
            int fromPlaceholder = crossings.Count(x => x.placeholder != null);
            foreach (var x in crossings.Where(x => x.placeholder != null)) x.names = "ONLY REACHABLE OVER AN EDITORONLY PLACEHOLDER TOP" + (x.placeholder != "placeholder" ? $" ('{x.placeholder}')" : "") + " | " + x.names;
            sb.AppendLine($"CLOSURE CROSSINGS ({j.closures.Count} closure lines): {crossings.Count}, of them {fromPlaceholder} launched from an EditorOnly placeholder top (gone in a build)");
            foreach (var s in crossings) sb.AppendLine("  " + ExitLine(j, s));

            string baseName = $"containment_{j.label}_{world}";
            File.WriteAllText(Path.Combine(ShotDir, baseName + ".txt"), sb.ToString());
            Map(j, Path.Combine(ShotDir, baseName + ".png"), outsideCols, insideCols, exits, crossings);
            Export(j, world, outsideCols.Count * cell, exits.Count, crossings.Count, fromPlaceholder);
            j.summary.Add($"{world}: outside {outsideCols.Count * cell:0} m2, {compArea.Count} leaks, {exits.Count} exits, {slits.Count} slits, {crossings.Count} closure crossings, nodes {N}");
        }

        static string ExitLine(Job j, Exit ex)
        {
            var f = Frame(j, ex.at.x, ex.at.z);
            string w = ex.width > 0f ? $", passage {ex.width:0.00} m" : "";
            string run = ex.length > 0f ? $" run {ex.length:0.0} m from u {ex.from.x:+0.0;-0.0} v {ex.from.y:+0.0;-0.0} to u {ex.to.x:+0.0;-0.0} v {ex.to.y:+0.0;-0.0}," : "";
            return $"u {f.x:+0.0;-0.0} v {f.y:+0.0;-0.0}{run} (world {ex.at.x:0.0}, {ex.at.y:0.0}, {ex.at.z:0.0}) side {ex.side}, {ex.count} moves [{string.Join("/", ex.types)}]{w}; near: {ex.names}";
        }

        static void Describe(Job j, Exit ex, Vector3 a, Vector3 b)
        {
            var f = Frame(j, ex.at.x, ex.at.z);
            float du = Mathf.Abs(f.x) - j.hu, dv = Mathf.Abs(f.y) - j.hv;
            ex.side = du > dv ? (f.x < 0 ? "W" : "E") : (f.y < 0 ? "S" : "N");
            var names = new List<string>();
            foreach (var c in Physics.OverlapSphere(ex.at + Vector3.up, 2.5f, j.mask, QueryTriggerInteraction.Ignore))
                if (!(c is TerrainCollider) && !names.Contains(c.name)) names.Add(c.name);
            ex.names = names.Count == 0 ? "(terrain only)" : string.Join(", ", names.Take(5));
            // free width across the move, at hip height
            var along = new Vector3(b.x - a.x, 0f, b.z - a.z);
            if (along.sqrMagnitude < 1e-6f) return;
            var side = Vector3.Cross(Vector3.up, along.normalized);
            var o = ex.at + Vector3.up * 0.9f;
            bool l = Physics.Raycast(o, side, out var hl, 1.5f, j.mask, QueryTriggerInteraction.Ignore);
            bool r = Physics.Raycast(o, -side, out var hr, 1.5f, j.mask, QueryTriggerInteraction.Ignore);
            if (l && r)
            {
                ex.width = hl.distance + hr.distance;
                ex.names = $"between {hl.collider.name} and {hr.collider.name}; " + ex.names;
            }
        }

        // The collider a node stands on, if it belongs to an EditorOnly object (a Task 7 placeholder), else null.
        static string StandsOnEditorOnly(Job j, Vector3 foot)
        {
            // a sphere the walker's width: standing on a roof edge with the capsule overhanging counts too
            float r = j.radius * 0.95f;
            foreach (var hit in Physics.SphereCastAll(foot + Vector3.up * (r + 0.3f), r, Vector3.down, 0.45f, j.mask, QueryTriggerInteraction.Ignore))
                if (IsEditorOnly(hit.collider.transform)) return hit.collider.name;
            return null;
        }

        static bool Cross(float ax, float az, float bx, float bz, float cx, float cz, float dx, float dz)
        {
            float d1 = (dx - cx) * (az - cz) - (dz - cz) * (ax - cx);
            float d2 = (dx - cx) * (bz - cz) - (dz - cz) * (bx - cx);
            float d3 = (bx - ax) * (cz - az) - (bz - az) * (cx - ax);
            float d4 = (bx - ax) * (dz - az) - (bz - az) * (dx - ax);
            return d1 * d2 < 0f && d3 * d4 < 0f;
        }

        // Top-down map of the play tile (2 px per cell): hill-shaded ground, built tops dark, reachable inside green,
        // outside red, exits yellow with their number, closure lines blue, the frame black.
        static void Map(Job j, string path, HashSet<int> outsideCols, HashSet<int> insideCols, List<Exit> exits, List<Exit> crossings)
        {
            int R = j.res, S = 2;
            var px = new Color32[R * R];
            var terrain = Terrain.activeTerrains.FirstOrDefault(t => t.name == "Terrain_Play") ?? Terrain.activeTerrain;
            var h = new float[R * R];
            for (int r = 0; r < R; r++)
            for (int c = 0; c < R; c++)
            {
                var p = Centre(j, r * R + c, 0f);
                h[r * R + c] = terrain != null ? terrain.SampleHeight(p) + terrain.transform.position.y : 0f;
            }
            for (int r = 0; r < R; r++)
            for (int c = 0; c < R; c++)
            {
                int i = r * R + c;
                float gx = (h[r * R + Mathf.Min(c + 1, R - 1)] - h[r * R + Mathf.Max(c - 1, 0)]) / (2 * j.step);
                float gz = (h[Mathf.Min(r + 1, R - 1) * R + c] - h[Mathf.Max(r - 1, 0) * R + c]) / (2 * j.step);
                var nrm = new Vector3(-gx, 1f, -gz).normalized;
                float shade = Mathf.Clamp01(0.55f + 0.45f * Vector3.Dot(nrm, new Vector3(-0.5f, 0.7f, 0.5f).normalized));
                var col = new Color(0.86f, 0.85f, 0.82f) * (0.65f + 0.35f * shade);
                var p = Centre(j, i, h[i] + 60f);
                if (Physics.Raycast(p, Vector3.down, out var hit, 120f, j.mask, QueryTriggerInteraction.Ignore) && !(hit.collider is TerrainCollider) && hit.point.y > h[i] + 0.3f)
                    col = new Color(0.36f, 0.38f, 0.44f);
                if (insideCols.Contains(i)) col = Color.Lerp(col, new Color(0.30f, 0.62f, 0.32f), 0.55f);
                if (outsideCols.Contains(i)) col = new Color(0.86f, 0.12f, 0.10f);
                col.a = 1f;
                px[i] = col;
            }
            var tex = new Texture2D(R * S, R * S, TextureFormat.RGB24, false);
            var big = new Color32[R * S * R * S];
            for (int y = 0; y < R * S; y++) for (int x = 0; x < R * S; x++) big[y * R * S + x] = px[(y / S) * R + x / S];
            tex.SetPixels32(big);
            // frame outline
            Vector2[] corners = { new Vector2(-j.hu, -j.hv), new Vector2(j.hu, -j.hv), new Vector2(j.hu, j.hv), new Vector2(-j.hu, j.hv) };
            for (int k = 0; k < 4; k++) DrawLine(j, tex, ToWorld(j, corners[k]), ToWorld(j, corners[(k + 1) % 4]), Color.black, 2);
            foreach (var cl in j.closures)
                for (int i = 0; i + 1 < cl.xs.Length; i++)
                    DrawLine(j, tex, new Vector2(cl.xs[i], cl.zs[i]), new Vector2(cl.xs[i + 1], cl.zs[i + 1]), new Color(0.1f, 0.3f, 0.95f), 3);
            for (int k = 0; k < exits.Count; k++) Mark(j, tex, exits[k].at, new Color(1f, 0.85f, 0f), k + 1);
            foreach (var x in crossings) Mark(j, tex, x.at, new Color(0.1f, 0.3f, 0.95f), 0);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }

        static Vector2 ToWorld(Job j, Vector2 f) => new Vector2(j.cx + f.x * j.cos - f.y * j.sin, j.cz + f.x * j.sin + f.y * j.cos);

        static Vector2 Px(Job j, float x, float z) => new Vector2((x - j.meta.x0) / j.step * 2f, (z - j.meta.z0) / j.step * 2f);

        static void DrawLine(Job j, Texture2D tex, Vector2 a, Vector2 b, Color c, int thick)
        {
            Vector2 p = Px(j, a.x, a.y), q = Px(j, b.x, b.y);
            int n = Mathf.CeilToInt((q - p).magnitude) + 1;
            for (int i = 0; i <= n; i++)
            {
                var s = Vector2.Lerp(p, q, i / (float)n);
                for (int dy = -thick / 2; dy <= thick / 2; dy++)
                for (int dx = -thick / 2; dx <= thick / 2; dx++) Set(tex, (int)s.x + dx, (int)s.y + dy, c);
            }
        }

        static void Mark(Job j, Texture2D tex, Vector3 at, Color c, int number)
        {
            var p = Px(j, at.x, at.z);
            for (int a = 0; a < 360; a += 3)
                for (int rr = 7; rr <= 9; rr++)
                    Set(tex, (int)(p.x + rr * Mathf.Cos(a * Mathf.Deg2Rad)), (int)(p.y + rr * Mathf.Sin(a * Mathf.Deg2Rad)), c);
            if (number > 0) Digits(tex, number.ToString(), (int)p.x + 11, (int)p.y - 5, Color.black);
        }

        // 3x5 pixel digits, drawn 2x
        static readonly string[] Glyph = { "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001", "111100111001111", "111100111101111", "111001001001001", "111101111101111", "111101111001111" };

        static void Digits(Texture2D tex, string s, int x, int y, Color c)
        {
            foreach (char ch in s)
            {
                string gl = Glyph[ch - '0'];
                for (int row = 0; row < 5; row++)
                for (int k = 0; k < 3; k++)
                    if (gl[row * 3 + k] == '1')
                        for (int a = 0; a < 2; a++) for (int b = 0; b < 2; b++) Set(tex, x + k * 2 + a, y + (4 - row) * 2 + b, c);
                x += 8;
            }
        }

        static void Set(Texture2D t, int x, int y, Color c)
        {
            if (x >= 0 && y >= 0 && x < t.width && y < t.height) t.SetPixel(x, y, c);
        }

        static string Sha1(string path)
        {
            if (!File.Exists(path)) return "";
            using var sha = SHA1.Create();
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        }

        // Graph for the Python checks: nodes (x, y, z float32; column int32; low uint8), moves in CSR order by node
        // (offsets int32 N+1, to int32, cost float32 = metres at sprint, type uint8), in that order in the .bin.
        static void Export(Job j, string world, float outsideM2, int exits, int crossings, int crossingsFromPlaceholder)
        {
            var g = j.g;
            int N = g.pos.Count, E = g.eTo.Count;
            var order = Enumerable.Range(0, E).OrderBy(e => g.eFrom[e]).ToArray();
            var offsets = new int[N + 1];
            foreach (int e in order) offsets[g.eFrom[e] + 1]++;
            for (int i = 0; i < N; i++) offsets[i + 1] += offsets[i];
            string bin = Path.Combine(PlayDir, $"reach_physics_{world}.bin");
            using (var w = new BinaryWriter(File.Create(bin)))
            {
                foreach (var p in g.pos) { w.Write(p.x); w.Write(p.y); w.Write(p.z); }
                foreach (var p in g.pos) w.Write(Col(j, p));
                foreach (var b in g.low) w.Write(b);
                foreach (int o in offsets) w.Write(o);
                foreach (int e in order) w.Write(g.eTo[e]);
                foreach (int e in order) w.Write(g.eCost[e]);
                foreach (int e in order) w.Write(g.eType[e]);
            }
            var json = new StringBuilder();
            json.Append("{\n");
            json.Append($" \"world\": \"{world}\", \"label\": \"{j.label}\", \"time\": \"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\",\n");
            json.Append($" \"nodes\": {N}, \"edges\": {E}, \"res\": {j.res}, \"step\": {j.step}, \"x0\": {j.meta.x0}, \"z0\": {j.meta.z0},\n");
            json.Append($" \"outside_m2\": {outsideM2:0.0}, \"exits\": {exits}, \"closure_crossings\": {crossings}, \"closure_crossings_from_placeholder\": {crossingsFromPlaceholder}, \"editoronly_off\": {j.disabled.Count},\n");
            json.Append($" \"types\": [\"walk\", \"crouch\", \"drop\", \"jump\", \"mantle\"],\n");
            json.Append($" \"walker\": {{\"radius\": {j.radius}, \"stand\": {j.standH}, \"crouch\": {j.crouchH}, \"slope\": {SlopeLimit}, \"step\": {StepOffset}, \"jump_v\": {j.jumpV}, \"mantle\": {MantleReach}}},\n");
            json.Append($" \"inputs\": {{\"greybox_meta.json\": \"{Sha1(LockdownGreybox.MetaPath)}\", \"play_meta.json\": \"{Sha1(Path.Combine(PlayDir, "play_meta.json"))}\", \"{j.meta.file}\": \"{Sha1(Path.Combine(PlayDir, j.meta.file))}\"}}\n");
            json.Append("}\n");
            File.WriteAllText(Path.Combine(PlayDir, $"reach_physics_{world}.json"), json.ToString());
        }
    }
}
