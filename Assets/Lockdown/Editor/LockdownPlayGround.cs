using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;
using static Lockdown.EditorTools.LockdownEditorUtil;

namespace Lockdown.EditorTools
{
    // Builds the play ground (Task 7) from SourceData/playground/, written by Tools/terrain/lockdown_playground.py
    // from the layout data: a 0.5 m terrain tile over the play area, set into a hole in the village terrain
    // (whose ring of edge cells drops underneath as a skirt), plus EditorOnly bounds posts, spawn/flag stakes
    // and review cameras. Re-runnable: it replaces its own output and restores any previous hole first.
    public static class LockdownPlayGround
    {
        const string TerrainDir = "Assets/Lockdown/Terrain";
        const string LayerDir = TerrainDir + "/Layers";
        const string PlayDataPath = TerrainDir + "/TD_Play.asset";
        const string GridTexPath = "Assets/Lockdown/Textures/Review/T_ReviewGrid_diff.png";
        const string SurfaceTexPath = "Assets/Lockdown/Textures/Review/T_ReviewSurface_diff.png";
        const string MatDir = "Assets/Lockdown/Materials/Review";
        const string EditorOnly = "EditorOnly";
        const float GridTile = 10f, SkirtDepth = 8f, EyeHeight = 1.6f;
        // The grid texture is near white (sRGB 200); under the review sun it blows out, so the review layers are
        // tinted down to a dry-soil albedo (about 0.25 linear). The village grid gets the same tint, so both match.
        const float GridTint = 0.45f, SurfaceTint = 0.85f;
        const string VillageGridLayerPath = LayerDir + "/TL_ReviewGrid.terrainlayer";

        [Serializable] class Point { public string name; public string kind; public float x, y, z; }
        [Serializable] class Route { public string name; public float[] xs, zs; }
        [Serializable] class Box { public string name, group, mat; public float x, y, z, sx, sy, sz, yaw, pitch; }
        [Serializable] class Label { public string text, sub, kind; public float x, y, z, size; }
        [Serializable] class Meta
        {
            public string file, mask;
            public int resolution, village_first_x, village_first_z, village_cells;
            public float size, x0, z0, base_height, top_height, frame_rotation_deg, flag_b_height;
            public float[] frame_center, frame_size;
            public List<Point> stakes, posts;
            public List<Route> routes;
            public List<Box> boxes;
            public List<Label> labels;
        }
        [Serializable] class DemMeta { public float base_height, top_height; public DemGrid village; }
        [Serializable] class DemGrid { public string file; public float size; public int resolution; }

        static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string PlayDir => Path.Combine(Root, "SourceData", "playground");
        static string DemDir => Path.Combine(Root, "SourceData", "dem");

        [MenuItem("Lockdown/Build/Play Ground")]
        public static void Build()
        {
            string metaPath = Path.Combine(PlayDir, "play_meta.json");
            if (!File.Exists(metaPath))
            {
                Debug.LogError("[Lockdown] " + metaPath + " missing. Run: uv run --with numpy --with scipy --with pillow Tools/terrain/lockdown_playground.py");
                return;
            }
            var meta = JsonUtility.FromJson<Meta>(File.ReadAllText(metaPath));
            Terrain village = GameObject.Find("_Ground/Terrain_Village")?.GetComponent<Terrain>();
            if (village == null) { Debug.LogError("[Lockdown] _Ground/Terrain_Village not found: open Lockdown_Lockdown.unity."); return; }

            CutVillageHole(village.terrainData, meta);
            TerrainData data = BuildData(meta, village);

            GameObject ground = GameObject.Find("_Ground");
            Transform old = ground.transform.Find("Terrain_Play");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            GameObject go = Terrain.CreateTerrainGameObject(data);
            go.name = "Terrain_Play";
            go.transform.SetParent(ground.transform, false);
            go.transform.position = new Vector3(meta.x0, meta.base_height, meta.z0);
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
            var t = go.GetComponent<Terrain>();
            t.heightmapPixelError = 1f;
            t.basemapDistance = 3000f;
            t.drawInstanced = true;
            t.shadowCastingMode = ShadowCastingMode.On;
            t.materialTemplate = village.materialTemplate;
            t.reflectionProbeUsage = village.reflectionProbeUsage;

            BuildMarkers(meta);
            BuildPlaceholders(meta);
            BuildCameras(meta);
            PlaceWalker(meta);
            EditorSceneManager.MarkSceneDirty(go.scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Lockdown] Play ground built: {meta.size:F1} m tile at ({meta.x0:F1}, {meta.z0:F1}), " +
                      $"heights {meta.base_height}..{meta.top_height} m, Flag B {meta.flag_b_height:F2} m, " +
                      $"village hole cells x {meta.village_first_x}+{meta.village_cells}, z {meta.village_first_z}+{meta.village_cells}.");
        }

