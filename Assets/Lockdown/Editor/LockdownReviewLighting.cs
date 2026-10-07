using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using static Lockdown.EditorTools.LockdownEditorUtil;

namespace Lockdown.EditorTools
{
    // The temporary review lighting (Task 5), kept in every review until Phase 6 replaces it: the real sun for
    // early September ~18:00, a warm 4500 K sun, a blue procedural sky as the ambient source, light warm haze, and a
    // rough Black Ops 2 grade. One prefab (sun + global volume) plus the per-scene render settings, which a prefab
    // can't hold (skybox, ambient, fog, lighting settings asset): `Apply` sets both in the open scene.
    public static class LockdownReviewLighting
    {
        // 5 Sept 2025, 18:00 EEST (15:00 UTC) at the village origin, 35.1503 N, 24.8477 E (standard solar-position
        // formula): 19.9 degrees up, azimuth 263.9 (a few degrees south of due west). Task 3: no ridge hides it.
        public const float SunAzimuth = 263.9f, SunElevation = 19.9f;
        public const float SunKelvin = 4500f;
        // Brightness tuned on the test corner so the Phase 6 ratios hold before grading (Tools/review/luminance.py):
        // sunlit / shaded ground 4-6x, sunlit / shaded wall ~10x, sunlit whitewash below clipping.
        public static float SunIntensity = 3.0f;
        public static float SkyExposure = 1.2f;
        // Sky fill. Unity applies this multiplier in gamma space (the fill scales as value^2.2): 1.25 = 1.6x the sky.
        public static float AmbientIntensity = 1.4f;
        // Warm bounce off sunlit dry ground, limestone and pale plaster: what fills alleys and rooms (brief Phase 6).
        public static float BounceIntensity = 2.5f, AlbedoBoost = 2.0f; // warm fill in alleys and rooms, heightened (BO2); at 1.5/1.0 the rooms were black

        public const string PrefabPath = "Assets/Lockdown/Prefabs/Review/ReviewLighting.prefab";
        public const string GradePath = "Assets/Lockdown/Settings/Review/VP_Review_BO2.asset";
        public const string MeasurePath = "Assets/Lockdown/Settings/Review/VP_Review_Measure.asset";
        public const string SkyPath = "Assets/Lockdown/Materials/Review/M_ReviewSky.mat";
        public const string LightingPath = "Assets/Lockdown/Settings/Review/LS_Review.lighting";
        public const string LightingLmPath = "Assets/Lockdown/Settings/Review/LS_Review_Lightmaps.lighting";
        public const string FlarePath = "Assets/Lockdown/Settings/Review/LF_ReviewSun.asset";

        // Rough BO2 grade (brief Phase 7 starting values); refined in Phase 7.
        public static TonemappingMode Tonemap = TonemappingMode.ACES;
        public static float PostExposure = -0.4f, Contrast = 12f, Saturation = 12f, Temperature = 5f;
        public static Color SplitShadows = new(0.48f, 0.54f, 0.56f), SplitHighlights = new(0.64f, 0.58f, 0.5f); // teal kept light: the sky already makes shade blue
        public static float SplitBalance = 0f, ShadowLift = 0.1f; // ACES toe crushes shade otherwise (Task 5 A/B: ACES + lift 0.1 beat Neutral and 0.2+)
        public static float BloomThreshold = 1.0f, BloomIntensity = 0.4f, BloomScatter = 0.7f, BloomClamp = 6f, VignetteIntensity = 0.2f;
        public static float ProbeNoise = 0.1f, ProbeNormalBias = 0.33f;

        // Late-summer dusty air: a light warm haze (directional, warmer toward the sun, comes in Phase 6).
        public static Color FogColor = new(0.78f, 0.75f, 0.68f);
        public static float FogDensity = 0.00005f; // exponential: ~50 % at Psiloritis' 11 km, almost nothing in the streets

