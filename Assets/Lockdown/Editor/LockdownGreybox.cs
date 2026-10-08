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
        const string ShaderName = "Lockdown/GreyboxGrid";
        static readonly string[] Groups = { "Buildings", "Walls", "Cover", "Stairs", "Bridges", "Boundary" };

        [Serializable] class Box
        {
            public string name, group, mat, section, shape;
            public float x, y, z, sx, sy, sz, yaw, pitch;
            public bool collider = true, walk_top, overhead, rail;
        }
        [Serializable] class Section { public string key; public int task; public bool locked; }
        [Serializable] class Cam { public string name; public float x, y, z, lx, ly, lz, fov; }
        [Serializable] class Meta { public List<Box> boxes; public List<string> replaces; public List<Cam> cameras; }

        public static string MetaPath => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "SourceData", "playground", "greybox_meta.json"));

        // The placeholder names (prefixes) the greybox replaces; empty when the greybox was never generated.
        public static List<string> Replaces()
        {
            if (!File.Exists(MetaPath)) return new List<string>();
            return JsonUtility.FromJson<Meta>(File.ReadAllText(MetaPath)).replaces ?? new List<string>();
        }

        public static bool IsReplaced(string name, List<string> replaces)
        {
            foreach (var p in replaces) if (name.StartsWith(p, StringComparison.Ordinal)) return true;
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

        static void MakeBox(Box b, Transform parent, Material mat)
        {
            bool cylinder = b.shape == "cylinder";
            var go = GameObject.CreatePrimitive(cylinder ? PrimitiveType.Cylinder : b.shape == "sphere" ? PrimitiveType.Sphere : PrimitiveType.Cube);
            go.name = b.name;
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(new Vector3(b.x, b.y, b.z), Quaternion.Euler(b.pitch, b.yaw, 0f));
            // Unity's cylinder is 2 m tall at scale 1.
            go.transform.localScale = new Vector3(b.sx, cylinder ? b.sy * 0.5f : b.sy, b.sz);
            if (!b.collider) Object.DestroyImmediate(go.GetComponent<Collider>());
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
        };

        static Material Material(string key)
        {
            EnsureFolder(MatDir);
            string path = $"{MatDir}/M_Greybox_{char.ToUpperInvariant(key[0])}{key.Substring(1)}.mat";
            var shader = Shader.Find(ShaderName);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
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
