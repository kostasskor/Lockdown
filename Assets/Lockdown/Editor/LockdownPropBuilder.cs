using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lockdown.EditorTools
{
    // Game-ready prop prefabs (project prop rule): the simplified LOD0/LOD1 glTFs written by
    // Tools/assets/lockdown_props.py (from the Poly Haven CC0 scans) become one prefab per prop with a LODGroup:
    // LOD0 up close, LOD1 from its measured switch distance (LockdownHouseReview.CaptureLodPairs), culled from about 25 m.
    // The switch is invisible: LOD1 keeps the silhouette and the two dither-cross-fade. glTFast's own shader graph can't
    // cross-fade, so every material is copied into Materials/Props on LockdownProp_glTFPbr (the same graph with LOD
    // cross-fade on: same properties, same look). LOD1 shares LOD0's materials and casts shadows too. Idempotent.
    public static class LockdownPropBuilder
    {
        const string ModelDir = "Assets/Lockdown/Models/Generated/Props";
        const string PrefabDir = "Assets/Lockdown/Prefabs/Props";
        const string MaterialDir = "Assets/Lockdown/Materials/Props";
        const string ShaderPath = "Assets/Lockdown/Shaders/LockdownProp_glTFPbr.shadergraph";
        public static readonly string[] Props = { "potted_plant_01", "potted_plant_02", "Barrel_02", "cardboard_box_01" };
        // Where LOD1 takes over (metres). Same values as LOD1_DISTANCE in lockdown_props.py, which simplifies LOD1 to a
        // one-pixel shape error at this distance; confirmed by the LockdownHouseReview.CaptureLodPair sweep.
        static readonly Dictionary<string, float> Lod1Distance = new Dictionary<string, float>
        {
            { "potted_plant_01", 12f },
            { "potted_plant_02", 12f },
            { "Barrel_02", 12f },
            { "cardboard_box_01", 12f },
        };
        const float CullDistance = 25f, PlayerFov = 64f, FadeStart = 0.85f;

        [MenuItem("Lockdown/Build/Props")]
        public static void BuildAll()
        {
            if (!AssetDatabase.IsValidFolder(PrefabDir)) AssetDatabase.CreateFolder("Assets/Lockdown/Prefabs", "Props");
            if (!AssetDatabase.IsValidFolder(MaterialDir)) AssetDatabase.CreateFolder("Assets/Lockdown/Materials", "Props");
            var report = new List<string>();
            // Approved props are frozen (project rule: approved generated content is never rebuilt): only props without a prefab yet are built. To rebuild one,
            // delete its prefab deliberately first.
            foreach (var name in Props)
                if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath(name)) == null) report.Add(Build(name));
            AssetDatabase.SaveAssets();
            Debug.Log("[Lockdown] Props built (verts LOD0 / LOD1):\n  " + string.Join("\n  ", report));
        }

        public static string PrefabPath(string name) => $"{PrefabDir}/{name}.prefab";

        static string Build(string name)
        {
            var lod0Src = AssetDatabase.LoadAssetAtPath<GameObject>($"{ModelDir}/{name}/{name}_LOD0.gltf");
            var lod1Src = AssetDatabase.LoadAssetAtPath<GameObject>($"{ModelDir}/{name}/{name}_LOD1.gltf");
            if (lod0Src == null || lod1Src == null) return $"{name}: MISSING glTF (run Tools/assets/lockdown_props.py)";

            var root = new GameObject(name);
            var mats = new Dictionary<string, Material>();
            var r0 = Copy(lod0Src, root, "LOD0", mats, out int v0);
            var r1 = Copy(lod1Src, root, "LOD1", mats, out int v1);

            var group = root.AddComponent<LODGroup>();
            group.SetLODs(new[] { new LOD(0.5f, r0), new LOD(0.1f, r1) });
            group.RecalculateBounds(); // only measures the renderers already in its LODs
            // Screen-relative height at a distance for the player's FOV: size / (2 * distance * tan(fov / 2)).
            float k = 2f * Mathf.Tan(PlayerFov * 0.5f * Mathf.Deg2Rad);
            float Height(float distance) => group.size / (distance * k);
            float h0 = Height(Lod1Distance[name]), h1 = Height(CullDistance);
            // The cross-fade runs over the last stretch before each switch (FadeStart of its distance onwards).
            // fadeTransitionWidth is a fraction of the LOD's own screen-height band, hence the conversion.
            group.SetLODs(new[]
            {
                new LOD(h0, r0) { fadeTransitionWidth = (Height(Lod1Distance[name] * FadeStart) - h0) / (1f - h0) },
                new LOD(h1, r1) { fadeTransitionWidth = (Height(CullDistance * FadeStart) - h1) / (h0 - h1) },
            });
            group.fadeMode = LODFadeMode.CrossFade;
            group.animateCrossFading = false;
            var flags = StaticEditorFlags.ContributeGI | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic;
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath(name));
            Object.DestroyImmediate(root);
            return $"{name}: {v0} / {v1}";
        }

        // Every mesh node of an imported glTF, under one LOD parent, with its transform relative to the glTF root.
        static Renderer[] Copy(GameObject src, GameObject root, string label, Dictionary<string, Material> mats, out int verts)
        {
            var parent = new GameObject(label);
            parent.transform.SetParent(root.transform, false);
            var list = new List<Renderer>();
            verts = 0;
            foreach (var mf in src.GetComponentsInChildren<MeshFilter>(true))
            {
                var go = new GameObject(mf.name);
                go.transform.SetParent(parent.transform, false);
                Matrix4x4 m = src.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                go.transform.SetLocalPositionAndRotation(m.GetPosition(), m.rotation);
                go.transform.localScale = m.lossyScale;
                go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var mr = go.AddComponent<MeshRenderer>();
                var srcMats = mf.GetComponent<MeshRenderer>().sharedMaterials;
                mr.sharedMaterials = srcMats.Select(mat =>
                {
                    if (mat == null) return null;
                    if (!mats.TryGetValue(mat.name, out var shared)) mats[mat.name] = shared = CrossFadeMaterial(mat);
                    return shared;
                }).ToArray();
                mr.shadowCastingMode = ShadowCastingMode.On; // a LOD without shadows pops: the plant's shadow and self-shadow vanish
                verts += mf.sharedMesh.vertexCount;
                list.Add(mr);
            }
            return list.ToArray();
        }

        // A copy of a glTFast material on the cross-fading graph: same properties, keywords and queue.
        static Material CrossFadeMaterial(Material src)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            string path = $"{MaterialDir}/{src.name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, path); }
            mat.shader = shader;
            mat.CopyPropertiesFromMaterial(src);
            mat.shaderKeywords = src.shaderKeywords;
            mat.renderQueue = src.renderQueue;
            mat.doubleSidedGI = src.doubleSidedGI;
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
