using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Lockdown.EditorTools
{
    // Builds the Task 2 review kit: the walker prefab, the readability dummies and the
    // EditorOnly walker test area. Every menu item can be re-run; it replaces only its own output.
    public static class LockdownReviewBuilder
    {
        const string MatDir = "Assets/Lockdown/Materials/Review";
        const string TexDir = "Assets/Lockdown/Textures/Review";
        const string DummyDir = "Assets/Lockdown/Prefabs/Review";
        const string PlayerDir = "Assets/Lockdown/Prefabs/Player";
        const string WalkerPath = PlayerDir + "/Walker.prefab";
        const string GridTexPath = TexDir + "/T_ReviewGrid_diff.png";
        const string EditorOnly = "EditorOnly";

        static readonly (string name, Color color)[] DummyColors =
        {
            ("Dark", new Color32(0x2B, 0x2D, 0x2A, 0xFF)),
            ("Tan", new Color32(0x9C, 0x87, 0x62, 0xFF)),
            ("Olive", new Color32(0x56, 0x5A, 0x3C, 0xFF)),
        };

        // ---------- Walker ----------

        [MenuItem("Lockdown/Review/Build Walker Prefab")]
        public static void BuildWalkerPrefab()
        {
            EnsureFolder(PlayerDir);
            var root = new GameObject("Walker");
            var controller = root.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = 0.35f;
            controller.center = new Vector3(0f, 0.9f, 0f);

            var camGo = new GameObject("Camera");
            camGo.tag = "MainCamera";
            camGo.transform.SetParent(root.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 30000f; // the real backdrop reaches 20 km (Task 3)
            cam.fieldOfView = 64f;
            var camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = true;
            camData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            camData.antialiasingQuality = AntialiasingQuality.High;
            camGo.AddComponent<AudioListener>();

            var walker = root.AddComponent<Lockdown.LockdownWalker>();
            walker.viewCamera = cam;
            var fps = root.AddComponent<Lockdown.LockdownFpsCounter>();
            fps.walker = walker;

            PrefabUtility.SaveAsPrefabAsset(root, WalkerPath);
            Object.DestroyImmediate(root);
            Debug.Log($"[Lockdown] Walker prefab saved: {WalkerPath}");
        }

        // ---------- Readability dummies ----------

        [MenuItem("Lockdown/Review/Build Readability Dummies")]
        public static void BuildDummies()
        {
            EnsureFolder(DummyDir);
            EnsureFolder(MatDir);
            foreach (var (name, color) in DummyColors)
            {
                Material mat = GetOrCreateLit($"{MatDir}/M_Dummy_{name}.mat", color, 0.2f);
                SaveDummy(BuildStanding(mat), $"{DummyDir}/Dummy_{name}_Stand.prefab");
                SaveDummy(BuildCrouching(mat), $"{DummyDir}/Dummy_{name}_Crouch.prefab");
            }
            Debug.Log("[Lockdown] Readability dummies saved to " + DummyDir);
        }

        static GameObject BuildStanding(Material mat)
        {
            var root = new GameObject("Dummy");
            // Legs
            Part(root, PrimitiveType.Capsule, mat, new Vector3(-0.1f, 0.43f, 0f), Vector3.zero, new Vector3(0.16f, 0.43f, 0.16f));
            Part(root, PrimitiveType.Capsule, mat, new Vector3(0.1f, 0.43f, 0f), Vector3.zero, new Vector3(0.16f, 0.43f, 0.16f));
            // Torso (vest-like)
            Part(root, PrimitiveType.Capsule, mat, new Vector3(0f, 1.17f, 0f), Vector3.zero, new Vector3(0.42f, 0.33f, 0.27f));
            // Arms, slightly forward as if holding a rifle
            Part(root, PrimitiveType.Capsule, mat, new Vector3(-0.25f, 1.15f, 0.08f), new Vector3(-25f, 0f, 0f), new Vector3(0.11f, 0.32f, 0.11f));
            Part(root, PrimitiveType.Capsule, mat, new Vector3(0.25f, 1.15f, 0.08f), new Vector3(-25f, 0f, 0f), new Vector3(0.11f, 0.32f, 0.11f));
            // Head and helmet
            Part(root, PrimitiveType.Sphere, mat, new Vector3(0f, 1.62f, 0f), Vector3.zero, new Vector3(0.21f, 0.23f, 0.22f));
            Part(root, PrimitiveType.Sphere, mat, new Vector3(0f, 1.69f, -0.01f), Vector3.zero, new Vector3(0.27f, 0.22f, 0.29f));
            AddRootCollider(root, 1.8f);
            return root;
        }

        static GameObject BuildCrouching(Material mat)
        {
            var root = new GameObject("Dummy");
            // Left foot planted, knee up
            Part(root, PrimitiveType.Capsule, mat, new Vector3(-0.11f, 0.25f, 0.2f), Vector3.zero, new Vector3(0.16f, 0.25f, 0.16f));
            Part(root, PrimitiveType.Capsule, mat, new Vector3(-0.11f, 0.5f, 0.02f), new Vector3(90f, 0f, 0f), new Vector3(0.17f, 0.23f, 0.17f));
            // Right knee on the ground
            Part(root, PrimitiveType.Capsule, mat, new Vector3(0.11f, 0.08f, -0.22f), new Vector3(90f, 0f, 0f), new Vector3(0.15f, 0.24f, 0.15f));
            Part(root, PrimitiveType.Capsule, mat, new Vector3(0.11f, 0.33f, -0.02f), new Vector3(-10f, 0f, 0f), new Vector3(0.17f, 0.25f, 0.17f));
            // Torso leaning forward
            Part(root, PrimitiveType.Capsule, mat, new Vector3(0f, 0.8f, 0.02f), new Vector3(12f, 0f, 0f), new Vector3(0.42f, 0.28f, 0.27f));
            // Arms forward
            Part(root, PrimitiveType.Capsule, mat, new Vector3(-0.25f, 0.8f, 0.12f), new Vector3(-35f, 0f, 0f), new Vector3(0.11f, 0.3f, 0.11f));
            Part(root, PrimitiveType.Capsule, mat, new Vector3(0.25f, 0.8f, 0.12f), new Vector3(-35f, 0f, 0f), new Vector3(0.11f, 0.3f, 0.11f));
            // Head and helmet
            Part(root, PrimitiveType.Sphere, mat, new Vector3(0f, 1.2f, 0.08f), Vector3.zero, new Vector3(0.21f, 0.23f, 0.22f));
            Part(root, PrimitiveType.Sphere, mat, new Vector3(0f, 1.27f, 0.07f), Vector3.zero, new Vector3(0.27f, 0.22f, 0.29f));
            AddRootCollider(root, 1.38f);
            return root;
        }

        static void AddRootCollider(GameObject root, float height)
        {
            var col = root.AddComponent<CapsuleCollider>();
            col.height = height;
            col.radius = 0.3f;
            col.center = new Vector3(0f, height * 0.5f, 0f);
        }

        static void SaveDummy(GameObject root, string path)
        {
            root.name = Path.GetFileNameWithoutExtension(path);
            SetTagRecursive(root, EditorOnly);
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
        }

        // ---------- Walker test area ----------

        [MenuItem("Lockdown/Review/Build Walker Test Area")]
        public static void BuildTestArea()
        {
            EnsureFolder(MatDir);
            EnsureFolder(TexDir);
            if (AssetDatabase.LoadAssetAtPath<GameObject>($"{DummyDir}/Dummy_Dark_Stand.prefab") == null) BuildDummies();

            Texture2D grid = GetOrCreateGridTexture();
            Material groundMat = GetOrCreateLit($"{MatDir}/M_ReviewGround.mat", new Color(0.55f, 0.55f, 0.55f), 0.1f);
            groundMat.SetTexture("_BaseMap", grid);
            groundMat.SetTextureScale("_BaseMap", new Vector2(6f, 8f));
            EditorUtility.SetDirty(groundMat);
            Material greyMat = GetOrCreateLit($"{MatDir}/M_ReviewGrey.mat", new Color(0.62f, 0.62f, 0.6f), 0.1f);
            Material markerMat = GetOrCreateLit($"{MatDir}/M_ReviewMarker.mat", new Color(0.75f, 0.12f, 0.1f), 0.2f);

            GameObject reviewRoot = GameObject.Find("_Review") ?? new GameObject("_Review");
            reviewRoot.tag = EditorOnly;
            Transform old = reviewRoot.transform.Find("WalkerTestArea");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var area = new GameObject("WalkerTestArea");
            area.transform.SetParent(reviewRoot.transform, false);

            // Ground: 60 m (x) by 80 m (z), top at y = 0. The walker starts at z = -25 facing +z.
            Box(area, "Ground", groundMat, new Vector3(0f, -0.1f, 10f), new Vector3(60f, 0.2f, 80f));

            // Distance markers from the start line, for sprint timing and engagement ranges.
            var markers = Group(area, "DistanceMarkers");
            Box(markers, "StartLine", markerMat, new Vector3(0f, 0.01f, -25f), new Vector3(14f, 0.02f, 0.15f));
            foreach (int d in new[] { 10, 20, 30, 50, 70 })
            {
                float z = -25f + d;
                Box(markers, $"Line_{d}m", markerMat, new Vector3(0f, 0.01f, z), new Vector3(14f, 0.02f, 0.15f));
                Box(markers, $"Post_{d}m", markerMat, new Vector3(7.2f, 1f, z), new Vector3(0.12f, 2f, 0.12f));
                Label(markers, $"{d} m", new Vector3(7.2f, 2.4f, z), 1f);
            }

            // Cover heights (BO2 metrics: waist cover 1.0-1.1 m, full cover >= 1.9 m).
            var cover = Group(area, "Cover");
            CoverWall(cover, greyMat, 1.0f, new Vector3(-12f, 0f, -18f));
            CoverWall(cover, greyMat, 1.1f, new Vector3(-17f, 0f, -18f));
            CoverWall(cover, greyMat, 1.9f, new Vector3(-22f, 0f, -18f));

            // Doorway 1.3 x 2.3 m in a 3.5 m wall.
            var door = Group(area, "Doorway");
            Box(door, "Wall_L", greyMat, new Vector3(-24.65f, 1.75f, -8f), new Vector3(2.7f, 3.5f, 0.3f));
            Box(door, "Wall_R", greyMat, new Vector3(-19.35f, 1.75f, -8f), new Vector3(2.7f, 3.5f, 0.3f));
            Box(door, "Lintel", greyMat, new Vector3(-22f, 2.9f, -8f), new Vector3(1.3f, 1.2f, 0.3f));
            Label(door, "door 1.3 x 2.3 m", new Vector3(-22f, 3.8f, -8.2f), 0.7f);

            // Alleys 2.5 m and 4 m wide, 12 m long, 4 m walls.
            var alleys = Group(area, "Alleys");
            Alley(alleys, greyMat, "Alley_2.5m", -24.75f, 2.5f);
            Alley(alleys, greyMat, "Alley_4m", -17.5f, 4f);

            // Stairs (30 and 35 degrees) and a 35 degree ramp, each up to a 3 m platform.
            var climb = Group(area, "StairsAndRamps");
            Stairs(climb, greyMat, "Stairs_35", -25f, 35f);
            Stairs(climb, greyMat, "Stairs_30", -19f, 30f);
            Ramp(climb, greyMat, "Ramp_35", -13f, 35f);

            // Two-storey block with a flat roof (6.4 m), sun from the west so its shade falls east.
            var block = Group(area, "TwoStoreyBlock");
            Box(block, "Block", greyMat, new Vector3(12f, 3.2f, 5f), new Vector3(8f, 6.4f, 10f));
            Label(block, "2 storeys, 6.4 m", new Vector3(12f, 7f, -0.2f), 1f);

            // Readability dummies: every colour and pose at 10 m and 30 m, plus a group in the block's shade.
            var dummies = Group(area, "Dummies");
            float[] xs = { -5f, -3f, -1f, 1f, 3f, 5f };
            foreach (float z in new[] { -15f, 5f })
            {
                int i = 0;
                foreach (var (name, _) in DummyColors)
                {
                    PlaceDummy(dummies, name, "Stand", new Vector3(xs[i++], 0f, z));
                    PlaceDummy(dummies, name, "Crouch", new Vector3(xs[i++], 0f, z));
                }
            }
            PlaceDummy(dummies, "Dark", "Stand", new Vector3(20.5f, 0f, 1f));
            PlaceDummy(dummies, "Tan", "Crouch", new Vector3(23.5f, 0f, 2f));
            PlaceDummy(dummies, "Olive", "Stand", new Vector3(26.5f, 0f, 3f));

            SetTagRecursive(reviewRoot, EditorOnly);
            EditorSceneManager.MarkSceneDirty(area.scene);
            Debug.Log("[Lockdown] Walker test area built under _Review/WalkerTestArea.");
        }

        static void CoverWall(GameObject parent, Material mat, float height, Vector3 basePos)
        {
            Box(parent, $"Cover_{height:0.0}m", mat, basePos + new Vector3(0f, height * 0.5f, 0f), new Vector3(3f, height, 0.3f));
            Label(parent, $"{height:0.0} m", basePos + new Vector3(0f, height + 0.4f, -0.2f), 0.6f);
        }

        static void Alley(GameObject parent, Material mat, string name, float centerX, float width)
        {
            var g = Group(parent, name);
            float offset = width * 0.5f + 0.15f;
            Box(g, "Wall_W", mat, new Vector3(centerX - offset, 2f, 6f), new Vector3(0.3f, 4f, 12f));
            Box(g, "Wall_E", mat, new Vector3(centerX + offset, 2f, 6f), new Vector3(0.3f, 4f, 12f));
            Label(g, $"alley {width:0.#} m", new Vector3(centerX, 4.5f, -0.2f), 0.7f);
        }

        static void Stairs(GameObject parent, Material mat, string name, float x, float angle)
        {
            var g = Group(parent, name);
            const float height = 3f, rise = 0.2f, width = 2f, zStart = 20f;
            float tread = rise / Mathf.Tan(angle * Mathf.Deg2Rad);
            int steps = Mathf.RoundToInt(height / rise);
            for (int i = 0; i < steps; i++)
            {
                float h = rise * (i + 1);
                Box(g, $"Step_{i:00}", mat, new Vector3(x, h * 0.5f, zStart + tread * (i + 0.5f)), new Vector3(width, h, tread));
            }
            float end = zStart + tread * steps;
            Box(g, "Platform", mat, new Vector3(x, height * 0.5f, end + 2f), new Vector3(width + 1f, height, 4f));
            Label(g, $"stairs {angle:0}°", new Vector3(x, 1f, zStart - 0.5f), 0.6f);
        }

        static void Ramp(GameObject parent, Material mat, string name, float x, float angle)
        {
            var g = Group(parent, name);
            const float height = 3f, width = 2f, zStart = 20f, thickness = 0.3f;
            float run = height / Mathf.Tan(angle * Mathf.Deg2Rad);
            float length = height / Mathf.Sin(angle * Mathf.Deg2Rad);
            // A slab whose top surface goes from (zStart, 0) to (zStart + run, height).
            var slab = Box(g, "Slab", mat, Vector3.zero, new Vector3(width, thickness, length));
            slab.transform.localRotation = Quaternion.Euler(-angle, 0f, 0f);
            Vector3 topMid = new Vector3(x, height * 0.5f, zStart + run * 0.5f);
            slab.transform.localPosition = topMid - slab.transform.up * (thickness * 0.5f);
            Box(g, "Platform", mat, new Vector3(x, height * 0.5f, zStart + run + 2f), new Vector3(width + 1f, height, 4f));
            Label(g, $"ramp {angle:0}°", new Vector3(x, 1f, zStart - 0.5f), 0.6f);
        }

        static void PlaceDummy(GameObject parent, string color, string pose, Vector3 pos)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{DummyDir}/Dummy_{color}_{pose}.prefab");
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent.scene);
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f); // facing the start line
        }

        // ---------- Helpers ----------

        static GameObject Group(GameObject parent, string name)
        {
            var g = new GameObject(name);
            g.transform.SetParent(parent.transform, false);
            return g;
        }

        static GameObject Box(GameObject parent, string name, Material mat, Vector3 pos, Vector3 size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = pos;
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        static void Part(GameObject parent, PrimitiveType type, Material mat, Vector3 pos, Vector3 euler, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = type.ToString();
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        static void Label(GameObject parent, string text, Vector3 pos, float size)
        {
            var go = new GameObject("Label_" + text);
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.identity;
            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            tm.fontSize = 64;
            tm.characterSize = size * 0.1f;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.color = Color.white;
            go.GetComponent<MeshRenderer>().sharedMaterial = tm.font.material;
        }

        static Material GetOrCreateLit(string path, Color color, float smoothness)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // 1024 px = 10 m: thin lines every metre, a strong line every 10 m.
        static Texture2D GetOrCreateGridTexture()
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(GridTexPath);
            if (tex != null) return tex;
            const int size = 1024;
            var pixels = new Color32[size * size];
            var bg = new Color32(200, 200, 200, 255);
            var minor = new Color32(150, 150, 150, 255);
            var major = new Color32(70, 70, 70, 255);
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Color32 c = bg;
                if (IsLine(x, 6) || IsLine(y, 6)) c = major;
                else if (IsMinor(x) || IsMinor(y)) c = minor;
                pixels[y * size + x] = c;
            }
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
            t.SetPixels32(pixels);
            t.Apply();
            File.WriteAllBytes(GridTexPath, t.EncodeToPNG());
            Object.DestroyImmediate(t);
            AssetDatabase.ImportAsset(GridTexPath);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(GridTexPath);

            bool IsLine(int p, int width) => p < width / 2 || p >= size - width / 2;
            bool IsMinor(int p)
            {
                float m = p / 102.4f;
                float frac = m - Mathf.Round(m);
                return Mathf.Abs(frac) * 102.4f < 1.5f;
            }
        }

        static void SetTagRecursive(GameObject go, string tag)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.tag = tag;
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