        // ---------- Terrain ----------

        static TerrainData BuildData(Meta meta, Terrain village)
        {
            int res = meta.resolution;
            byte[] raw = File.ReadAllBytes(Path.Combine(PlayDir, meta.file));
            if (raw.Length != res * res * 2) throw new Exception($"{meta.file}: expected {res * res * 2} bytes, got {raw.Length}");
            var heights = new float[res, res];
            for (int z = 0; z < res; z++)
            for (int x = 0; x < res; x++)
            {
                int i = (z * res + x) * 2;
                heights[z, x] = (raw[i] | (raw[i + 1] << 8)) / 65535f; // rows south to north = terrain z
            }

            var data = AssetDatabase.LoadAssetAtPath<TerrainData>(PlayDataPath);
            if (data == null)
            {
                data = new TerrainData();
                AssetDatabase.CreateAsset(data, PlayDataPath);
            }
            data.heightmapResolution = res;
            data.size = new Vector3(meta.size, meta.top_height - meta.base_height, meta.size);
            data.SetHeights(0, 0, heights);
            data.baseMapResolution = 512;
            data.SetDetailResolution(32, 16);

            // Two review layers: the grey 1 m / 10 m grid on free ground, a warmer, darker copy on lanes,
            // ramps and pads. Offsets keep the grid lines continuous with the village terrain's.
            Vector3 vp = village.transform.position;
            var offset = new Vector2(Mathf.Repeat(meta.x0 - vp.x, GridTile), Mathf.Repeat(meta.z0 - vp.z, GridTile));
            var grid = GetOrCreateLayer(LayerDir + "/TL_PlayGrid.terrainlayer", AssetDatabase.LoadAssetAtPath<Texture2D>(GridTexPath), offset, GridTint);
            var surface = GetOrCreateLayer(LayerDir + "/TL_PlaySurface.terrainlayer", GetOrCreateSurfaceTexture(), offset, SurfaceTint);
            var villageGrid = AssetDatabase.LoadAssetAtPath<TerrainLayer>(VillageGridLayerPath);
            if (villageGrid != null)
            {
                villageGrid.diffuseRemapMax = new Vector4(GridTint, GridTint, GridTint, 1f);
                EditorUtility.SetDirty(villageGrid);
            }
            data.terrainLayers = new[] { grid, surface };

            var mask = new Texture2D(2, 2);
            mask.LoadImage(File.ReadAllBytes(Path.Combine(PlayDir, meta.mask))); // north up; LoadImage puts row 0 at the south
            int ar = mask.width;
            data.alphamapResolution = ar;
            var alpha = new float[ar, ar, 2];
            Color32[] px = mask.GetPixels32();
            for (int z = 0; z < ar; z++)
            for (int x = 0; x < ar; x++)
            {
                float s = px[z * ar + x].r / 255f;
                alpha[z, x, 0] = 1f - s;
                alpha[z, x, 1] = s;
            }
            Object.DestroyImmediate(mask);
            data.SetAlphamaps(0, 0, alpha);
            EditorUtility.SetDirty(data);
            return data;
        }

