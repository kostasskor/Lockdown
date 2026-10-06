using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Lockdown;

namespace Lockdown.EditorTools
{
    // Task 5 test corner: the approved test house in a small street corner (a W-E street with raking sun, a shaded
    // N-S alley along the house's east side, a rough limestone garden wall, a dry-earth yard with the olive tree),
    // under the review lighting, with the review cameras the brief asks for. Rebuilding is idempotent: `_Corner` and
    // `_Review` are rebuilt from scratch; the house and the walls are prefab instances (never edited here).
    public static class LockdownTestCorner
    {
        public const string ScenePath = "Assets/Lockdown/Scenes/Review_TestCorner.unity";
        const string MeshDir = "Assets/Lockdown/Models/Generated/ReviewScenes";
        const string HousePrefab = "Assets/Lockdown/Prefabs/Houses/House_BlueTrimWhite.prefab";
        const string WallsPrefab = "Assets/Lockdown/Prefabs/Houses/Corner_Walls.prefab";
        public const string OlivePrefab = "Assets/Lockdown/Prefabs/Props/old_olive_tree.prefab";
        const string EditorOnly = "EditorOnly";

        // Corner layout (house local frame: street facade at z 0 facing south, west wall at x 0, east wall x 9.2;
        // its outside stair and stair wall reach x 10.65).
        const float StreetNorth = -2.1f, StreetSouth = -8.9f;  // a 6.8 m village street (VILLAGE_NOTES 2: 6-7 m)
        const float KerbW = 0.2f;
        public static readonly Vector3 OlivePos = new(-5f, 0.15f, -12.6f);
        const string TileMatPath = "Assets/Lockdown/Materials/Review/M_Review_Grey30.mat";
        public static readonly Vector3 TileSun = new(22f, 0.17f, 11f), TileShade = new(17f, 0.17f, 3f);

        // Pixel centre of a world point in a 1920x1080 capture of a camera (for Tools/review/luminance.py regions).
        public static Vector2Int PixelOf(string cameraName, Vector3 world)
        {
            var cam = Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None).First(c => c.name == cameraName);
            cam.aspect = 16f / 9f;
            var v = cam.WorldToViewportPoint(world);
            return new Vector2Int(Mathf.RoundToInt(v.x * 1920f), Mathf.RoundToInt((1f - v.y) * 1080f));
        }

        [MenuItem("Lockdown/Build/Corner Walls")]
        public static void BuildCornerWalls() => LockdownHouseBuilder.Build(HouseSpecs.CornerWalls());

        [MenuItem("Lockdown/Review/Build Test Corner")]
        public static void BuildMenu() => Build();

        public static void Build()
        {
            if (EditorApplication.isPlaying) { Debug.LogError("[Lockdown] Exit Play mode first."); return; }
            if (AssetDatabase.LoadAssetAtPath<GameObject>(WallsPrefab) == null) BuildCornerWalls();

            EditorSceneManager.SaveOpenScenes();
            if (!File.Exists(ScenePath))
            {
                var created = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SaveScene(created, ScenePath);
            }
            else if (EditorSceneManager.GetActiveScene().path != ScenePath)
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var scene = EditorSceneManager.GetActiveScene();

            LockdownReviewLighting.Apply(lightmaps: false);

            // ---- The corner (static, lit by the baked bounce) ----
            var corner = Fresh("_Corner");
            Instance(corner, HousePrefab, Vector3.zero);
            Instance(corner, WallsPrefab, Vector3.zero);
            BuildGround(corner);

            // Props: potted plants at the house (as in Task 4), a blue drum in the alley, a box by the store doorway.
            LockdownHouseBuilder.PlaceProp(corner, "potted_plant_02", new Vector3(-0.45f, 0f, 1.6f), 70f, 1.2f);
            LockdownHouseBuilder.PlaceProp(corner, "potted_plant_01", new Vector3(3.0f, 0.27f, -0.45f), 200f, 1.0f);
            LockdownHouseBuilder.PlaceProp(corner, "cardboard_box_01", new Vector3(9.0f, 0.25f, -1.45f), 17f, 1.0f);
            var olive = AssetDatabase.LoadAssetAtPath<GameObject>(OlivePrefab);
            if (olive != null) Instance(corner, OlivePrefab, OlivePos, 140f);
            else Debug.LogWarning("[Lockdown] Olive tree not built yet (waiting for the Sketchfab import).");

            // Reflection probes (baked with the lighting): the street, and the upper room for the interior camera.
            Probe(corner, "ReflectionProbe_Street", new Vector3(4.5f, 2.5f, -5.5f), new Vector3(44f, 14f, 30f), 0);
            Probe(corner, "ReflectionProbe_UpperRoom", new Vector3(4.6f, 4.45f, 4.1f), new Vector3(8.6f, 2.8f, 5.9f), 1);

            // Everything in the corner is static and contributes to the bake; APV lights it through probes.
            var flags = StaticEditorFlags.ContributeGI | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic;
            foreach (var t in corner.GetComponentsInChildren<Transform>(true)) GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags);
            foreach (var r in corner.GetComponentsInChildren<MeshRenderer>(true)) r.receiveGI = ReceiveGI.LightProbes;

