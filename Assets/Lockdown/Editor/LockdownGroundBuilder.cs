using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace Lockdown.EditorTools
{
    // Builds the real ground (Task 3) from the heightmaps written by Tools/terrain/lockdown_dem.py:
    // a 2 km village terrain on the real slope, set into a hole in a 40 km low-resolution backdrop.
    // Local transverse Mercator centred on the village: +X east, +Z true north, Y = metres above sea level.
    public static class LockdownGroundBuilder
    {
        // Set to true once Task 3 is approved: the terrain is then sculpted by later tasks and must never
        // be overwritten by a rebuild (project rule: approved generated content is frozen).
        static readonly bool Locked = true; // Task 3 approved

        const string TerrainDir = "Assets/Lockdown/Terrain";
        const string LayerDir = TerrainDir + "/Layers";
        const string TexDir = "Assets/Lockdown/Textures/Review";
        const string GridTexPath = TexDir + "/T_ReviewGrid_diff.png";
        const string FlatTexPath = TexDir + "/T_ReviewFlat_diff.png";
        const string EditorOnly = "EditorOnly";

        // Sun approximation for reviews until Task 5/66: azimuth 263 deg (just south of west), 20 deg up.
        public const float SunAzimuth = 263f, SunElevation = 20f;
        const float SkirtDepth = 8f;

        [Serializable] class GridInfo { public string file; public float size; public int resolution; public int hole_first_cell; public int hole_cells; }
        [Serializable] class Meta { public float base_height; public float top_height; public float origin_height; public GridInfo village; public GridInfo backdrop; }

        static string DemDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "SourceData", "dem"));

        [MenuItem("Lockdown/Build/Real Ground")]
        public static void BuildRealGround()
        {
            if (Locked)
            {
                Debug.LogError("[Lockdown] Real Ground is approved and locked; a rebuild would erase later sculpting.");
                return;
            }
            string metaPath = Path.Combine(DemDir, "dem_meta.json");
            if (!File.Exists(metaPath))
            {
                Debug.LogError("[Lockdown] " + metaPath + " missing. Run: uv run --with rasterio --with numpy Tools/terrain/lockdown_dem.py");
                return;
            }
            var meta = JsonUtility.FromJson<Meta>(File.ReadAllText(metaPath));
            EnsureFolder(LayerDir);

            TerrainLayer gridLayer = GetOrCreateLayer(LayerDir + "/TL_ReviewGrid.terrainlayer", GetGridTexture(), 10f);
            TerrainLayer flatLayer = GetOrCreateLayer(LayerDir + "/TL_ReviewFlat.terrainlayer", GetFlatTexture(), 50f);

            TerrainData village = BuildData(meta, meta.village, TerrainDir + "/TD_Village.asset", gridLayer, false);
            TerrainData backdrop = BuildData(meta, meta.backdrop, TerrainDir + "/TD_Backdrop.asset", flatLayer, true);

            GameObject root = GameObject.Find("_Ground");
            if (root != null) Object.DestroyImmediate(root);
            root = new GameObject("_Ground");
            Terrain tv = CreateTerrain(root, "Terrain_Village", village, meta, meta.village.size, 1f);
            Terrain tb = CreateTerrain(root, "Terrain_Backdrop", backdrop, meta, meta.backdrop.size, 10f);
            tv.basemapDistance = 3000f; // keeps the review grid sharp in aerial views
            tb.basemapDistance = 20000f;

            PlaceWalker(tv);
            SetSun();
            EditorSceneManager.MarkSceneDirty(root.scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Lockdown] Real ground built: village {meta.village.size} m, backdrop {meta.backdrop.size} m, " +
                      $"heights {meta.base_height}..{meta.top_height} m, origin at {meta.origin_height} m.");
        }

        static TerrainData BuildData(Meta meta, GridInfo info, string path, TerrainLayer layer, bool cutHole)
        {
            int res = info.resolution;
            byte[] raw = File.ReadAllBytes(Path.Combine(DemDir, info.file));
            if (raw.Length != res * res * 2) throw new Exception($"{info.file}: expected {res * res * 2} bytes, got {raw.Length}");
            var heights = new float[res, res];
            for (int z = 0; z < res; z++)
            for (int x = 0; x < res; x++)
            {
                int i = (z * res + x) * 2;
                heights[z, x] = (raw[i] | (raw[i + 1] << 8)) / 65535f; // rows south to north = terrain z
            }

            // Skirt: the hole is one cell smaller than the village tile, and the backdrop nodes inside the tile
            // drop below it, so LOD cracks along the village edge show ground instead of sky.
            int first = info.hole_first_cell, last = info.hole_first_cell + info.hole_cells;
            if (cutHole)
            {
                float drop = SkirtDepth / (meta.top_height - meta.base_height);
                for (int z = first + 1; z < last; z++)
                for (int x = first + 1; x < last; x++)
                    heights[z, x] = Mathf.Max(0f, heights[z, x] - drop);
            }

            var data = AssetDatabase.LoadAssetAtPath<TerrainData>(path);
            if (data == null)
            {
                data = new TerrainData();
                AssetDatabase.CreateAsset(data, path);
            }
            data.heightmapResolution = res;
            data.alphamapResolution = 32;
            data.baseMapResolution = 256;
            data.SetDetailResolution(32, 16);
            data.size = new Vector3(info.size, meta.top_height - meta.base_height, info.size);
            data.SetHeights(0, 0, heights);
            data.terrainLayers = new[] { layer };

            int holeRes = data.holesResolution;
            var solid = new bool[holeRes, holeRes];
            for (int z = 0; z < holeRes; z++)
            for (int x = 0; x < holeRes; x++)
            {
                bool inHole = cutHole && x > first && x < last - 1 && z > first && z < last - 1;
                solid[z, x] = !inHole;
            }
            data.SetHoles(0, 0, solid);
            EditorUtility.SetDirty(data);
            return data;
        }

        static Terrain CreateTerrain(GameObject parent, string name, TerrainData data, Meta meta, float size, float pixelError)
        {
            GameObject go = Terrain.CreateTerrainGameObject(data);
            go.name = name;
            go.transform.SetParent(parent.transform, false);
            go.transform.position = new Vector3(-size * 0.5f, meta.base_height, -size * 0.5f);
            // Occlusion only; lightmap/GI flags are decided in the lighting phase. No static batching (GPU Resident Drawer).
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
            var t = go.GetComponent<Terrain>();
            t.heightmapPixelError = pixelError;
            t.drawInstanced = true;
            t.shadowCastingMode = ShadowCastingMode.On;
            var pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline != null && pipeline.defaultTerrainMaterial != null) t.materialTemplate = pipeline.defaultTerrainMaterial;
            return t;
        }

        static void PlaceWalker(Terrain village)
        {
            var walker = Object.FindFirstObjectByType<Lockdown.LockdownWalker>();
            if (walker == null) return;
            Vector3 p = Vector3.zero;
            p.y = village.SampleHeight(p) + village.transform.position.y + 0.05f;
            walker.transform.SetPositionAndRotation(p, Quaternion.Euler(0f, 0f, 0f));
        }

        static void SetSun()
        {
            Light sun = RenderSettings.sun;
            if (sun == null)
                foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type == LightType.Directional) { sun = l; break; }
            if (sun == null) return;
            // The light points away from the sun: yaw = sun azimuth - 180.
            sun.transform.rotation = Quaternion.Euler(SunElevation, SunAzimuth - 180f, 0f);
        }

        // ---------- Review cameras ----------

        [MenuItem("Lockdown/Review/Build Ground Review Cameras")]
        public static void BuildReviewCameras()
        {
            Terrain village = GameObject.Find("_Ground/Terrain_Village")?.GetComponent<Terrain>();
            if (village == null) { Debug.LogError("[Lockdown] Build the real ground first."); return; }

            GameObject reviewRoot = GameObject.Find("_Review") ?? new GameObject("_Review");
            Transform old = reviewRoot.transform.Find("Cameras");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var cams = new GameObject("Cameras");
            cams.transform.SetParent(reviewRoot.transform, false);

            float Ground(float x, float z) => village.SampleHeight(new Vector3(x, 0f, z)) + village.transform.position.y;
            float eye = Ground(0f, 0f) + 1.6f;
            ReviewCamera(cams, "Cam_West_Horizon", new Vector3(0f, eye, 0f), Quaternion.Euler(-4f, SunAzimuth, 0f), 64f);
            ReviewCamera(cams, "Cam_North_Psiloritis", new Vector3(0f, eye, 0f), Quaternion.Euler(-8f, 330f, 0f), 64f);
            ReviewCamera(cams, "Cam_South_Messara", new Vector3(0f, eye, 0f), Quaternion.Euler(6f, 180f, 0f), 64f);
            ReviewCamera(cams, "Cam_Aerial", new Vector3(0f, Ground(0f, 0f) + 900f, -2000f), Quaternion.Euler(24f, 0f, 0f), 50f);
            ReviewCamera(cams, "Cam_TopDown", new Vector3(0f, Ground(0f, 0f) + 2000f, 0f), Quaternion.Euler(90f, 0f, 0f), 50f);

            foreach (var t in reviewRoot.GetComponentsInChildren<Transform>(true)) t.gameObject.tag = EditorOnly;
            EditorSceneManager.MarkSceneDirty(cams.scene);
            Debug.Log("[Lockdown] Ground review cameras built under _Review/Cameras.");
        }

        static void ReviewCamera(GameObject parent, string name, Vector3 pos, Quaternion rot, float fov)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.transform.SetPositionAndRotation(pos, rot);
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 30000f;
            cam.depth = -10f;
            cam.enabled = false; // rendered on demand for screenshots, never in the Game view
            var data = go.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        }

        // ---------- Satellite reference board ----------

        const string SatDir = "Assets/Lockdown/Textures/Review/Satellite";
        const string BoardMeshDir = "Assets/Lockdown/Models/Generated/Review";
        const string BoardMatDir = "Assets/Lockdown/Materials/Review";
        const float BoardSize = 1200f, BoardCell = 2f, BoardLift = 3f; // lifted so the LOD-simplified terrain never pokes through from far away

        // Drapes the aerial photo (Tools/terrain/lockdown_satellite.py) over the village terrain as four tile meshes.
        // Review-only: EditorOnly, textures and meshes are gitignored and regenerated by this menu item.
        [MenuItem("Lockdown/Review/Build Satellite Board")]
        public static void BuildSatelliteBoard()
        {
            Terrain village = GameObject.Find("_Ground/Terrain_Village")?.GetComponent<Terrain>();
            if (village == null) { Debug.LogError("[Lockdown] Build the real ground first."); return; }
            if (!File.Exists(Path.Combine(Application.dataPath, "..", SatDir, "sat_0_0.jpg")))
            {
                Debug.LogError("[Lockdown] Satellite tiles missing. Run: uv run --with rasterio --with numpy --with pillow Tools/terrain/lockdown_satellite.py");
                return;
            }
            EnsureFolder(BoardMeshDir);
            EnsureFolder(BoardMatDir);

            GameObject reviewRoot = GameObject.Find("_Review") ?? new GameObject("_Review");
            Transform old = reviewRoot.transform.Find("SatelliteBoard");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var board = new GameObject("SatelliteBoard");
            board.transform.SetParent(reviewRoot.transform, false);

            float half = BoardSize * 0.5f, tileSize = BoardSize * 0.5f;
            int cells = Mathf.RoundToInt(tileSize / BoardCell), n = cells + 1;
            float baseY = village.transform.position.y;
            for (int col = 0; col < 2; col++)
            for (int row = 0; row < 2; row++)
            {
                float x0 = -half + col * tileSize, z0 = -half + row * tileSize;
                var verts = new Vector3[n * n];
                var uvs = new Vector2[n * n];
                for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    float x = x0 + i * BoardCell, z = z0 + j * BoardCell;
                    verts[j * n + i] = new Vector3(x, village.SampleHeight(new Vector3(x, 0f, z)) + baseY + BoardLift, z);
                    uvs[j * n + i] = new Vector2(i / (float)cells, j / (float)cells);
                }
                var tris = new int[cells * cells * 6];
                int t = 0;
                for (int j = 0; j < cells; j++)
                for (int i = 0; i < cells; i++)
                {
                    int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                    tris[t++] = a; tris[t++] = c; tris[t++] = b;
                    tris[t++] = b; tris[t++] = c; tris[t++] = d;
                }
                var mesh = new Mesh { name = $"SatBoard_{col}_{row}", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.vertices = verts; mesh.uv = uvs; mesh.triangles = tris;
                mesh.RecalculateBounds();
                string meshPath = $"{BoardMeshDir}/SatBoard_{col}_{row}.asset";
                AssetDatabase.DeleteAsset(meshPath);
                AssetDatabase.CreateAsset(mesh, meshPath);

                var go = new GameObject($"Tile_{col}_{row}");
                go.transform.SetParent(board.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = GetBoardMaterial(col, row, false);
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            foreach (var tr in reviewRoot.GetComponentsInChildren<Transform>(true)) tr.gameObject.tag = EditorOnly;
            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(board.scene);
            Debug.Log("[Lockdown] Satellite board built under _Review/SatelliteBoard (1.2 km square, EditorOnly).");
        }

        [MenuItem("Lockdown/Review/Toggle Satellite Board")]
        public static void ToggleSatelliteBoard()
        {
            Transform b = GameObject.Find("_Review")?.transform.Find("SatelliteBoard");
            if (b == null) { Debug.LogError("[Lockdown] No satellite board in the scene."); return; }
            b.gameObject.SetActive(!b.gameObject.activeSelf);
            EditorSceneManager.MarkSceneDirty(b.gameObject.scene);
        }

        [MenuItem("Lockdown/Review/Toggle Satellite Contours")]
        public static void ToggleSatelliteContours()
        {
            Transform b = GameObject.Find("_Review")?.transform.Find("SatelliteBoard");
            if (b == null) { Debug.LogError("[Lockdown] No satellite board in the scene."); return; }
            var first = b.GetChild(0).GetComponent<MeshRenderer>().sharedMaterial;
            bool toContours = !first.mainTexture.name.StartsWith("sat_contours");
            foreach (Transform tile in b)
            {
                string[] p = tile.name.Split('_'); // Tile_<col>_<row>
                tile.GetComponent<MeshRenderer>().sharedMaterial = GetBoardMaterial(int.Parse(p[1]), int.Parse(p[2]), toContours);
            }
            EditorSceneManager.MarkSceneDirty(b.gameObject.scene);
        }

        static Material GetBoardMaterial(int col, int row, bool contours)
        {
            string name = (contours ? "sat_contours_" : "sat_") + col + "_" + row;
            string path = $"{BoardMatDir}/M_{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{SatDir}/{name}.jpg"));
            mat.SetColor("_BaseColor", Color.white);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // ---------- Helpers ----------

        static TerrainLayer GetOrCreateLayer(string path, Texture2D tex, float tile)
        {
            var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
            if (layer == null)
            {
                layer = new TerrainLayer();
                AssetDatabase.CreateAsset(layer, path);
            }
            layer.diffuseTexture = tex;
            layer.tileSize = new Vector2(tile, tile);
            layer.tileOffset = Vector2.zero;
            layer.smoothness = 0f;
            layer.metallic = 0f;
            EditorUtility.SetDirty(layer);
            return layer;
        }

        static Texture2D GetGridTexture()
        {
            // The 1 m / 10 m grid made for the Task 2 test area (Lockdown/Review/Build Walker Test Area).
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(GridTexPath);
            if (tex == null) throw new Exception("[Lockdown] " + GridTexPath + " missing: run Lockdown/Review/Build Walker Test Area once.");
            return tex;
        }

        // Plain grey matching the review grid's background, for the far backdrop until the real terrain layers (Phase 2).
        static Texture2D GetFlatTexture()
        {
            EnsureFolder(TexDir);
            var t = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            var pixels = new Color32[64];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(200, 200, 200, 255);
            t.SetPixels32(pixels);
            t.Apply();
            File.WriteAllBytes(FlatTexPath, t.EncodeToPNG());
            Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(FlatTexPath);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(FlatTexPath);
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