        // Restores any previous hole and skirt from the DEM, then cuts the hole for this tile: the cells under
        // the tile minus a one-cell ring, whose inner vertices drop SkirtDepth so LOD cracks show ground, not sky.
        static void CutVillageHole(TerrainData vd, Meta meta)
        {
            var dem = JsonUtility.FromJson<DemMeta>(File.ReadAllText(Path.Combine(DemDir, "dem_meta.json")));
            int res = vd.heightmapResolution;
            if (dem.village.resolution != res) throw new Exception("[Lockdown] DEM and village terrain resolutions differ.");
            byte[] raw = File.ReadAllBytes(Path.Combine(DemDir, dem.village.file));
            float Dem(int z, int x) { int i = (z * res + x) * 2; return (raw[i] | (raw[i + 1] << 8)) / 65535f; }

            int hr = vd.holesResolution;
            bool[,] solid = vd.GetHoles(0, 0, hr, hr);
            int minX = hr, minZ = hr, maxX = -1, maxZ = -1;
            for (int z = 0; z < hr; z++)
            for (int x = 0; x < hr; x++)
                if (!solid[z, x]) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z); }
            if (maxX >= 0)
            {
                // Old hole cells minX..maxX; its lowered vertices are minX..maxX+1.
                int w = maxX - minX + 2, h = maxZ - minZ + 2;
                var restore = new float[h, w];
                for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++) restore[z, x] = Dem(minZ + z, minX + x);
                vd.SetHeights(minX, minZ, restore);
            }

            int fx = meta.village_first_x, fz = meta.village_first_z, n = meta.village_cells;
            float drop = SkirtDepth / vd.size.y;
            var skirt = new float[n - 1, n - 1];
            for (int z = 0; z < n - 1; z++)
            for (int x = 0; x < n - 1; x++) skirt[z, x] = Mathf.Max(0f, Dem(fz + 1 + z, fx + 1 + x) - drop);
            vd.SetHeights(fx + 1, fz + 1, skirt);

            for (int z = 0; z < hr; z++)
            for (int x = 0; x < hr; x++)
                solid[z, x] = !(x > fx && x < fx + n - 1 && z > fz && z < fz + n - 1);
            vd.SetHoles(0, 0, solid);
            EditorUtility.SetDirty(vd);
        }

        static TerrainLayer GetOrCreateLayer(string path, Texture2D tex, Vector2 offset, float tint)
        {
            var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
            if (layer == null)
            {
                layer = new TerrainLayer();
                AssetDatabase.CreateAsset(layer, path);
            }
            layer.diffuseTexture = tex;
            layer.tileSize = new Vector2(GridTile, GridTile);
            layer.tileOffset = offset;
            layer.smoothness = 0f;
            layer.metallic = 0f;
            layer.diffuseRemapMax = new Vector4(tint, tint, tint, 1f);
            EditorUtility.SetDirty(layer);
            return layer;
        }

        // The review grid recoloured warm and darker, so lanes, ramps and pads read at a glance.
        static Texture2D GetOrCreateSurfaceTexture()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(SurfaceTexPath);
            if (existing != null) return existing;
            var src = new Texture2D(2, 2);
            src.LoadImage(File.ReadAllBytes(Path.Combine(Root, GridTexPath)));
            Color32[] px = src.GetPixels32();
            for (int i = 0; i < px.Length; i++)
            {
                float v = px[i].r / 200f; // background 1, minor lines 0.75, major lines 0.35
                px[i] = new Color32((byte)(150 * v), (byte)(132 * v), (byte)(110 * v), 255);
            }
            src.SetPixels32(px);
            src.Apply();
            File.WriteAllBytes(Path.Combine(Root, SurfaceTexPath), src.EncodeToPNG());
            Object.DestroyImmediate(src);
            AssetDatabase.ImportAsset(SurfaceTexPath);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(SurfaceTexPath);
        }

        // ---------- EditorOnly markers ----------

        static void BuildMarkers(Meta meta)
        {
            GameObject review = GameObject.Find("_Review") ?? new GameObject("_Review");
            Transform old = review.transform.Find("PlayBounds");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var root = new GameObject("PlayBounds");
            root.transform.SetParent(review.transform, false);

            Material red = Lit("M_PlayMarker_Red", new Color(0.75f, 0.12f, 0.1f));
            Material white = Lit("M_PlayMarker_White", new Color(0.85f, 0.85f, 0.85f));
            Material west = Lit("M_PlayMarker_West", new Color(0.2f, 0.65f, 0.95f));
            Material east = Lit("M_PlayMarker_East", new Color(0.95f, 0.3f, 0.6f));
            Material flag = Lit("M_PlayMarker_Flag", new Color(0.95f, 0.8f, 0.15f));

            var posts = Group(root, "Posts");
            int i = 0;
            foreach (var p in meta.posts)
            {
                bool corner = p.kind == "corner";
                Pole(posts, p.name, new Vector3(p.x, p.y, p.z), corner ? 6f : 3f, corner ? 0.25f : 0.12f, (i++ % 2 == 0) ? red : white);
            }
            var stakes = Group(root, "Stakes");
            foreach (var p in meta.stakes)
            {
                var pos = new Vector3(p.x, p.y, p.z);
                switch (p.kind)
                {
                    case "flag": Pole(stakes, p.name, pos, 5f, 0.2f, flag, 0.7f); break;
                    case "spawn_west": Pole(stakes, p.name, pos, 4f, 0.2f, west, 0.6f); break;
                    case "spawn_east": Pole(stakes, p.name, pos, 4f, 0.2f, east, 0.6f); break;
                    case "start_west": Pole(stakes, p.name, pos, 0.4f, 0.25f, west); break;
                    case "start_east": Pole(stakes, p.name, pos, 0.4f, 0.25f, east); break;
                }
            }
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.tag = EditorOnly;
        }

        static void Pole(GameObject parent, string name, Vector3 groundPos, float height, float radius, Material mat, float ball = 0f)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.transform.position = groundPos;
            Part(go, PrimitiveType.Cylinder, mat, new Vector3(0f, height * 0.5f, 0f), new Vector3(radius * 2f, height * 0.5f, radius * 2f));
            if (ball > 0f) Part(go, PrimitiveType.Sphere, mat, new Vector3(0f, height + ball * 0.5f, 0f), Vector3.one * ball);
        }

        static void Part(GameObject parent, PrimitiveType type, Material mat, Vector3 localPos, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(type);
            Object.DestroyImmediate(go.GetComponent<Collider>()); // markers never block the walker
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
        }

        static Material Lit(string name, Color color)
        {
            string path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Smoothness", 0.2f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // ---------- EditorOnly placeholders and name labels (Task 7 review) ----------

        const string LabelShader = "Lockdown/ReviewLabel";
        static Font labelFont;
        static Font LabelFont => labelFont != null ? labelFont : labelFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        const float LabelScale = 1.8f; // readable from about 60 m

        [MenuItem("Lockdown/Review/Build Placeholders")]
        public static void BuildPlaceholdersMenu()
        {
            var meta = JsonUtility.FromJson<Meta>(File.ReadAllText(Path.Combine(PlayDir, "play_meta.json")));
            BuildPlaceholders(meta);
            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        }

        // Grey blocks for buildings, decks, parapets and ramps (with colliders, so the walker feels them), plus a
        // floating name over every place. Everything comes from play_meta.json, written by lockdown_playground.py.
        static void BuildPlaceholders(Meta meta)
        {
            GameObject review = GameObject.Find("_Review") ?? new GameObject("_Review");
            Transform old = review.transform.Find("Placeholders");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var root = new GameObject("Placeholders");
            root.transform.SetParent(review.transform, false);

            var mats = new Dictionary<string, Material>
            {
                { "house", Lit("M_Placeholder_House", new Color(0.55f, 0.55f, 0.53f)) },
                { "bridge", Lit("M_Placeholder_Bridge", new Color(0.42f, 0.45f, 0.47f)) },
                { "wall", Lit("M_Placeholder_Wall", new Color(0.5f, 0.46f, 0.4f)) },
                { "rock", Lit("M_Placeholder_Rock", new Color(0.46f, 0.44f, 0.42f)) },
                { "landmark_real", Lit("M_Placeholder_Real", new Color(0.45f, 0.58f, 0.45f)) },
                { "landmark_adapted", Lit("M_Placeholder_Adapted", new Color(0.66f, 0.56f, 0.38f)) },
                { "landmark_invented", Lit("M_Placeholder_Invented", new Color(0.55f, 0.45f, 0.64f)) },
            };
            var groups = new Dictionary<string, GameObject>();
            var replaced = LockdownGreybox.Replaces(); // superseded by the greybox (Tasks 8-14)
            foreach (var b in meta.boxes)
            {
                if (LockdownGreybox.IsReplaced(b.name, replaced)) continue;
                if (!groups.TryGetValue(b.group, out var g)) groups[b.group] = g = Group(root, b.group);
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube); // keeps its BoxCollider
                go.name = b.name;
                go.transform.SetParent(g.transform, false);
                go.transform.SetPositionAndRotation(new Vector3(b.x, b.y, b.z), Quaternion.Euler(b.pitch, b.yaw, 0f));
                go.transform.localScale = new Vector3(b.sx, b.sy, b.sz);
                go.GetComponent<MeshRenderer>().sharedMaterial = mats.TryGetValue(b.mat, out var m) ? m : mats["house"];
            }

            var labels = Group(root, "Labels");
            Material text = LabelMaterial("M_ReviewLabel", Vector2.zero, 3020);
            var outline = new List<Material>();
            const float o = 0.06f;
            foreach (var d in new[] { new Vector2(o, 0f), new Vector2(-o, 0f), new Vector2(0f, o), new Vector2(0f, -o),
                                      new Vector2(o, o) * 0.7f, new Vector2(-o, o) * 0.7f, new Vector2(o, -o) * 0.7f, new Vector2(-o, -o) * 0.7f })
                outline.Add(LabelMaterial($"M_ReviewLabel_Outline_{outline.Count}", d, 3019));
            foreach (var l in meta.labels) MakeLabel(labels, l, text, outline);

            foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.tag = EditorOnly;
        }

        static readonly Dictionary<string, Color> LabelColors = new Dictionary<string, Color>
        {
            { "REAL", new Color32(110, 225, 120, 255) }, { "ADAPTED", new Color32(250, 195, 70, 255) },
            { "INVENTED", new Color32(205, 140, 255, 255) }, { "flag", new Color32(255, 225, 60, 255) },
            { "west", new Color32(90, 200, 255, 255) }, { "east", new Color32(255, 110, 185, 255) },
            { "bridge", new Color32(120, 225, 255, 255) }, { "ravine", new Color32(120, 225, 255, 255) },
            { "cordon", new Color32(120, 160, 255, 255) }, { "connector", new Color32(225, 225, 225, 255) },
            { "lane", Color.white }, { "area", new Color32(240, 240, 230, 255) },
        };

        static void MakeLabel(GameObject parent, Label l, Material text, List<Material> outline)
        {
            var go = new GameObject("Label_" + l.text);
            go.transform.SetParent(parent.transform, false);
            go.transform.position = new Vector3(l.x, l.y, l.z);
            go.transform.localScale = Vector3.one * l.size * LabelScale;
            Color c = LabelColors.TryGetValue(l.kind, out var k) ? k : Color.white;
            string sub = string.IsNullOrEmpty(l.sub) ? "" : $"\n<size=40>{l.sub}</size>";
            string colored = l.text + (sub.Length > 0 ? $"\n<size=40><color=#{ColorUtility.ToHtmlStringRGB(Color.Lerp(c, Color.white, 0.35f))}>{l.sub}</color></size>" : "");
            for (int i = 0; i < outline.Count; i++) TextPart(go, "Outline" + i, l.text + sub, Color.black, outline[i]);
            TextPart(go, "Text", colored, c, text);
        }

        static void TextPart(GameObject parent, string name, string s, Color color, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var tm = go.AddComponent<TextMesh>();
            tm.font = LabelFont;
            tm.fontSize = 64;
            tm.characterSize = 0.1f;
            tm.anchor = TextAnchor.LowerCenter;
            tm.alignment = TextAlignment.Center;
            tm.richText = true;
            tm.color = color;
            tm.text = s;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        static Material LabelMaterial(string name, Vector2 offset, int queue)
        {
            string path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find(LabelShader);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.shader = shader;
            mat.mainTexture = LabelFont.material.mainTexture;
            mat.SetVector("_Offset", new Vector4(offset.x, offset.y, 0f, 0f));
            mat.renderQueue = queue;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // ---------- Review cameras and the walker ----------

        // World yaw of the frame's +u axis (along the main street, west to east).
        static float StreetYaw(Meta meta)
        {
            float r = meta.frame_rotation_deg * Mathf.Deg2Rad;
            return Mathf.Atan2(Mathf.Cos(r), Mathf.Sin(r)) * Mathf.Rad2Deg;
        }

        // ---------- Walk test ----------

        // A CharacterController set up like the walker (radius, stand height, 36 deg slope limit, 0.4 m step)
        // walks every lane and ground connector end to end, both ways, on the built terrain.
        [MenuItem("Lockdown/Review/Play Ground Walk Test")]
        public static void WalkTestMenu()
        {
            foreach (var line in WalkTest()) Debug.Log("[Lockdown] " + line);
        }

        public static List<string> WalkTest()
        {
            var lines = new List<string>();
            var meta = JsonUtility.FromJson<Meta>(File.ReadAllText(Path.Combine(PlayDir, "play_meta.json")));
            if (File.Exists(LockdownGreybox.MetaPath)) // the greybox adds its own routes (terraces, stairs, passages)
                meta.routes.AddRange(JsonUtility.FromJson<Meta>(File.ReadAllText(LockdownGreybox.MetaPath)).routes);
            var go = new GameObject("PlayWalkTester");
            var cc = go.AddComponent<CharacterController>();
            var w = Object.FindFirstObjectByType<Lockdown.LockdownWalker>();
            cc.radius = w != null ? w.radius : 0.35f;
            cc.height = w != null ? w.standHeight : 1.8f;
            cc.center = new Vector3(0f, cc.height * 0.5f, 0f);
            cc.slopeLimit = 36f;
            cc.stepOffset = 0.4f;
            cc.skinWidth = 0.03f;
            var walkerCc = w != null ? w.GetComponent<CharacterController>() : null;
            bool walkerOn = walkerCc != null && walkerCc.enabled;
            if (walkerCc != null) walkerCc.enabled = false; // the parked walker must not block a route
            int pass = 0, total = 0;
            var terrain = GameObject.Find("_Ground/Terrain_Play").GetComponent<Terrain>(); // looked up once, not per step
            try
            {
                foreach (var r in meta.routes)
                for (int dir = 0; dir < 2; dir++)
                {
                    total++;
                    int n = r.xs.Length;
                    Func<int, Vector2> wp = i => dir == 0 ? new Vector2(r.xs[i], r.zs[i]) : new Vector2(r.xs[n - 1 - i], r.zs[n - 1 - i]);
                    string result = Walk(cc, terrain, wp, n, out float length, out Vector3 end);
                    if (result == null) pass++;
                    lines.Add($"{(result == null ? "PASS" : "FAIL")}  {r.name} ({(dir == 0 ? "forward" : "back")}): " +
                              $"{length:0} m, {length / 6.5f:0.0} s sprint" + (result == null ? "" : $" - {result} at ({end.x:0.0}, {end.y:0.0}, {end.z:0.0})"));
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
                if (walkerCc != null) walkerCc.enabled = walkerOn;
            }
            lines.Insert(0, $"Play ground walk test: {pass}/{total} PASS");
            return lines;
        }

        static string Walk(CharacterController cc, Terrain terrain, Func<int, Vector2> wp, int n, out float length, out Vector3 end)
        {
            const float dt = 0.02f, speed = 4.5f;
            length = 0f;
            Vector2 s = wp(0);
            cc.transform.position = new Vector3(s.x, GroundY(terrain, s.x, s.y) + 0.1f, s.y);
            Physics.SyncTransforms();
            float vy = 0f;
            for (int i = 1; i < n; i++)
            {
                Vector2 target = wp(i);
                float t = 0f, stuck = 0f;
                while (true)
                {
                    Vector3 p = cc.transform.position;
                    var flat = new Vector3(target.x - p.x, 0f, target.y - p.z);
                    if (flat.magnitude < 0.3f) break;
                    if (t > 20f || stuck > 1f) { end = p; return "stuck"; }
                    vy = cc.isGrounded ? -2f : vy - 9.81f * dt;
                    Vector3 step = flat.normalized * speed * dt;
                    cc.Move(new Vector3(step.x, vy * dt, step.z));
                    Vector3 q = cc.transform.position;
                    float moved = new Vector2(q.x - p.x, q.z - p.z).magnitude;
                    length += moved;
                    stuck = moved < speed * dt * 0.2f ? stuck + dt : 0f;
                    t += dt;
                    if (q.y < GroundY(terrain, q.x, q.z) - 1f) { end = q; return "fell through"; }
                }
            }
            end = cc.transform.position;
            return null;
        }

        static float GroundY(Terrain t, float x, float z)
        {
            return t.SampleHeight(new Vector3(x, 0f, z)) + t.transform.position.y;
        }

        static Point Stake(Meta meta, string name) => meta.stakes.Find(s => s.name == name);

        static Vector3 Eye(Point p) => new Vector3(p.x, p.y + EyeHeight, p.z);

        static void BuildCameras(Meta meta)
        {
            GameObject review = GameObject.Find("_Review") ?? new GameObject("_Review");
            Transform old = review.transform.Find("PlayCameras");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var cams = new GameObject("PlayCameras");
            cams.transform.SetParent(review.transform, false);

            float yaw = StreetYaw(meta);
            var c = new Vector3(meta.frame_center[0], meta.flag_b_height, meta.frame_center[1]);
            Quaternion street = Quaternion.Euler(0f, yaw, 0f);
            Vector3 right = street * Vector3.forward, uphill = street * Vector3.left;

            Camera("Cam_Play_TopDown", cams, c + Vector3.up * 210f, Quaternion.Euler(90f, yaw - 90f, 0f), 50f);
            Vector3 aerial = c - uphill * 120f - right * 40f + Vector3.up * 75f;
            Camera("Cam_Play_Aerial_South", cams, aerial, Quaternion.LookRotation(c + uphill * 8f - aerial), 50f);
            Vector3 aerialW = c - right * 130f - uphill * 30f + Vector3.up * 60f;
            Camera("Cam_Play_Aerial_West", cams, aerialW, Quaternion.LookRotation(c - aerialW), 50f);

            Vector3 labelsCam = c - uphill * 75f - right * 15f + Vector3.up * 32f;
            Camera("Cam_Play_Labels", cams, labelsCam, Quaternion.LookRotation(c + uphill * 10f - labelsCam), 55f);
            Camera("Cam_Play_SpawnWest", cams, Eye(Stake(meta, "Spawn_West")), Quaternion.Euler(2f, yaw, 0f), 64f);
            Camera("Cam_Play_SpawnEast", cams, Eye(Stake(meta, "Spawn_East")), Quaternion.Euler(2f, yaw + 180f, 0f), 64f);
            // From the south kerb of the main street, slightly raised, so the whole climb to the upper lane is in view.
            Camera("Cam_Play_FlagB_Uphill", cams, Eye(Stake(meta, "Flag_B")) - uphill * 4f - right * 2.5f + Vector3.up * 1.5f, Quaternion.Euler(-16f, yaw - 90f, 0f), 64f);
            Camera("Cam_Play_FlagA_West", cams, Eye(Stake(meta, "Flag_A")), Quaternion.Euler(0f, yaw - 160f, 0f), 64f);
            foreach (var t in cams.GetComponentsInChildren<Transform>(true)) t.gameObject.tag = EditorOnly;
        }

        static void Camera(string name, GameObject parent, Vector3 pos, Quaternion rot, float fov)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.transform.SetPositionAndRotation(pos, rot);
            var cam = go.AddComponent<UnityEngine.Camera>();
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 30000f;
            cam.depth = -10f;
            cam.enabled = false; // rendered on demand for screenshots, never in the Game view
            var data = go.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        }

        static void PlaceWalker(Meta meta)
        {
            var walker = Object.FindFirstObjectByType<Lockdown.LockdownWalker>();
            Point s = Stake(meta, "Spawn_West");
            if (walker == null || s == null) return;
            walker.transform.SetPositionAndRotation(new Vector3(s.x, s.y + 0.05f, s.z), Quaternion.Euler(0f, StreetYaw(meta), 0f));
        }
    }
}