            // The real landform around it (review-only copies of the Task 3 terrain, the corner cut into the slope as
            // a terrace), so the light, the haze and the views can be judged against the photos.
            BuildLandscape();

            // Adaptive Probe Volume over the corner only (the landscape is lit by the probes, it doesn't bake).
            var apv = Fresh("ProbeVolume");
            apv.transform.position = new Vector3(4f, 7f, -3f);
            var pv = apv.AddComponent<ProbeVolume>();
            pv.mode = ProbeVolume.Mode.Local;
            pv.size = new Vector3(64f, 18f, 52f);

            // Walker on the street, for walking the corner at player scale.
            if (Object.FindFirstObjectByType<LockdownWalker>() == null)
            {
                var walkerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Lockdown/Prefabs/Player/Walker.prefab");
                if (walkerPrefab != null)
                {
                    var walker = (GameObject)PrefabUtility.InstantiatePrefab(walkerPrefab, scene);
                    walker.transform.SetPositionAndRotation(new Vector3(-4f, 0.05f, -5.5f), Quaternion.Euler(0f, 80f, 0f));
                }
            }

            // ---- Review helpers (EditorOnly) ----
            var review = Fresh("_Review");
            var cams = new GameObject("Cameras");
            cams.transform.SetParent(review.transform, false);
            // Brief Task 5: into the sun, sun behind, a shaded alley, the interior. Player FOV (64), eye height.
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_IntoSun", new Vector3(26f, 1.7f, -5.0f), new Vector3(-20f, 3.2f, -7.0f), 64f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_SunBehind", new Vector3(-15f, 1.7f, -7.2f), new Vector3(9f, 2.6f, -0.5f), 64f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_ShadedAlley", new Vector3(11.7f, 1.7f, -1.2f), new Vector3(11.4f, 2.0f, 10f), 64f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_Interior", new Vector3(8.6f, 4.65f, 5.6f), new Vector3(2.0f, 3.4f, 3.4f), 64f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_Overview", new Vector3(-12f, 9f, -24f), new Vector3(5f, 1.5f, -2f), 50f);
            // The six views marked in the review (estimated from Game-view screenshots), for before/after strips.
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_User1", new Vector3(3.5f, 1.7f, -14f), new Vector3(4f, 3.5f, -4.9f), 64f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_User2", new Vector3(23f, 1.7f, 4f), new Vector3(5f, 6.8f, 4f), 64f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_User3", new Vector3(17f, 1.7f, -9f), new Vector3(15f, 1.8f, -2.2f), 64f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_User4", new Vector3(1.2f, 4.65f, 3f), new Vector3(9f, 4.2f, 4f), 64f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_User5", new Vector3(1.0f, 4.65f, 5.5f), new Vector3(8f, 4.2f, 1f), 64f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_User6", new Vector3(1.5f, 4.65f, 0.2f), new Vector3(8.5f, 4.0f, 6f), 64f);
            // Round 2: porch corner, upper room corners, balcony, the glazed door leaf, wide room; plus narrow ceiling-edge zooms.
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_User7", new Vector3(2.6f, 1.6f, -1.6f), new Vector3(0.4f, 0.6f, -0.2f), 64f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_User8", new Vector3(6.5f, 4.65f, 2.5f), new Vector3(0.3f, 4.2f, 5.5f), 64f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_User9", new Vector3(1.5f, 4.65f, 5.5f), new Vector3(8.9f, 4.3f, 2.5f), 64f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_User10", new Vector3(5.0f, 4.65f, -1.0f), new Vector3(-0.5f, 4.2f, -0.4f), 64f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_User11", new Vector3(4.5f, 4.65f, 4.0f), new Vector3(3.0f, 4.3f, 0f), 64f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_User12", new Vector3(8.5f, 4.65f, 6.0f), new Vector3(2f, 4.3f, 0.5f), 64f);
            // Round 3: ground-floor room (pier base, ceiling) and the porch corner.
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_User14", new Vector3(3.2f, 1.6f, -1.3f), new Vector3(0.2f, 1.0f, 0.1f), 64f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_User16", new Vector3(7.0f, 1.6f, 1.8f), new Vector3(0.3f, 1.5f, 1.8f), 64f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_Zoom_Ceil0b", new Vector3(3.5f, 1.6f, 5.0f), new Vector3(3.5f, 2.75f, 3.0f), 40f);
            // Round 4: the south-west corner from inside and outside, and the ground-floor ceiling edges.
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_SW_In", new Vector3(2.4f, 1.5f, 2.4f), new Vector3(0.3f, 0.7f, 0.3f), 64f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_SW_Out", new Vector3(2.2f, 1.5f, -1.5f), new Vector3(0.3f, 0.8f, 0f), 64f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_Ceil0_NW", new Vector3(4.5f, 1.6f, 3.5f), new Vector3(0.3f, 2.6f, 6.7f), 64f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_Ceil0_SE", new Vector3(3.0f, 1.6f, 4.5f), new Vector3(8.9f, 2.6f, 0.3f), 64f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_Zoom_CeilW",new Vector3(4.5f, 4.65f, 4.0f), new Vector3(0.3f, 5.85f, 4.0f), 20f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_Zoom_CeilSeam", new Vector3(3.0f, 4.65f, 4.0f), new Vector3(3.0f, 5.85f, 1.21f), 20f);
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_Zoom_Ceil0", new Vector3(3.0f, 1.6f, 4.0f), new Vector3(5.5f, 2.75f, 1.0f), 30f);
            // Brightness check (Tools/review/luminance.py): the sunlit west wall and the shaded north wall of the house
            // side by side, with sunlit ground in front and the house's shadow on the ground beyond.
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_Measure", new Vector3(-9f, 2.2f, 16f), new Vector3(4.5f, 1.6f, 4f), 64f);
            // ... and the garden earth behind the stone wall: the wall's shadow next to sunlit earth (one material).
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_Measure_Ground", new Vector3(27f, 3.5f, 4f), new Vector3(17f, 0.15f, 3f), 64f);
            // Brightness test tiles (identical 30 % grey): one in open sun, one in the house's shadow on the open garden,
            // seen from above by Cam_Measure_Tiles. Not static: lit by the probes like a player.
            var grey = AssetDatabase.LoadAssetAtPath<Material>(TileMatPath);
            if (grey == null)
            {
                grey = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                grey.SetColor("_BaseColor", new Color(0.3f, 0.3f, 0.3f)); // linear 0.3 albedo (sRGB ~149)
                grey.SetFloat("_Smoothness", 0f);
                AssetDatabase.CreateAsset(grey, TileMatPath);
            }
            foreach (var (name, pos) in new[] { ("Tile_Sun", TileSun), ("Tile_Shade", TileShade) })
            {
                var tile = GameObject.CreatePrimitive(PrimitiveType.Quad);
                tile.name = name;
                Object.DestroyImmediate(tile.GetComponent<Collider>());
                tile.transform.SetParent(review.transform, false);
                tile.transform.SetPositionAndRotation(pos, Quaternion.Euler(90f, 0f, 0f));
                tile.GetComponent<MeshRenderer>().sharedMaterial = grey;
            }
            LockdownHouseBuilder.ReviewCamera(cams, "Cam_Measure_Tiles", new Vector3(19.5f, 14f, 7f), new Vector3(19.5f, 0f, 7.01f), 50f);
            foreach (var t in review.GetComponentsInChildren<Transform>(true)) t.gameObject.tag = EditorOnly;

            ConfigureBakingSet();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[Lockdown] Test corner ready: " + ScenePath);
        }

        // ---- Landscape: review-only copies of the real ground (the Task 3 originals are locked and never edited) ----
        // The corner sits on a terrace cut into the real slope at the village origin (528.6 m): inside the pad the copy
        // is flattened just under the corner's ground meshes, and it blends back to the real slope over 25 m.
        // Copies and layers live in LandDir, gitignored and regenerated here.
        const string LandDir = MeshDir + "/Landscape";
        const float PadAsl = 528.6f, PadBlend = 25f;
        static readonly Rect Pad = Rect.MinMaxRect(-30f, -20f, 40f, 14f); // x, z

        static void BuildLandscape()
        {
            if (!AssetDatabase.IsValidFolder(LandDir)) AssetDatabase.CreateFolder(MeshDir, "Landscape");
            var earthMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Lockdown/Materials/Ground/M_Earth.mat");
            var near = Layer("TL_Corner_Earth", earthMat, 4f, new Color(0.84f, 0.94f, 0.88f)); // less orange: real slopes are olive-ochre (lighting/02)
            // Hillsides: late-summer dry grass and earth, pale limestone patches and dark olive / scrub clumps (a review
            // stand-in generated below; real landscape materials and vegetation come in Phases 2 and 4).
            var far = Layer("TL_Corner_Far", earthMat, 60f, Color.white);
            far.diffuseTexture = HillsideTexture();
            far.normalMapTexture = null;
            far.diffuseRemapMax = Vector4.one;
            EditorUtility.SetDirty(far);

            var village = CopyTerrain("Assets/Lockdown/Terrain/TD_Village.asset", "TD_Corner_Village", far, near);
            var backdrop = CopyTerrain("Assets/Lockdown/Terrain/TD_Backdrop.asset", "TD_Corner_Backdrop", far);

            // Flatten the pad in the village copy (world y 0 = PadAsl; terrain heights are metres above sea level).
            const float tileOrigin = -1015.625f;
            int res = village.heightmapResolution;
            var h = village.GetHeights(0, 0, res, res);
            float cell = village.size.x / (res - 1);
            float padH = (PadAsl - 0.05f) / village.size.y;
            for (int j = 0; j < res; j++)
            for (int i = 0; i < res; i++)
            {
                float x = tileOrigin + i * cell, z = tileOrigin + j * cell;
                float dx = Mathf.Max(Pad.xMin - x, 0f, x - Pad.xMax), dz = Mathf.Max(Pad.yMin - z, 0f, z - Pad.yMax);
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d >= PadBlend) continue;
                float w = d / PadBlend;
                w = w * w * (3f - 2f * w);
                h[j, i] = Mathf.Lerp(padH, h[j, i], w);
            }
            village.SetHeights(0, 0, h);

            // Dry earth (near layer) on and just round the pad, the hillside tone beyond, blended from 2 to 8 m out.
            int ares = village.alphamapResolution;
            var alpha = new float[ares, ares, 2];
            float acell = village.size.x / ares;
            for (int j = 0; j < ares; j++)
            for (int i = 0; i < ares; i++)
            {
                float x = tileOrigin + (i + 0.5f) * acell, z = tileOrigin + (j + 0.5f) * acell;
                float dx = Mathf.Max(Pad.xMin - x, 0f, x - Pad.xMax), dz = Mathf.Max(Pad.yMin - z, 0f, z - Pad.yMax);
                float n = 1f - Mathf.Clamp01((Mathf.Sqrt(dx * dx + dz * dz) - 2f) / 6f); // wider, the earth scan stretched orange up the slopes
                alpha[j, i, 0] = 1f - n;
                alpha[j, i, 1] = n;
            }
            village.SetAlphamaps(0, 0, alpha);
            EditorUtility.SetDirty(village);
            AssetDatabase.SaveAssets();

            var land = Fresh("_Landscape");
            land.tag = EditorOnly;
            TerrainObject(land, "Terrain_Village", village, new Vector3(tileOrigin, -PadAsl, tileOrigin));
            TerrainObject(land, "Terrain_Backdrop", backdrop, new Vector3(-20000f, -PadAsl, -20000f));
        }

        static Texture2D HillsideTexture()
        {
            string path = $"{LandDir}/T_Corner_Hillside.png";
            const int n = 1024;
            var src = new Texture2D(2, 2);
            src.LoadImage(File.ReadAllBytes("Assets/Lockdown/ThirdParty/PolyHaven/Textures/dry_ground_rocks/dry_ground_rocks_diff_2k.jpg"));
            var px = new Color32[n * n];
            Color dry = new Color32(138, 126, 104, 255), rock = new Color32(150, 146, 136, 255), scrub = new Color32(52, 58, 38, 255);
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = (float)x / n, v = (float)y / n;
                Color c = src.GetPixelBilinear(u * 2f, v * 2f);            // fine grain from the scan, greyed
                float g = c.grayscale;
                Color baseCol = dry * (0.75f + 0.5f * g);
                float rocks = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.64f, 0.8f, Fbm(u, v, 4, 11))) * 0.7f;
                float bush = Mathf.InverseLerp(0.55f, 0.65f, Fbm(u, v, 12, 47) * 0.7f + Fbm(u, v, 40, 3) * 0.3f);
                Color col = Color.Lerp(baseCol, rock * (0.85f + 0.3f * g), rocks * 0.8f);
                col = Color.Lerp(col, scrub * (0.8f + 0.4f * g), Mathf.Clamp01(bush) * 0.9f);
                px[y * n + x] = col;
            }
            var tex = new Texture2D(n, n, TextureFormat.RGB24, true);
            tex.SetPixels32(px);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            Object.DestroyImmediate(src);
            AssetDatabase.ImportAsset(path);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // Periodic value noise (period = one texture tile), so the generated texture tiles without seams.
        static float ValueNoise(float u, float v, int period, int seed)
        {
            float x = u * period, y = v * period;
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float H(int i, int j)
            {
                uint h = (uint)(((i % period + period) % period) * 73856093 ^ ((j % period + period) % period) * 19349663 ^ seed * 83492791);
                h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
                return (h & 0xffff) / 65535f;
            }
            return Mathf.Lerp(Mathf.Lerp(H(x0, y0), H(x0 + 1, y0), fx), Mathf.Lerp(H(x0, y0 + 1), H(x0 + 1, y0 + 1), fx), fy);
        }

        static float Fbm(float u, float v, int period, int seed) =>
            ValueNoise(u, v, period, seed) * 0.55f + ValueNoise(u, v, period * 2, seed + 1) * 0.3f + ValueNoise(u, v, period * 4, seed + 2) * 0.15f;

        static TerrainData CopyTerrain(string src, string name, params TerrainLayer[] layers)
        {
            string dst = $"{LandDir}/{name}.asset";
            var td = AssetDatabase.LoadAssetAtPath<TerrainData>(dst);
            if (td == null)
            {
                if (!AssetDatabase.CopyAsset(src, dst)) Debug.LogError($"[Lockdown] Could not copy {src}.");
                td = AssetDatabase.LoadAssetAtPath<TerrainData>(dst);
            }
            else // rebuilt from the original's heights, so the pad is never applied twice
            {
                var o = AssetDatabase.LoadAssetAtPath<TerrainData>(src);
                td.SetHeights(0, 0, o.GetHeights(0, 0, o.heightmapResolution, o.heightmapResolution));
            }
            td.terrainLayers = layers;
            if (layers.Length > 1) td.alphamapResolution = 512; // ~4 m cells: enough to fade the earth out round the pad
            return td;
        }

        // The earth scan tinted like M_Earth (times `shift`): the landscape's real materials come in Phase 2.
        static TerrainLayer Layer(string name, Material earth, float tile, Color shift)
        {
            string path = $"{LandDir}/{name}.terrainlayer";
            var l = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
            if (l == null) { l = new TerrainLayer(); AssetDatabase.CreateAsset(l, path); }
            l.diffuseTexture = (Texture2D)earth.GetTexture("_BaseMap");
            l.normalMapTexture = (Texture2D)earth.GetTexture("_BumpMap");
            l.tileSize = new Vector2(tile, tile);
            Color tint = earth.GetColor("_BaseColor");
            l.diffuseRemapMax = new Vector4(tint.r * shift.r, tint.g * shift.g, tint.b * shift.b, 1f);
            l.smoothness = 0f;
            l.metallic = 0f;
            EditorUtility.SetDirty(l);
            return l;
        }

        static void TerrainObject(GameObject parent, string name, TerrainData td, Vector3 pos)
        {
            var go = Terrain.CreateTerrainGameObject(td);
            go.name = name;
            go.tag = EditorOnly;
            go.transform.SetParent(parent.transform, false);
            go.transform.position = pos;
            var t = go.GetComponent<Terrain>();
            t.materialTemplate = GraphicsSettings.currentRenderPipeline.defaultTerrainMaterial;
            t.heightmapPixelError = 3f;
            t.basemapDistance = 400f;
            t.drawTreesAndFoliage = false;
            GameObjectUtility.SetStaticEditorFlags(go, 0); // not in the bake: lit by the probes and the sky
        }

        // Street (worn asphalt), the dry-earth yard across it behind a whitewashed kerb (VILLAGE_NOTES 4b: whitewashed
        // curbs in the west part of the village), and the garden behind the stone wall. Metre UVs, saved as assets.
        static void BuildGround(GameObject corner)
        {
            if (!AssetDatabase.IsValidFolder(MeshDir)) AssetDatabase.CreateFolder("Assets/Lockdown/Models/Generated", "ReviewScenes");
            var street = new MeshPart();
            AddBox(street, new Vector3(Pad.xMin, -0.2f, StreetSouth), new Vector3(Pad.xMax, 0f, Pad.yMax)); // the street and everything north of it on the pad
            var earth = new MeshPart();
            AddBox(earth, new Vector3(-16f, -0.2f, -18f), new Vector3(6f, 0.15f, StreetSouth - KerbW));    // yard south of the street
            AddBox(earth, new Vector3(13.5f, 0f, -1.7f), new Vector3(24f, 0.15f, 12f));                   // garden behind the stone wall
            var kerb = new MeshPart();
            AddBox(kerb, new Vector3(-16f, -0.2f, StreetSouth - KerbW), new Vector3(6f, 0.17f, StreetSouth));
            // The alley: a poured concrete strip (VILLAGE_NOTES 5: lanes are concrete, not asphalt), 3 cm proud of the
            // street, along the stair wall and wider behind the landing. Pale concrete is what lets real alleys glow.
            var alley = new MeshPart();
            AddBox(alley, new Vector3(10.65f, -0.05f, StreetNorth), new Vector3(13f, 0.03f, 1.35f));
            AddBox(alley, new Vector3(9.2f, -0.05f, 1.35f), new Vector3(13f, 0.03f, 10f));
                        Ground(corner, "Ground_Street", street, "Assets/Lockdown/Materials/Ground/M_Street.mat");
            Ground(corner, "Ground_Alley", alley, "Assets/Lockdown/Materials/Houses/M_House_Concrete.mat");
            Ground(corner, "Ground_Earth", earth, "Assets/Lockdown/Materials/Ground/M_Earth.mat");
            Ground(corner, "Ground_Kerb", kerb, "Assets/Lockdown/Materials/Houses/M_House_RenderOld.mat");
        }

        public static readonly string[] ReviewCams = { "Cam_IntoSun", "Cam_SunBehind", "Cam_ShadedAlley", "Cam_Interior" };
        public static readonly string[] UserCams = { "Cam_User1", "Cam_User2", "Cam_User3", "Cam_User4", "Cam_User5", "Cam_User6" };
        public static readonly string[] UserCams2 = { "Cam_User7", "Cam_User8", "Cam_User9", "Cam_User10", "Cam_User11", "Cam_User12" };
        public static readonly string[] ZoomCams = { "Cam_Zoom_CeilW", "Cam_Zoom_CeilSeam", "Cam_Zoom_Ceil0" };
        public static readonly string[] Cams4 = { "Cam_SW_In", "Cam_SW_Out", "Cam_Ceil0_NW", "Cam_Ceil0_SE" };
        public static readonly string[] UserCams3 ={ "Cam_User10", "Cam_User14", "Cam_User16", "Cam_Zoom_Ceil0", "Cam_Zoom_Ceil0b" };

        // Rebuild, preview-bake (seconds on this small corner) and capture the review cameras into Screenshots/Task5/.
        // measure: the cameras render without grade or tonemapping, for Tools/review/luminance.py.
        public static void BakeAndCapture(string prefix, bool measure, params string[] cams)
        {
            Build();
            ConfigureBakingSet();
            Lightmapping.Bake();
            bool prev = EditorSettings.asyncShaderCompilation;
            EditorSettings.asyncShaderCompilation = false;
            LockdownReviewLighting.SetMeasureMode(measure);
            try
            {
                foreach (var c in cams.Length > 0 ? cams : ReviewCams) LockdownCapture.Capture(c, $"Task5/{prefix}_{c}.png");
            }
            finally
            {
                LockdownReviewLighting.SetMeasureMode(false);
                EditorSettings.asyncShaderCompilation = prev;
            }
        }

        // The APV baking set (created by the first bake, next to the scene): dilation on, so probes that end up inside
        // floors and walls take their neighbours' light instead of going black at wall bases and on alley floors.
        // Probe grid: the finest spacing (m) and the number of coarser levels (each 3x). Walls are 0.2 m thick, rooms ~3 m.
        public static float ProbeSpacing = 0.5f; // 0.25 m put valid probes inside the 0.3 m slabs: big bright blocks leaked into the ground-floor room
        public static int ProbeLevels = 2;
        public static float DilationDistance = 1.5f;
        // Grid shifted so probe rows land inside the 0.3 m walls and slabs (x/z = 0.15, y = 2.9), never on their faces:
        // on the round grid a row sat on the outer wall faces in the sun and leaked into corners, ceiling edges, the balcony.
        public static Vector3 ProbeOffset = new(0.15f, 0.4f, 0.15f);
        public static bool VirtualOffset = false; // on, probes pushed out of thin slabs picked up sky light: a bright strip along every ceiling edge

        static void ConfigureBakingSet()
        {
            var set = AssetDatabase.LoadMainAssetAtPath(Path.ChangeExtension(ScenePath, null) + "/Review_TestCorner Baking Set.asset");
            if (set == null) return;
            var so = new SerializedObject(set);
            so.FindProperty("settings.dilationSettings.enableDilation").boolValue = true;
            so.FindProperty("settings.dilationSettings.dilationDistance").floatValue = DilationDistance;
            so.FindProperty("settings.virtualOffsetSettings.useVirtualOffset").boolValue = VirtualOffset;
            so.FindProperty("minDistanceBetweenProbes").floatValue = ProbeSpacing;
            so.FindProperty("probeOffset").vector3Value = ProbeOffset;
            so.FindProperty("simplificationLevels").intValue = ProbeLevels;
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }

        static void AddBox(MeshPart part, Vector3 min, Vector3 max) =>
            part.Box((min + max) * 0.5f, max - min, Quaternion.identity, Slot.Render);

        static void Ground(GameObject parent, string name, MeshPart part, string matPath)
        {
            var mesh = LockdownHouseBuilder.SaveMesh(part.ToMesh("Corner_" + name, out _), $"{MeshDir}/Corner_{name}.asset");
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        static void Probe(GameObject parent, string name, Vector3 pos, Vector3 size, int importance)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = pos;
            var probe = go.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Baked;
            probe.size = size;
            probe.resolution = 256;
            probe.boxProjection = true;
            probe.importance = importance;
        }

        static GameObject Instance(GameObject parent, string prefabPath, Vector3 pos, float yaw = 0f)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath), parent.scene);
            go.transform.SetParent(parent.transform, false);
            go.transform.SetLocalPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            return go;
        }

        static GameObject Fresh(string name)
        {
            var scene = EditorSceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects().Where(r => r.name == name).ToList()) Object.DestroyImmediate(root);
            return new GameObject(name);
        }
    }
}