        [MenuItem("Lockdown/Review/Apply Review Lighting")]
        public static void ApplyMenu()
        {
            if (EditorApplication.isPlaying) { Debug.LogError("[Lockdown] Exit Play mode first."); return; }
            Apply(lightmaps: false);
            EditorSceneManager.SaveOpenScenes();
        }

        public static GameObject Apply(bool lightmaps)
        {
            BuildAssets();
            var scene = EditorSceneManager.GetActiveScene();

            // One sun only: remove any other directional light (Task 2-4 approximations) and old review volumes.
            GameObject rig = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == "ReviewLighting" && PrefabUtility.IsPartOfPrefabInstance(root)) { rig = root; continue; }
                if (root.name == "ReviewVolume") { Object.DestroyImmediate(root); continue; }
                foreach (var l in root.GetComponentsInChildren<Light>(true))
                    if (l.type == LightType.Directional) Object.DestroyImmediate(l.gameObject); // the old suns are standalone objects
            }
            if (rig == null)
                rig = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath), scene);
            Light sun = rig.GetComponentInChildren<Light>();

            RenderSettings.sun = sun;
            RenderSettings.skybox = AssetDatabase.LoadAssetAtPath<Material>(SkyPath);
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = AmbientIntensity;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity = 1f;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = FogDensity;
            RenderSettings.fogColor = FogColor;
            Lightmapping.lightingSettings = AssetDatabase.LoadAssetAtPath<LightingSettings>(lightmaps ? LightingLmPath : LightingPath);
            DynamicGI.UpdateEnvironment();
            EditorSceneManager.MarkSceneDirty(scene);
            return rig;
        }

        public static void BuildAssets()
        {
            EnsureFolder("Assets/Lockdown/Settings/Review");
            EnsureFolder("Assets/Lockdown/Materials/Review");
            BuildGrade();
            BuildMeasureProfile();
            BuildSky();
            BuildFlare();
            BuildLightingSettings(LightingPath, lightmaps: false);
            BuildLightingSettings(LightingLmPath, lightmaps: true);
            BuildPrefab();
            AssetDatabase.SaveAssets();
        }

        // Swaps the review volume between the BO2 grade and the measurement profile (no grade, no tonemapping,
        // exposure pulled down so nothing clips), for the brightness-ratio check. Not saved with the scene.
        public static void SetMeasureMode(bool measure)
        {
            foreach (var v in Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
                if (v.name == "Volume" && v.transform.parent != null && v.transform.parent.name == "ReviewLighting")
                    v.sharedProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(measure ? MeasurePath : GradePath);
        }

        public const float MeasureExposure = -2f;

        static void BuildGrade()
        {
            var p = GetProfile(GradePath);
            Get<Tonemapping>(p).mode.Override(Tonemap);
            var ca = Get<ColorAdjustments>(p);
            ca.postExposure.Override(PostExposure);
            ca.contrast.Override(Contrast);
            ca.saturation.Override(Saturation);
            Get<WhiteBalance>(p).temperature.Override(Temperature);
            var st = Get<SplitToning>(p); // warm highlights, teal shadows: pushes what the warm sun and blue sky give
            st.shadows.Override(SplitShadows);
            st.highlights.Override(SplitHighlights);
            st.balance.Override(SplitBalance);
            var smh = Get<ShadowsMidtonesHighlights>(p); // shade lifted so it keeps its texture and players stay visible
            smh.shadows.Override(new Vector4(1f, 1f, 1f, ShadowLift));
            smh.midtones.Override(new Vector4(1f, 1f, 1f, 0.03f));
            var bloom = Get<Bloom>(p);
            bloom.threshold.Override(BloomThreshold);
            bloom.intensity.Override(BloomIntensity);
            bloom.scatter.Override(BloomScatter);
            bloom.highQualityFiltering.Override(true);
            bloom.clamp.Override(BloomClamp); // unclamped, one hot glint pixel on glass bloomed into a white disc
            var apv = Get<ProbeVolumesOptions>(p);
            apv.samplingNoise.Override(ProbeNoise);
            apv.animateSamplingNoise.Override(false); // no TAA to resolve it: animated noise only crawls
            apv.normalBias.Override(ProbeNormalBias);
            apv.leakReductionMode.Override(UnityEngine.Rendering.APVLeakReductionMode.Quality); // Performance (project default) fails on 0.2-0.3 m walls and slabs
            var vig = Get<Vignette>(p);
            vig.intensity.Override(VignetteIntensity);
            vig.smoothness.Override(0.45f);
#pragma warning disable CS0618 // still the only way to make volumes drop their cached values of an edited profile
            p.isDirty = true;
#pragma warning restore CS0618
            EditorUtility.SetDirty(p);
        }

        static void BuildMeasureProfile()
        {
            var p = GetProfile(MeasurePath);
            Get<Tonemapping>(p).mode.Override(TonemappingMode.None);
            Get<ColorAdjustments>(p).postExposure.Override(MeasureExposure);
            Get<Bloom>(p).intensity.Override(0f);
#pragma warning disable CS0618
            p.isDirty = true;
#pragma warning restore CS0618
            EditorUtility.SetDirty(p);
        }

        // Deep clean blue overhead, pale warm yellow-white around the low western sun (brief Phase 6, "Sky and air").
        static void BuildSky()
        {
            var sky = AssetDatabase.LoadAssetAtPath<Material>(SkyPath);
            if (sky == null)
            {
                sky = new Material(Shader.Find("Skybox/Procedural"));
                AssetDatabase.CreateAsset(sky, SkyPath);
            }
            // The sky keeps its sun disc and warm halo: tested on the corner, baking with and without the disc gives the
            // same shade (Task 5 sweep), so the sun is not counted twice. The flare adds the glow and the ghosts.
            sky.SetFloat("_SunDisk", 2f);
            sky.SetFloat("_SunSize", 0.025f);
            sky.SetFloat("_SunSizeConvergence", 6f);
            // Late-summer dust: a paler, less saturated sky than a clean blue tint gives, so shade comes out light grey-blue
            // as in the photos and BO2 (Task 5 boards), not navy.
            sky.SetFloat("_AtmosphereThickness", 0.95f);
            sky.SetColor("_SkyTint", new Color(0.48f, 0.53f, 0.6f));
            sky.SetColor("_GroundColor", new Color(0.42f, 0.38f, 0.33f)); // sunlit dry ground and limestone below the horizon
            sky.SetFloat("_Exposure", SkyExposure);
            EditorUtility.SetDirty(sky);
        }

        public static float FlareGlow = 3.5f, FlareGlowIntensity = 0.3f, FlareGhostIntensity = 0.06f;

        static void BuildFlare()
        {
            var data = AssetDatabase.LoadAssetAtPath<LensFlareDataSRP>(FlarePath);
            if (data == null) { data = ScriptableObject.CreateInstance<LensFlareDataSRP>(); AssetDatabase.CreateAsset(data, FlarePath); }
            LensFlareDataElementSRP El(float pos, float scale, float intensity, Color tint, float fallOff, float edge)
            {
                var e = new LensFlareDataElementSRP
                {
                    flareType = SRPLensFlareType.Circle, position = pos, uniformScale = scale, localIntensity = intensity,
                    tint = tint, blendMode = SRPLensFlareBlendMode.Additive, modulateByLightColor = true,
                };
                e.fallOff = fallOff;
                e.edgeOffset = edge;
                return e;
            }
            data.elements = new[]
            {
                El(0f, FlareGlow, FlareGlowIntensity, new Color(1f, 0.92f, 0.8f), 0.15f, 1f),    // wide warm glow round it
                El(0.55f, 1.4f, FlareGhostIntensity, new Color(1f, 0.85f, 0.6f), 0.4f, 0.9f),    // two faint ghosts toward the centre
                El(1.25f, 2.6f, FlareGhostIntensity * 0.6f, new Color(0.7f, 0.85f, 1f), 0.3f, 0.95f),
            };
            EditorUtility.SetDirty(data);
        }

        // Baked indirect (realtime sun shadows near the player, bounce baked once: the sun never moves). The same
        // settings drive both GI paths compared in Task 5; the lightmap variant adds the lightmap resolution.
        public enum BakeQuality { Preview, Medium, Full }
        public static BakeQuality Quality = BakeQuality.Medium; // preview's 128 indirect samples left blotches on the ceilings

        static void BuildLightingSettings(string path, bool lightmaps)
        {
            var ls = AssetDatabase.LoadAssetAtPath<LightingSettings>(path);
            if (ls == null)
            {
                ls = new LightingSettings { name = System.IO.Path.GetFileNameWithoutExtension(path) };
                AssetDatabase.CreateAsset(ls, path);
            }
            ls.bakedGI = true;
            ls.realtimeGI = false;
            ls.mixedBakeMode = MixedLightingMode.IndirectOnly;
            ls.lightmapper = LightingSettings.Lightmapper.ProgressiveGPU;
            int q = (int)Quality;
            ls.directSampleCount = new[] { 16, 32, 64 }[q];
            ls.indirectSampleCount = new[] { 128, 512, 1024 }[q];
            ls.environmentSampleCount = new[] { 128, 256, 512 }[q];
            ls.maxBounces = 4; // alleys and rooms are lit almost only by bounce
            ls.albedoBoost = AlbedoBoost;
            ls.lightmapResolution = lightmaps ? (Quality == BakeQuality.Full ? 16f : 8f) : 4f;
            ls.lightmapMaxSize = 2048;
            ls.lightmapPadding = 4;
            ls.ao = false; // contact darkening comes from SSAO (small radius)
            ls.filteringMode = LightingSettings.FilterMode.Auto;
            EditorUtility.SetDirty(ls);
        }

        static void BuildPrefab()
        {
            EnsureFolder("Assets/Lockdown/Prefabs/Review");
            var root = new GameObject("ReviewLighting");
            var sunGo = new GameObject("Sun");
            sunGo.transform.SetParent(root.transform, false);
            // The light points away from the sun: pitch = elevation, yaw = azimuth - 180.
            sunGo.transform.localRotation = Quaternion.Euler(SunElevation, SunAzimuth - 180f, 0f);
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.lightmapBakeType = LightmapBakeType.Mixed;
            sun.useColorTemperature = true;
            sun.colorTemperature = SunKelvin;
            sun.color = Color.white;
            sun.intensity = SunIntensity;
            sun.bounceIntensity = BounceIntensity;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 1f;
            var data = sunGo.AddComponent<UniversalAdditionalLightData>();
            data.softShadowQuality = SoftShadowQuality.Low; // crisp edges: the sun is a small, hard source

            // The visible sun: the sky has no disc (it would be baked twice), so the disc, its glow and a faint BO2
            // flare are drawn here, hidden by depth when a building or the terrain covers the sun.
            var flare = sunGo.AddComponent<LensFlareComponentSRP>();
            flare.lensFlareData = AssetDatabase.LoadAssetAtPath<LensFlareDataSRP>(FlarePath);
            flare.intensity = 1f;
            flare.useOcclusion = true;
            flare.occlusionRadius = 0.1f;
            flare.sampleCount = 32;
            flare.allowOffScreen = false;
            flare.environmentOcclusion = false;

            var volGo = new GameObject("Volume");
            volGo.transform.SetParent(root.transform, false);
            var vol = volGo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.sharedProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(GradePath);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
        }

        static VolumeProfile GetProfile(string path)
        {
            var p = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (p != null) return p;
            p = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(p, path);
            return p;
        }

        static T Get<T>(VolumeProfile p) where T : VolumeComponent
        {
            if (p.TryGet<T>(out var c)) return c;
            c = p.Add<T>(false);
            c.name = typeof(T).Name;
            AssetDatabase.AddObjectToAsset(c, p);
            return c;
        }
    }
}
