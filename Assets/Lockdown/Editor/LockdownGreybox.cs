using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using static Lockdown.EditorTools.LockdownEditorUtil;

namespace Lockdown.EditorTools
{
    // Builds the greybox (Tasks 8-14) from SourceData/playground/greybox_meta.json, written by
    // Tools/layout/lockdown_greybox.py from the layout data: grey blocks with colliders under
    // _Greybox/<group>/<section>. Map geometry until Phase 3, so not EditorOnly. Re-runnable: it replaces its own
    // sections, skips locked ones (approved tasks), and removes the Task 7 placeholders the greybox supersedes.
    public static class LockdownGreybox
    {
        const string MatDir = "Assets/Lockdown/Materials/Greybox";
        const string MeshDir = "Assets/Lockdown/Models/Generated/Greybox";
        const string ShaderName = "Lockdown/GreyboxGrid";
        static readonly string[] Groups = { "Buildings", "Walls", "Cover", "Stairs", "Paths", "Bridges", "Boundary" };

        [Serializable] class Box
        {
            public string name, group, mat, section, shape;
            public float x, y, z, sx, sy, sz, yaw, pitch;
            public bool collider = true, walk_top, overhead, rail;
            // Boundary (Task 16): an invisible blocker keeps only its collider; a trigger is an OutOfBounds volume.
            public bool invisible, trigger;
            // shape "jersey": a precast barrier extruded from `profile` (height, half width pairs, bottom up) along
            // its length, standing on three feet with forklift slots (slot_w long, slot_h high, slot_in from each end)
            public float[] profile;
            public float slot_w, slot_h, slot_in;
        }
        [Serializable] class Section { public string key; public int task; public bool locked; }
        [Serializable] class Cam { public string name; public float x, y, z, lx, ly, lz, fov; }
        // A draped strip (stone path paving): rows of `cols` vertices across the path, world coordinates.
        [Serializable] class Ribbon { public string name, mat, section; public int cols; public float[] xs, ys, zs, us; }
        // A kalderimi chunk: LOD0 (bevelled flagstones) and LOD1 (flat outlines), vertices relative to origin.
        [Serializable] class StoneChunk { public string name, section; public float[] origin, v0, uv0, v1, uv1; public int[] t0, t1; }
        [Serializable] class Meta { public List<Box> boxes; public List<Ribbon> ribbons; public List<StoneChunk> stone_chunks; public List<string> replaces; public List<Cam> cameras; }

        public static string MetaPath => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "SourceData", "playground", "greybox_meta.json"));

        // The placeholder names (prefixes) the greybox replaces; empty when the greybox was never generated.
        public static List<string> Replaces()
        {
            if (!File.Exists(MetaPath)) return new List<string>();
            return JsonUtility.FromJson<Meta>(File.ReadAllText(MetaPath)).replaces ?? new List<string>();
        }

        public static bool IsReplaced(string name, List<string> replaces)
        {
            // an entry ending in "$" is an exact name ("Olive press$" is the press block, not the "Olive press terrace wall" strips)
            foreach (var p in replaces)
                if (p.EndsWith("$", StringComparison.Ordinal) ? name == p.Substring(0, p.Length - 1) : name.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        [MenuItem("Lockdown/Build/Greybox")]
        public static void Build()
        {
            if (!File.Exists(MetaPath))
            {
                Debug.LogError("[Lockdown] " + MetaPath + " missing. Run: uv run --with numpy --with scipy --with pillow Tools/layout/lockdown_greybox.py");
                return;
            }
            string json = File.ReadAllText(MetaPath);
            var meta = JsonUtility.FromJson<Meta>(json);
            var sections = ReadSections(json);

            GameObject root = GameObject.Find("_Greybox") ?? new GameObject("_Greybox");
            var groups = new Dictionary<string, GameObject>();
            foreach (var g in Groups)
            {
                Transform t = root.transform.Find(g);
                groups[g] = t != null ? t.gameObject : Group(root, g);
            }

            var built = new Dictionary<string, Transform>();
            int count = 0, skipped = 0;
            var mats = new Dictionary<string, Material>();
            foreach (var b in meta.boxes)
            {
                if (sections.TryGetValue(b.section, out var sec) && sec.locked) { skipped++; continue; }
                string key = b.group + "/" + b.section;
                if (!built.TryGetValue(key, out var parent))
                {
                    var g = groups.TryGetValue(b.group, out var gg) ? gg : groups["Buildings"];
                    Transform old = g.transform.Find(b.section);
                    if (old != null) Object.DestroyImmediate(old.gameObject);
                    built[key] = parent = Group(g, b.section).transform;
                }
                if (!mats.TryGetValue(b.mat, out var mat)) mats[b.mat] = mat = Material(b.mat);
                MakeBox(b, parent, mat);
                count++;
            }
            foreach (var rb in meta.ribbons ?? new List<Ribbon>())
            {
                if (sections.TryGetValue(rb.section, out var sec) && sec.locked) { skipped++; continue; }
                string key = "Paths/" + rb.section;
                if (!built.TryGetValue(key, out var parent))
                {
                    Transform old = groups["Paths"].transform.Find(rb.section);
                    if (old != null) Object.DestroyImmediate(old.gameObject);
                    built[key] = parent = Group(groups["Paths"], rb.section).transform;
                }
                if (!mats.TryGetValue(rb.mat, out var mat)) mats[rb.mat] = mat = Material(rb.mat);
                MakeRibbon(rb, parent, mat);
                count++;
            }
            foreach (var sc in meta.stone_chunks ?? new List<StoneChunk>())
            {
                if (sections.TryGetValue(sc.section, out var sec) && sec.locked) { skipped++; continue; }
                string key = "Paths/" + sc.section;
                if (!built.TryGetValue(key, out var parent))
                {
                    Transform old = groups["Paths"].transform.Find(sc.section);
                    if (old != null) Object.DestroyImmediate(old.gameObject);
                    built[key] = parent = Group(groups["Paths"], sc.section).transform;
                }
                if (!mats.TryGetValue("stone_path", out var mat)) mats["stone_path"] = mat = Material("stone_path");
                MakeStoneChunk(sc, parent, mat);
                count++;
            }
            // Sections that produced nothing this time (and are unlocked) leave no stale children behind.
            foreach (var s in sections.Values)
            {
                if (s.locked) continue;
                foreach (var g in groups.Values)
                {
                    Transform t = g.transform.Find(s.key);
                    if (t != null && !built.ContainsKey(g.name + "/" + s.key)) Object.DestroyImmediate(t.gameObject);
                }
            }
            // A section deleted from the layout data leaves its objects behind too: remove every group child that
            // no longer names a section.
            foreach (var g in groups.Values)
            {
                var gone = new List<GameObject>();
                foreach (Transform t in g.transform)
                    if (!sections.ContainsKey(t.name)) gone.Add(t.gameObject);
                foreach (var go in gone)
                {
                    Debug.Log($"[Lockdown] Greybox: removed {g.name}/{go.name} (section no longer in the layout data).");
                    Object.DestroyImmediate(go);
                }
            }

            int removed = RemoveReplacedPlaceholders(meta.replaces);
            BuildCameras(meta.cameras);
            EditorSceneManager.MarkSceneDirty(root.scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Lockdown] Greybox built: {count} blocks ({skipped} in locked sections kept), {removed} placeholders removed.");
        }

        // JsonUtility can't read a dictionary: the "sections" object is parsed by hand ({"key": {"task": n, "locked": b}}).
        static Dictionary<string, Section> ReadSections(string json)
        {
            var result = new Dictionary<string, Section>();
            int i = json.IndexOf("\"sections\"", StringComparison.Ordinal);
            if (i < 0) return result;
            int open = json.IndexOf('{', i), depth = 0, end = open;
            for (int k = open; k < json.Length; k++)
            {
                if (json[k] == '{') depth++;
                else if (json[k] == '}' && --depth == 0) { end = k; break; }
            }
            string body = json.Substring(open + 1, end - open - 1);
            int p = 0;
            while (true)
            {
                int q0 = body.IndexOf('"', p);
                if (q0 < 0) break;
                int q1 = body.IndexOf('"', q0 + 1);
                int o = body.IndexOf('{', q1), c = body.IndexOf('}', o);
                var s = JsonUtility.FromJson<Section>(body.Substring(o, c - o + 1));
                s.key = body.Substring(q0 + 1, q1 - q0 - 1);
                result[s.key] = s;
                p = c + 1;
            }
            return result;
        }

        // A ribbon becomes one mesh, saved as an asset (never stored in the scene) and refilled in place on a rebuild.
        static void MakeRibbon(Ribbon rb, Transform parent, Material mat)
        {
            int cols = rb.cols, rows = rb.xs.Length / cols;
            var origin = new Vector3(rb.xs[0], rb.ys[0], rb.zs[0]);
            var verts = new Vector3[rows * cols];
            var uvs = new Vector2[rows * cols];
            float[] along = new float[cols];
            for (int r = 0; r < rows; r++)
            {
                if (r > 0)
                {
                    for (int c = 0; c < cols; c++)
                    {
                        int currIdx = r * cols + c;
                        int prevIdx = (r - 1) * cols + c;
                        along[c] += Vector3.Distance(new Vector3(rb.xs[currIdx], rb.ys[currIdx], rb.zs[currIdx]),
                                                     new Vector3(rb.xs[prevIdx], rb.ys[prevIdx], rb.zs[prevIdx]));
                    }
                }
                float across = 0f;
                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c;
                    verts[i] = new Vector3(rb.xs[i], rb.ys[i], rb.zs[i]) - origin;
                    if (c > 0)
                    {
                        across += Vector3.Distance(new Vector3(rb.xs[i], rb.ys[i], rb.zs[i]),
                                                   new Vector3(rb.xs[i - 1], rb.ys[i - 1], rb.zs[i - 1]));
                    }
                    // Scale UV to physical meters: 0.42f means 1 UV unit = ~2.4 meters of physical paving
                    uvs[i] = rb.us != null && rb.us.Length == rows * cols ? new Vector2(rb.us[i], 0.5f) : new Vector2(across * 0.42f, along[c] * 0.42f);
                }
            }
            var tris = new List<int>();
            for (int r = 0; r < rows - 1; r++)
                for (int c = 0; c < cols - 1; c++)
                {
                    int i = r * cols + c;
                    tris.AddRange(new[] { i, i + 1, i + cols, i + 1, i + cols + 1, i + cols });
                }
            EnsureFolder(MeshDir);
            string path = $"{MeshDir}/Path_{rb.name.Replace(' ', '_')}.asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            bool create = mesh == null;
            if (create) mesh = new Mesh();
            mesh.Clear();
            mesh.name = Path.GetFileNameWithoutExtension(path);  // must match the asset file name (Unity warns otherwise)
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            float up = 0f;
            foreach (var nrm in mesh.normals) up += nrm.y;
            if (up < 0f)   // wound the wrong way for this path's direction: flip
            {
                for (int t = 0; t < tris.Count; t += 3) (tris[t + 1], tris[t + 2]) = (tris[t + 2], tris[t + 1]);
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateNormals();
            }
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            if (create) AssetDatabase.CreateAsset(mesh, path);
            else EditorUtility.SetDirty(mesh);

            var go = new GameObject(rb.name);
            go.transform.SetParent(parent, false);
            go.transform.position = origin;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r2 = go.AddComponent<MeshRenderer>();
            r2.sharedMaterial = mat;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.ContributeGI);
            r2.receiveGI = ReceiveGI.LightProbes;
            r2.shadowCastingMode = ShadowCastingMode.On;
        }

        static Mesh StoneMesh(string path, float[] v, float[] uv, int[] tris, bool recalcTangents)
        {
            var verts = new Vector3[v.Length / 3];
            for (int i = 0; i < verts.Length; i++) verts[i] = new Vector3(v[3 * i], v[3 * i + 1], v[3 * i + 2]);
            var uvs = new Vector2[verts.Length];
            for (int i = 0; i < uvs.Length; i++) uvs[i] = new Vector2(uv[2 * i], uv[2 * i + 1]);
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            bool create = mesh == null;
            if (create) mesh = new Mesh();
            mesh.Clear();
            mesh.indexFormat = verts.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.name = Path.GetFileNameWithoutExtension(path);
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            // fans are wound for the plan view; flip when the mesh faces down
            float up = 0f;
            foreach (var n in mesh.normals) up += n.y;
            if (up < 0f)
            {
                for (int k = 0; k < tris.Length; k += 3) (tris[k + 1], tris[k + 2]) = (tris[k + 2], tris[k + 1]);
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateNormals();
            }
            mesh.RecalculateBounds();
            if (create) AssetDatabase.CreateAsset(mesh, path);
            else EditorUtility.SetDirty(mesh);
            return mesh;
        }

        // A kalderimi chunk: one object with a LODGroup (LOD0 bevelled stones, LOD1 flat outlines, cross-faded). No collider:
        // the Task 7 ramp (a centimetre or two under the stone tops) is the walking surface.
        static void MakeStoneChunk(StoneChunk sc, Transform parent, Material mat)
        {
            EnsureFolder(MeshDir);
            string baseName = "Kalderimi_" + sc.name.Replace(' ', '_');
            var m0 = StoneMesh($"{MeshDir}/{baseName}_LOD0.asset", sc.v0, sc.uv0, (int[])sc.t0.Clone(), false);
            var m1 = StoneMesh($"{MeshDir}/{baseName}_LOD1.asset", sc.v1, sc.uv1, (int[])sc.t1.Clone(), false);
            var go = new GameObject(sc.name);
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(sc.origin[0], sc.origin[1], sc.origin[2]);
            var lods = new LOD[2];
            for (int i = 0; i < 2; i++)
            {
                var child = new GameObject($"LOD{i}");
                child.transform.SetParent(go.transform, false);
                child.AddComponent<MeshFilter>().sharedMesh = i == 0 ? m0 : m1;
                var r = child.AddComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                r.receiveGI = ReceiveGI.LightProbes;
                r.shadowCastingMode = i == 0 ? ShadowCastingMode.On : ShadowCastingMode.On;
                GameObjectUtility.SetStaticEditorFlags(child, StaticEditorFlags.ContributeGI);
                lods[i] = new LOD(i == 0 ? 0.07f : 0.012f, new[] { r });
            }
            var lg = go.AddComponent<LODGroup>();
            lg.SetLODs(lods);
            lg.fadeMode = LODFadeMode.CrossFade;
            lg.animateCrossFading = true;
            lg.RecalculateBounds();
        }

        static void MakeBox(Box b, Transform parent, Material mat)
        {
            if (b.shape == "jersey") { MakeJersey(b, parent, mat); return; }
            bool cylinder = b.shape == "cylinder";
            var go = GameObject.CreatePrimitive(cylinder ? PrimitiveType.Cylinder : b.shape == "sphere" ? PrimitiveType.Sphere : PrimitiveType.Cube);
            go.name = b.name;
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(new Vector3(b.x, b.y, b.z), Quaternion.Euler(b.pitch, b.yaw, 0f));
            // Unity's cylinder is 2 m tall at scale 1.
            go.transform.localScale = new Vector3(b.sx, cylinder ? b.sy * 0.5f : b.sy, b.sz);
            if (!b.collider) Object.DestroyImmediate(go.GetComponent<Collider>());
            if (b.invisible || b.trigger)
            {
                Object.DestroyImmediate(go.GetComponent<MeshRenderer>());
                Object.DestroyImmediate(go.GetComponent<MeshFilter>());
                if (b.trigger)
                {
                    go.GetComponent<Collider>().isTrigger = true;
                    go.AddComponent<Lockdown.LockdownOutOfBounds>();
                }
                return;
            }
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            bool thin = b.rail || Mathf.Min(b.sx, b.sz) < 0.2f;
            // Thin rails, posts and tree canopies stay out of the bake (they'd only add noise to the probes) but keep
            // their shadows.
            var flags = StaticEditorFlags.BatchingStatic;
            if (!thin && b.mat != "foliage") flags |= StaticEditorFlags.ContributeGI | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic;
            GameObjectUtility.SetStaticEditorFlags(go, flags);
            r.receiveGI = ReceiveGI.LightProbes;
            r.shadowCastingMode = ShadowCastingMode.On;
        }

        // A jersey barrier: one mesh per length (5 cm steps) saved as an asset, pivot at the bottom centre, length along
        // local z; the collider is the convex hull (the slots are visual).
        static void MakeJersey(Box b, Transform parent, Material mat)
        {
            int mm = Mathf.RoundToInt(b.sz * 20f) * 50;
            float L = mm / 1000f;
            EnsureFolder(MeshDir);
            string path = $"{MeshDir}/Jersey_{mm:0000}.asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh == null)
            {
                mesh = JerseyMesh(b, L);
                mesh.name = Path.GetFileNameWithoutExtension(path);
                AssetDatabase.CreateAsset(mesh, path);
            }
            var go = new GameObject(b.name);
            go.transform.SetParent(parent, false);
            // pivot at the middle of the base; pitch tilts it along its length to lie on a sloping road
            go.transform.SetPositionAndRotation(new Vector3(b.x, b.y - b.sy * 0.5f, b.z), Quaternion.Euler(b.pitch, b.yaw, 0f));
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex = true;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.ContributeGI);
            r.receiveGI = ReceiveGI.LightProbes;
            r.shadowCastingMode = ShadowCastingMode.On;
        }

        static Mesh JerseyMesh(Box b, float L)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            int n = b.profile.Length / 2;
            float foot = b.slot_h, half = L * 0.5f;
            // the profile from the top of the feet up, both faces mirrored: a closed outline across the barrier
            var ring = new List<Vector2>();
            for (int i = 0; i < n; i++) if (b.profile[2 * i] >= foot - 1e-4f) ring.Add(new Vector2(b.profile[2 * i + 1], b.profile[2 * i]));
            if (ring[0].y > foot + 1e-4f) ring.Insert(0, new Vector2(b.profile[1], foot));
            var outline = new List<Vector2>();
            for (int i = ring.Count - 1; i >= 0; i--) outline.Add(new Vector2(-ring[i].x, ring[i].y));
            foreach (var p in ring) outline.Add(p);
            // outline: down the left face, across the bottom, up the right face; with Unity's cross(b - a, c - a)
            // front faces, the strips below face outward and the cap fans face -z / +z
            for (int i = 0; i + 1 < outline.Count; i++)   // flat-shaded strip per profile segment
            {
                Vector2 p = outline[i], q = outline[i + 1];
                int k = verts.Count;
                verts.Add(new Vector3(p.x, p.y, -half)); verts.Add(new Vector3(q.x, q.y, -half));
                verts.Add(new Vector3(q.x, q.y, half)); verts.Add(new Vector3(p.x, p.y, half));
                tris.AddRange(new[] { k, k + 1, k + 2, k, k + 2, k + 3 });
            }
            foreach (float zc in new[] { -half, half })   // end caps: fan over the convex outline
            {
                int k = verts.Count;
                foreach (var p in outline) verts.Add(new Vector3(p.x, p.y, zc));
                for (int i = 1; i + 1 < outline.Count; i++)
                    if (zc < 0f) tris.AddRange(new[] { k, k + i + 1, k + i }); else tris.AddRange(new[] { k, k + i, k + i + 1 });
            }
            // three feet between the two forklift slots (the outline's bottom strip is the body's underside over them)
            float w = b.profile[1], s0 = b.slot_in - b.slot_w * 0.5f, s1 = b.slot_in + b.slot_w * 0.5f;
            var feet = new[] { new Vector2(-half, -half + s0), new Vector2(-half + s1, half - s1), new Vector2(half - s0, half) };
            foreach (var f in feet) AddBlock(verts, tris, new Vector3(-w, 0f, f.x), new Vector3(w, foot, f.y));
            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static void AddBlock(List<Vector3> v, List<int> t, Vector3 a, Vector3 b)
        {
            Vector3[] c =
            {
                new Vector3(a.x, a.y, a.z), new Vector3(b.x, a.y, a.z), new Vector3(b.x, b.y, a.z), new Vector3(a.x, b.y, a.z),
                new Vector3(a.x, a.y, b.z), new Vector3(b.x, a.y, b.z), new Vector3(b.x, b.y, b.z), new Vector3(a.x, b.y, b.z),
            };
            int[][] faces = { new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 4, 7, 3 }, new[] { 1, 2, 6, 5 }, new[] { 3, 7, 6, 2 }, new[] { 0, 1, 5, 4 } };
            foreach (var f in faces)
            {
                int k = v.Count;
                foreach (int i in f) v.Add(c[i]);
                t.AddRange(new[] { k, k + 1, k + 2, k, k + 2, k + 3 });
            }
        }

        static readonly Dictionary<string, Color> Tints = new Dictionary<string, Color>
        {
            // sRGB tints, kept around 0.5 so the review sun never blows them out. Walkable tops are warm, cover green-grey.
            { "wall", new Color(0.50f, 0.50f, 0.49f) },
            { "low", new Color(0.56f, 0.53f, 0.46f) },
            { "walk", new Color(0.52f, 0.43f, 0.33f) },
            { "cover", new Color(0.42f, 0.47f, 0.40f) },
            { "trim", new Color(0.14f, 0.14f, 0.15f) },
            { "foliage", new Color(0.27f, 0.37f, 0.21f) },
            { "bark", new Color(0.36f, 0.30f, 0.24f) },
            { "bridge", new Color(0.50f, 0.53f, 0.55f) },
            { "rock", new Color(0.52f, 0.49f, 0.45f) },
            { "stone", new Color(0.60f, 0.57f, 0.51f) },   // stone path paving (kalderimi)
            { "stone_trim", new Color(0.64f, 0.61f, 0.55f) }, // dressed limestone kerbs, ribs and thresholds
            { "rubble", new Color(0.55f, 0.51f, 0.44f) },  // dry-stone terrace retaining walls
            { "concrete", new Color(0.62f, 0.62f, 0.60f) }, // precast jersey barriers (police roadblocks)
            { "concrete_dark", new Color(0.52f, 0.52f, 0.50f) }, // roadblock barriers: mid precast grey, reads in sun and in shade
            { "fence", new Color(0.44f, 0.46f, 0.47f) },    // temporary mesh-fence panels on the roadblocks
        };

        static Material Material(string key)
        {
            EnsureFolder(MatDir);
            string path = $"{MatDir}/M_Greybox_{char.ToUpperInvariant(key[0])}{key.Substring(1)}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (key == "stone")
            {
                var urpShader = Shader.Find("Universal Render Pipeline/Lit");
                if (mat == null)
                {
                    mat = new Material(urpShader);
                    AssetDatabase.CreateAsset(mat, path);
                }
                mat.shader = urpShader;
                mat.SetColor("_BaseColor", Color.white);
                var diff = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Lockdown/Textures/Ground/T_Kalderimi_diff.png");
                var nor = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Lockdown/Textures/Ground/T_Kalderimi_nor.png");
                var maos = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Lockdown/Textures/Ground/T_Kalderimi_MAOS.png");
                if (diff != null) mat.SetTexture("_BaseMap", diff);
                if (nor != null)
                {
                    mat.SetTexture("_BumpMap", nor);
                    mat.SetFloat("_BumpScale", 1.0f);
                    mat.EnableKeyword("_NORMALMAP");
                }
                if (maos != null)
                {
                    mat.SetTexture("_MetallicGlossMap", maos);
                    mat.SetFloat("_Smoothness", 1.0f);
                    mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                    mat.SetTexture("_OcclusionMap", maos);
                    mat.SetFloat("_OcclusionStrength", 1.0f);
                }
                mat.enableInstancing = true;
                EditorUtility.SetDirty(mat);
                return mat;
            }

            if (key == "stone_path" || key == "soil_path")
            {
                // kalderimi flagstones (one palette swatch per stone; soil_path is no longer generated)
                var urp = Shader.Find("Universal Render Pipeline/Lit");
                if (mat == null) { mat = new Material(urp); AssetDatabase.CreateAsset(mat, path); }
                mat.shader = urp;
                bool soil = key == "soil_path";
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(soil ? "Assets/Lockdown/Textures/Ground/T_KalderimiSoil.png" : "Assets/Lockdown/Textures/Ground/T_KalderimiStones_pal.png");
                mat.SetTexture("_BaseMap", tex);
                mat.SetColor("_BaseColor", Color.white);
                // matte placeholder stone: no specular glints, no environment reflections
                mat.SetFloat("_Smoothness", 0.05f);
                mat.SetFloat("_Metallic", 0f);
                mat.SetFloat("_SpecularHighlights", 0f);
                mat.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
                mat.SetFloat("_EnvironmentReflections", 0f);
                mat.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
                mat.SetFloat("_Surface", 0f);                    // opaque (an earlier version of the soil was transparent)
                mat.SetFloat("_ZWrite", 1f);
                mat.SetOverrideTag("RenderType", "Opaque");
                mat.renderQueue = -1;
                mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.SetShaderPassEnabled("ShadowCaster", true);
                mat.enableInstancing = true;
                EditorUtility.SetDirty(mat);
                return mat;
            }

            var shader = Shader.Find(ShaderName);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.shader = shader;
            mat.SetColor("_BaseColor", Tints.TryGetValue(key, out var c) ? c : Color.magenta);
            mat.SetFloat("_Smoothness", key == "trim" ? 0.35f : 0.15f);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // EditorOnly review cameras (rendered on demand for screenshots, never in the Game view).
        static void BuildCameras(List<Cam> cams)
        {
            GameObject review = GameObject.Find("_Review") ?? new GameObject("_Review");
            Transform old = review.transform.Find("GreyboxCameras");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            if (cams == null || cams.Count == 0) return;
            var root = Group(review, "GreyboxCameras");
            foreach (var c in cams)
            {
                var go = new GameObject(c.name);
                go.transform.SetParent(root.transform, false);
                var pos = new Vector3(c.x, c.y, c.z);
                go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(new Vector3(c.lx, c.ly, c.lz) - pos));
                var cam = go.AddComponent<Camera>();
                cam.fieldOfView = c.fov;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 30000f;
                cam.depth = -10f;
                cam.enabled = false;
                var data = go.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
                data.renderPostProcessing = true;
                data.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            }
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.tag = "EditorOnly";
        }

        static int RemoveReplacedPlaceholders(List<string> replaces)
        {
            Transform ph = GameObject.Find("_Review")?.transform.Find("Placeholders");
            if (ph == null || replaces == null) return 0;
            var doomed = new List<GameObject>();
            foreach (Transform group in ph)
                foreach (Transform t in group)
                    if (IsReplaced(t.name, replaces)) doomed.Add(t.gameObject);
            foreach (var g in doomed) Object.DestroyImmediate(g);
            return doomed.Count;
        }
    }
}
