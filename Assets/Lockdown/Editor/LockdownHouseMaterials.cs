using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Lockdown.EditorTools
{
    // Builds the house materials from the downloaded CC0 sets (Tools/assets/lockdown_fetch.py).
    // Each material's tint is solved from a target albedo (sRGB, sampled from evenly lit areas of the
    // reference photos and pulled into realistic ranges, brief Phase 3), so the result is right whatever
    // the source scan's brightness. Re-running rebuilds the same assets in place.
    public static class LockdownHouseMaterials
    {
        const string PH = "Assets/Lockdown/ThirdParty/PolyHaven";
        const string Decals = "Assets/Lockdown/ThirdParty/AmbientCG/Decals";
        const string MatDir = "Assets/Lockdown/Materials/Houses";
        const float MaxTint = 1.6f;
        const string RendererPath = "Assets/Lockdown/Settings/Lockdown_URP_Renderer.asset";

        public class Def
        {
            public string set;          // texture set folder under ThirdParty/PolyHaven
            public Color32 target;      // target mean albedo, sRGB
            public float tileMetres;    // world size of one texture repeat
            public float smoothScale = 1f;
            public float normalScale = 1f;
            public string reliefSet;    // optional: take normal + MAOS from another set (colour from `set`)
            public float desaturate;    // layered materials only: grey the scan first (sun-bleached wood)
        }

        // Albedo targets: whitewash ~205-215 sRGB (never 255), pale stone mid-bright, dark wood and black paint dark.
        public static readonly Dictionary<string, Def> Defs = new()
        {
            { "Render", new Def { set = "Textures/painted_plaster_wall", target = new Color32(210, 208, 200, 255), tileMetres = 2.6f } },
            { "RenderOld", new Def { set = "Textures/painted_plaster_wall", reliefSet = "Textures/white_rough_plaster", target = new Color32(200, 197, 188, 255), tileMetres = 2.4f, normalScale = 1.2f } }, // lumpy whitewash; peeling comes from the Peel decal
            { "Concrete", new Def { set = "Textures/concrete_wall_009", target = new Color32(150, 146, 137, 255), tileMetres = 2.5f } },
            { "PaintBlue", new Def { set = "Textures/painted_plaster_wall", target = new Color32(52, 104, 158, 255), tileMetres = 2.6f, smoothScale = 1.2f } },
            { "Stone", new Def { set = "Textures/concrete_wall_009", target = new Color32(172, 160, 138, 255), tileMetres = 1.2f, normalScale = 1.6f } }, // dressed stone: a joint-free scan, so no fake mortar lines run through a block
            { "Wood", new Def { set = "Props/weathered_brown_planks", target = new Color32(92, 58, 38, 255), tileMetres = 1.4f } },
            { "Tile", new Def { set = "Textures/stone_tiles_02", target = new Color32(150, 146, 136, 255), tileMetres = 1.0f, smoothScale = 0.8f } }, // warm light-grey slabs (03)
            { "Metal", new Def { set = "Props/rusty_metal_02", target = new Color32(52, 50, 47, 255), tileMetres = 0.8f, smoothScale = 0.8f } },
            // Weathering layers (hand-built imperfections): sun-bleached grey wood, rust under the railing paint.
            { "WoodBleached", new Def { set = "Props/weathered_brown_planks", target = new Color32(132, 128, 120, 255), tileMetres = 1.4f, desaturate = 0.85f } },
            { "Rust", new Def { set = "Props/rusty_metal_02", target = new Color32(104, 62, 40, 255), tileMetres = 0.5f, smoothScale = 0.6f } },
            { "FrameWhite", new Def { set = "Props/white_planks_clean", target = new Color32(212, 212, 206, 255), tileMetres = 1.2f } },
            // Faded paint: chalky grey wood showing where the white has weathered off frames and shutters.
            { "FrameFaded", new Def { set = "Props/weathered_brown_planks", target = new Color32(150, 146, 138, 255), tileMetres = 1.2f, desaturate = 0.85f } },
            // Curtains behind the glass: white and cream cotton (the plaster scan's fine grain only, barely any relief).
            { "CurtainWhite", new Def { set = "Textures/painted_plaster_wall", target = new Color32(224, 222, 214, 255), tileMetres = 1.5f, normalScale = 0.15f, smoothScale = 0.5f } },
            { "CurtainCream", new Def { set = "Textures/painted_plaster_wall", target = new Color32(214, 202, 176, 255), tileMetres = 1.5f, normalScale = 0.15f, smoothScale = 0.5f } },
            // Interior walls and ceilings: the same white family as outside, cleaner and a touch warmer (fresh emulsion
            // over smooth plaster: far less relief), with a slightly older, warmer coat lower down on the ground floor.
            { "InteriorPaint", new Def { set = "Textures/painted_plaster_wall", target = new Color32(218, 215, 206, 255), tileMetres = 2.6f, normalScale = 0.3f, smoothScale = 0.8f } },
            { "InteriorPaintOld", new Def { set = "Textures/painted_plaster_wall", target = new Color32(210, 204, 190, 255), tileMetres = 2.6f, normalScale = 0.35f, smoothScale = 0.8f } },
            { "Street", new Def { set = "Textures/asphalt_02", target = new Color32(92, 92, 88, 255), tileMetres = 3.5f } },
            // Rough limestone masonry of the retaining and garden walls: grey-white stones, flush lime mortar
            // (streets/main_02, sampled 160/156/150 under overcast light). A 2 m scan.
            { "StoneRubble", new Def { set = "Textures/old_stone_wall", target = new Color32(165, 160, 150, 255), tileMetres = 2.0f, normalScale = 1.3f } },
            // Dry, stony red-brown earth of yards and verges (late summer). A 4 m scan.
            { "Earth", new Def { set = "Textures/dry_ground_rocks", target = new Color32(150, 124, 100, 255), tileMetres = 4.0f } },
        };

        // Decal materials: texture folder, draw order. Every kind has its own order: overlapping decals with the
        // same order swap sorting as the camera moves, which flickers.
        // Weathering stains are 4-cell atlases cut from the CC0 scans (Tools/assets/lockdown_decals.py), each in two
        // materials one draw order apart: the generator alternates overlapping stains of one kind between them.
        static readonly (string name, string atlas, int order)[] AtlasDecals =
        {
            ("Damp", "T_Decal_Damp_diff", 0), ("Peel", "T_Decal_PeelAtlas_diff", 2), ("Grime", "T_Decal_Grime_diff", 4), ("Streak", "T_Decal_Streak_diff", 7),
        };
        const string DecalShader = "Shader Graphs/LockdownDecal"; // URP's decal graph with angle fade switched on
        const string NoisePath = "Assets/Lockdown/Textures/Noise/T_Noise_Plaster_mask.png";
        const string MacroNoisePath = "Assets/Lockdown/Textures/Noise/T_Noise_Macro.png";
        const string MacroNormalPath = "Assets/Lockdown/Textures/Noise/T_Noise_Macro_nor.png";
        const string ToneMapPath = "Assets/Lockdown/Textures/Noise/T_Noise_MacroTone.png";
        const string DecalTexDir = "Assets/Lockdown/Textures/Decals";
        static readonly HashSet<string> LayeredOnly = new() { "Render", "Wood", "Metal", "WoodBleached", "Rust", "Concrete", "FrameWhite", "FrameFaded", "CurtainWhite", "CurtainCream", "InteriorPaint", "InteriorPaintOld", "StoneRubble" };

        // Settings of the two-layer shader per material (see LockdownPlaster.shader).
        class Layering
        {
            public float noiseScale = 0.37f, noiseAmp = 0.45f, blendWidth = 0.12f, desaturateA, vertexDarken;
            public float macro, patches, speckle, speckleScale = 2f;
            public bool hex, tone;             // no visible repetition: stochastic tiling, large-scale tone
        }

        [MenuItem("Lockdown/Build/House Materials")]
        public static void BuildAll()
        {
            filesCache = new Dictionary<string, string[]>();
            meanCache = new Dictionary<string, Color>();
            try { BuildAllCached(); }
            finally { filesCache = null; meanCache = null; }
        }

        static void BuildAllCached()
        {
            Directory.CreateDirectory(MatDir);
            EnsureDecalFeature();
            var report = new List<string>();
            foreach (var kv in Defs)
            {
                if (LayeredOnly.Contains(kv.Key)) continue; // two-layer Lockdown/Plaster materials, built below
                string path = kv.Key is "Street" or "Earth" ? $"Assets/Lockdown/Materials/Ground/M_{kv.Key}.mat" : $"{MatDir}/M_House_{kv.Key}.mat";
                report.Add(BuildLit(path, kv.Value));
            }
            BuildGlass($"{MatDir}/M_House_Glass.mat", new Color32(22, 30, 30, 255), 0.13f, 0.97f);
            BuildGlass($"{MatDir}/M_House_GlassDusty.mat", new Color32(78, 80, 76, 255), 0.3f, 0.86f);
            BuildDark();
            BuildPlain($"{MatDir}/M_House_Aluminium.mat", new Color32(214, 214, 208, 255), 0f, 0.45f, 0.08f);  // white powder coat
            BuildBrass($"{MatDir}/M_House_Brass.mat");
            BuildPlain($"{MatDir}/M_House_Hardware.mat", new Color32(178, 176, 170, 255), 1f, 0.58f, 0.05f);  // satin aluminium / steel fittings
            BuildDecal("Rust", "Leaking016A", 6);
            foreach (var d in AtlasDecals)
            {
                BuildDecal(d.name, null, d.order, $"{DecalTexDir}/{d.atlas}.png");
                BuildDecal(d.name + "_B", null, d.order + 1, $"{DecalTexDir}/{d.atlas}.png");
            }
            // Chips project diagonally onto a corner; a decal normal map would be tilted with the projector and light
            // both faces as if they faced the corner, so chips keep the wall's own normals (colour only).
            BuildDecal("Chip", null, 9, $"{DecalTexDir}/T_Decal_Chip_diff.png");
            BuildDecal("Crack", null, 10, $"{DecalTexDir}/T_Decal_Crack_diff.png");
            EnsureNormalMap(MacroNormalPath);
            EnsureLinear(MacroNoisePath);
            EnsureLinear(ToneMapPath);
            // Walls: upper and ground-floor render, plus the hand-built bumps and repaired patches, never repeating.
            report.Add(BuildPlaster($"{MatDir}/M_House_Render.mat", Defs["Render"], Defs["RenderOld"], new Layering
            {
                macro = ImperfectionSettings.PlasterBumps, patches = ImperfectionSettings.PlasterPatches, hex = true, tone = true,
            }));
            // Bare concrete block and cement (rooftop room, sills, plinths, porch floor): one scan in both layers.
            report.Add(BuildPlaster($"{MatDir}/M_House_Concrete.mat", Defs["Concrete"], Defs["Concrete"], new Layering { hex = true, tone = true }));
            // Rubble stone walls: one scan in both layers, never repeating (hex + tone), so no stone shows twice.
            report.Add(BuildPlaster($"{MatDir}/M_House_StoneRubble.mat", Defs["StoneRubble"], Defs["StoneRubble"], new Layering { hex = true, tone = true }));
            // White paint (A) worn through to grey wood (B) in patches, by vertex R = how faded each frame / leaf is.
            report.Add(BuildPlaster($"{MatDir}/M_House_FrameWhite.mat", Defs["FrameWhite"], Defs["FrameFaded"], new Layering
            {
                noiseScale = 3.1f, noiseAmp = 1.5f, blendWidth = 0.25f, vertexDarken = 0.3f,
            }));
            // Timber: sun-bleached grey (A) over brown (B), mixed per piece by vertex colour; rain darkens (G).
            report.Add(BuildPlaster($"{MatDir}/M_House_Wood.mat", Defs["WoodBleached"], Defs["Wood"], new Layering
            {
                noiseScale = 1.2f, noiseAmp = 0.8f, blendWidth = 1.6f, desaturateA = Defs["WoodBleached"].desaturate, vertexDarken = 0.45f,
            }));
            // Iron: black paint (A), rust (B) at joints and feet by vertex colour, and small chips everywhere.
            report.Add(BuildPlaster($"{MatDir}/M_House_Metal.mat", Defs["Metal"], Defs["Rust"], new Layering
            {
                noiseScale = 4f, noiseAmp = 1.2f, blendWidth = 0.6f, speckle = 0.1f, speckleScale = 2f,
            }));
            // Rooms behind the openings: the same two renders, no imperfections, darkened with depth by vertex G
            // (until the baked light of Task 5, the only thing that makes an unlit room read as dim from outside).
            report.Add(BuildPlaster($"{MatDir}/M_House_Interior.mat", Defs["Render"], Defs["RenderOld"], new Layering { vertexDarken = 0.95f, blendWidth = 0.4f }));
            // Room walls: clean paint, a hand-plastered undulation (imperfections rule), never repeating (hex + tone), no
            // patches or stains.
            report.Add(BuildPlaster($"{MatDir}/M_House_InteriorWall.mat", Defs["InteriorPaint"], Defs["InteriorPaintOld"], new Layering
            {
                macro = ImperfectionSettings.PlasterBumps * 0.5f, hex = true, tone = true, blendWidth = 0.4f,
            }));
            report.Add(BuildPlaster($"{MatDir}/M_House_Curtain.mat", Defs["CurtainWhite"], Defs["CurtainCream"], new Layering { vertexDarken = 0.6f, blendWidth = 0.4f, noiseAmp = 0.1f }));
            AssetDatabase.SaveAssets();
            Debug.Log("[Lockdown] House materials built. Albedo report (mean sRGB, luminance, verdict):\n" + string.Join("\n", report));
        }

        struct Resolved
        {
            public Texture2D diff, nor, maos;
            public Color tint, mean;
        }

        static Resolved Resolve(Def d)
        {
            string dir = $"{PH}/{d.set}";
            string id = Path.GetFileName(d.set);
            string rdir = $"{PH}/{d.reliefSet ?? d.set}";
            string rid = Path.GetFileName(d.reliefSet ?? d.set);
            string diff = Find(dir, id + "_diff_"), nor = Find(rdir, rid + "_nor_gl_"), rough = Find(rdir, rid + "_rough_");
            string maos = $"{rdir}/{rid}_MAOS.png";
            if (!File.Exists(maos) && rough != null)
            {
                maos = LockdownTexturePacker.Pack(rough, false);
                filesCache?.Remove(rdir); // the folder has a new file now
            }
            Color mean = MeanSrgb(diff);
            float lum = 0.2126f * mean.r + 0.7152f * mean.g + 0.0722f * mean.b;
            mean = Color.Lerp(mean, new Color(lum, lum, lum), d.desaturate); // the shader greys layer A the same way
            Color tgt = d.target;
            return new Resolved
            {
                diff = AssetDatabase.LoadAssetAtPath<Texture2D>(diff),
                nor = AssetDatabase.LoadAssetAtPath<Texture2D>(nor),
                maos = AssetDatabase.LoadAssetAtPath<Texture2D>(maos),
                mean = mean,
                tint = new Color(Mathf.Min(MaxTint, tgt.r / Mathf.Max(mean.r, 0.01f)), Mathf.Min(MaxTint, tgt.g / Mathf.Max(mean.g, 0.01f)), Mathf.Min(MaxTint, tgt.b / Mathf.Max(mean.b, 0.01f)), 1f),
            };
        }

        // Two-layer materials (Lockdown/Plaster): walls (upper / ground-floor render), wood (bleached / brown),
        // metal (paint / rust), blended by vertex colour with a noisy edge.
        static string BuildPlaster(string matPath, Def a, Def b, Layering l)
        {
            var mat = GetOrCreate(matPath, "Lockdown/Plaster");
            mat.shader = Shader.Find("Lockdown/Plaster");
            var ra = Resolve(a);
            var rb = Resolve(b);
            mat.SetTexture("_BaseMap", ra.diff);
            mat.SetTextureScale("_BaseMap", Vector2.one / a.tileMetres);
            mat.SetTextureOffset("_BaseMap", Vector2.zero);
            mat.SetColor("_BaseColor", ra.tint);
            mat.SetTexture("_BumpMap", ra.nor);
            mat.SetFloat("_BumpScale", a.normalScale);
            mat.SetTexture("_MaskMap", ra.maos);
            mat.SetFloat("_Smoothness", a.smoothScale);
            mat.SetTexture("_BaseMapB", rb.diff);
            mat.SetColor("_BaseColorB", rb.tint);
            mat.SetTexture("_BumpMapB", rb.nor);
            mat.SetFloat("_BumpScaleB", b.normalScale);
            mat.SetTexture("_MaskMapB", rb.maos);
            mat.SetFloat("_SmoothnessB", b.smoothScale);
            mat.SetFloat("_TilingB", 1f / b.tileMetres);
            mat.SetFloat("_Metallic", 1f);
            mat.SetFloat("_OcclusionStrength", 1f);
            mat.SetTexture("_NoiseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(NoisePath));
            mat.SetFloat("_NoiseScale", l.noiseScale);
            mat.SetFloat("_NoiseAmp", l.noiseAmp);
            mat.SetFloat("_BlendWidth", l.blendWidth);
            mat.SetFloat("_DesaturateA", l.desaturateA);
            mat.SetFloat("_DesaturateB", b.desaturate);
            mat.SetFloat("_VertexDarken", l.vertexDarken);
            // Hand-built imperfections.
            var macroNoise = AssetDatabase.LoadAssetAtPath<Texture2D>(MacroNoisePath);
            mat.SetTexture("_MacroNormal", AssetDatabase.LoadAssetAtPath<Texture2D>(MacroNormalPath));
            mat.SetTexture("_MacroNoise", macroNoise);
            mat.SetFloat("_MacroScale", 1f / 6f);       // bumps ~2 m across
            mat.SetFloat("_MacroStrength", l.macro);
            mat.SetFloat("_UseMacro", l.macro > 0f ? 1f : 0f);
            SetKeyword(mat, "_MACRONORMAL", l.macro > 0f);
            mat.SetFloat("_PatchScale", 1f / 8f);       // patches ~1-2 m across
            mat.SetFloat("_PatchThreshold", Mathf.Lerp(0.95f, 0.72f, l.patches));
            mat.SetColor("_PatchTint", new Color(1.045f, 1.035f, 1.0f, 1f)); // fresher, slightly warmer white
            mat.SetFloat("_PatchSmoothness", 0.05f);
            mat.SetFloat("_PatchRim", 0.08f);
            mat.SetFloat("_UsePatches", l.patches > 0f ? 1f : 0f);
            SetKeyword(mat, "_PATCHES", l.patches > 0f);
            mat.SetFloat("_Speckle", l.speckle);
            mat.SetFloat("_SpeckleScale", l.speckleScale);
            mat.SetFloat("_UseSpeckle", l.speckle > 0f ? 1f : 0f);
            SetKeyword(mat, "_SPECKLE", l.speckle > 0f);
            // No visible repetition: hex cells ~1.5 m, each at its own offset and any angle; a tone map 64 m across.
            mat.SetFloat("_UseHex", l.hex ? 1f : 0f);
            SetKeyword(mat, "_HEXTILE", l.hex);
            mat.SetFloat("_HexCell", 1.5f);
            // Small turns only: the plaster scans light differently once turned far (their relief runs mostly up and
            // down), and the low sun would pick that out. Offsets do the real work of breaking the repeat.
            mat.SetFloat("_HexRotate", 0.05f);
            mat.SetFloat("_HexContrast", 0.6f);
            mat.SetFloat("_UseTone", l.tone ? 1f : 0f);
            SetKeyword(mat, "_TONE", l.tone);
            mat.SetTexture("_ToneMap", AssetDatabase.LoadAssetAtPath<Texture2D>(ToneMapPath));
            mat.SetFloat("_ToneScale", 1f / 64f);
            mat.SetFloat("_ToneDirt", 0.5f);
            mat.SetFloat("_ToneGrey", 0.4f);
            mat.SetFloat("_ToneBleach", 0.5f);
            mat.enableInstancing = false;
            EditorUtility.SetDirty(mat);
            return Report(Path.GetFileNameWithoutExtension(matPath) + " A", ra.mean, ra.tint, a.target) + "\n" +
                   Report(Path.GetFileNameWithoutExtension(matPath) + " B", rb.mean, rb.tint, b.target);
        }

        static string Report(string name, Color mean, Color tint, Color32 target)
        {
            Color result = new Color(mean.r * tint.r, mean.g * tint.g, mean.b * tint.b);
            float lum = 0.2126f * result.r + 0.7152f * result.g + 0.0722f * result.b;
            bool clipped = tint.r >= MaxTint - 0.001f || tint.g >= MaxTint - 0.001f || tint.b >= MaxTint - 0.001f;
            string verdict = lum < 30f / 255f ? "TOO DARK" : lum > 240f / 255f ? "TOO BRIGHT" : clipped ? "TINT LIMIT (scan too far off)" : "ok";
            return $"  {name,-22} {Rgb(result)}  L={lum * 255f:0}  {verdict}";
        }

        static string BuildLit(string matPath, Def d)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(matPath));
            string dir = $"{PH}/{d.set}";
            string id = Path.GetFileName(d.set);
            string rdir = $"{PH}/{d.reliefSet ?? d.set}";
            string rid = Path.GetFileName(d.reliefSet ?? d.set);
            string diff = Find(dir, id + "_diff_"), nor = Find(rdir, rid + "_nor_gl_"), rough = Find(rdir, rid + "_rough_");
            string maos = $"{rdir}/{rid}_MAOS.png";
            if (!File.Exists(maos) && rough != null)
            {
                maos = LockdownTexturePacker.Pack(rough, false);
                filesCache?.Remove(rdir); // the folder has a new file now
            }

            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, matPath);
            }
            mat.shader = Shader.Find("Universal Render Pipeline/Lit");
            var diffTex = AssetDatabase.LoadAssetAtPath<Texture2D>(diff);
            var norTex = AssetDatabase.LoadAssetAtPath<Texture2D>(nor);
            var maosTex = AssetDatabase.LoadAssetAtPath<Texture2D>(maos);
            float scale = 1f / d.tileMetres;

            Color mean = MeanSrgb(diff);
            Color tgt = d.target;
            // Tint per channel so mean(albedo x tint) hits the target. Scans are often shot darker than the real
            // surface, so the tint may brighten too, up to MaxTint (beyond that the scan is the wrong choice).
            var tint = new Color(Mathf.Min(MaxTint, tgt.r / Mathf.Max(mean.r, 0.01f)), Mathf.Min(MaxTint, tgt.g / Mathf.Max(mean.g, 0.01f)), Mathf.Min(MaxTint, tgt.b / Mathf.Max(mean.b, 0.01f)), 1f);
            mat.SetColor("_BaseColor", tint);
            mat.SetTexture("_BaseMap", diffTex);
            mat.SetTextureScale("_BaseMap", new Vector2(scale, scale));
            mat.SetTexture("_BumpMap", norTex);
            mat.SetFloat("_BumpScale", d.normalScale);
            mat.SetTexture("_MetallicGlossMap", maosTex);
            mat.SetTexture("_OcclusionMap", maosTex);
            mat.SetFloat("_OcclusionStrength", 1f);
            mat.SetFloat("_Smoothness", d.smoothScale);
            mat.SetFloat("_SmoothnessTextureChannel", 0f); // smoothness from the MAOS alpha
            mat.SetFloat("_Metallic", 1f);                  // scaled by the map's R (0 for these sets)
            SetKeyword(mat, "_NORMALMAP", norTex != null);
            SetKeyword(mat, "_METALLICSPECGLOSSMAP", maosTex != null);
            SetKeyword(mat, "_OCCLUSIONMAP", maosTex != null);
            EditorUtility.SetDirty(mat);

            Color result = new Color(mean.r * tint.r, mean.g * tint.g, mean.b * tint.b);
            float lum = 0.2126f * result.r + 0.7152f * result.g + 0.0722f * result.b;
            bool clipped = tint.r >= MaxTint - 0.001f || tint.g >= MaxTint - 0.001f || tint.b >= MaxTint - 0.001f;
            string verdict = lum < 30f / 255f ? "TOO DARK" : lum > 240f / 255f ? "TOO BRIGHT" : clipped ? "TINT LIMIT (scan too far off)" : "ok";
            return $"  {Path.GetFileNameWithoutExtension(matPath),-20} {Rgb(result)}  L={lum * 255f:0}  {verdict}";
        }

        // Glass: transparent with "preserve specular", so a pane lets ~85 % of the room through yet keeps its full
        // reflection (Fresnel stronger at a slant), like real glass. A faint large-scale normal gives the gentle waviness
        // of old float glass. No shadow pass: sunlight goes through into the room.
        static void BuildGlass(string path, Color32 tint, float alpha, float smooth)
        {
            var mat = GetOrCreate(path, "Universal Render Pipeline/Lit");
            mat.shader = Shader.Find("Universal Render Pipeline/Lit");
            Color c = tint;
            c.a = alpha;
            mat.SetColor("_BaseColor", c);
            mat.SetTexture("_BaseMap", null);
            mat.SetFloat("_Smoothness", smooth);
            mat.SetFloat("_Metallic", 0f);
            mat.SetTexture("_MetallicGlossMap", null);
            mat.SetTexture("_OcclusionMap", null);
            EnsureNormalMap(MacroNormalPath);
            mat.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(MacroNormalPath));
            mat.SetTextureScale("_BaseMap", Vector2.one * 0.4f); // bump waves ~2.5 m across
            mat.SetFloat("_BumpScale", 0.06f);
            mat.SetFloat("_Surface", 1f);                      // transparent
            mat.SetFloat("_Blend", 0f);                        // alpha
            mat.SetFloat("_BlendModePreserveSpecular", 1f);
            mat.SetFloat("_Cull", 2f);
            mat.SetFloat("_ReceiveShadows", 1f);
            mat.SetFloat("_SpecularHighlights", 1f);
            mat.SetFloat("_EnvironmentReflections", 1f);
            BaseShaderGUI.SetMaterialKeywords(mat, UnityEditor.Rendering.Universal.ShaderGUI.LitGUI.SetMaterialKeywords);
            mat.SetShaderPassEnabled("ShadowCaster", false);
            EditorUtility.SetDirty(mat);
        }

        // Old brass (door handles, hinges): the rusty-metal scan's mottling tinted to tarnished brass, metallic, so the
        // fittings read as decades old (brown patina and dull spots) rather than shiny new gold.
        static void BuildBrass(string path)
        {
            var mat = GetOrCreate(path, "Universal Render Pipeline/Lit");
            mat.shader = Shader.Find("Universal Render Pipeline/Lit");
            string dir = $"{PH}/Props/rusty_metal_02";
            string diff = Find(dir, "rusty_metal_02_diff_"), nor = Find(dir, "rusty_metal_02_nor_gl_");
            Color mean = MeanSrgb(diff);
            Color32 target = new Color32(150, 118, 66, 255);
            Color t = target;
            mat.SetColor("_BaseColor", new Color(Mathf.Min(2.5f, t.r / Mathf.Max(mean.r, 0.01f)), Mathf.Min(2.5f, t.g / Mathf.Max(mean.g, 0.01f)), Mathf.Min(2.5f, t.b / Mathf.Max(mean.b, 0.01f)), 1f));
            mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(diff));
            mat.SetTextureScale("_BaseMap", Vector2.one * 6f); // ~13 cm repeat: patina spots at handle scale
            mat.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(nor));
            mat.SetFloat("_BumpScale", 0.35f);
            mat.SetTexture("_MetallicGlossMap", null);
            mat.SetFloat("_Metallic", 0.85f);
            mat.SetFloat("_Smoothness", 0.5f);
            BaseShaderGUI.SetMaterialKeywords(mat, UnityEditor.Rendering.Universal.ShaderGUI.LitGUI.SetMaterialKeywords);
            EditorUtility.SetDirty(mat);
        }

        // Untextured URP Lit (smooth paint, fittings) with a faint macro normal so it isn't CG-perfect.
        static void BuildPlain(string path, Color32 albedo, float metallic, float smooth, float bump)
        {
            var mat = GetOrCreate(path, "Universal Render Pipeline/Lit");
            mat.shader = Shader.Find("Universal Render Pipeline/Lit");
            mat.SetColor("_BaseColor", albedo);
            mat.SetTexture("_BaseMap", null);
            mat.SetFloat("_Metallic", metallic);
            mat.SetFloat("_Smoothness", smooth);
            mat.SetTexture("_MetallicGlossMap", null);
            EnsureNormalMap(MacroNormalPath);
            mat.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(MacroNormalPath));
            mat.SetTextureScale("_BaseMap", Vector2.one * 3f);
            mat.SetFloat("_BumpScale", bump);
            BaseShaderGUI.SetMaterialKeywords(mat, UnityEditor.Rendering.Universal.ShaderGUI.LitGUI.SetMaterialKeywords);
            EditorUtility.SetDirty(mat);
        }

        static void BuildDark()
        {
            // The dark interior behind open doorways: rough render, very low albedo (an unlit room seen from the sun).
            var mat = GetOrCreate($"{MatDir}/M_House_Dark.mat", "Universal Render Pipeline/Lit");
            var src = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/M_House_RenderOld.mat");
            if (src != null)
            {
                mat.SetTexture("_BaseMap", src.GetTexture("_BaseMap"));
                mat.SetTextureScale("_BaseMap", src.GetTextureScale("_BaseMap"));
                mat.SetTexture("_BumpMap", src.GetTexture("_BumpMap"));
                SetKeyword(mat, "_NORMALMAP", true);
            }
            mat.SetColor("_BaseColor", new Color32(52, 49, 45, 255)); // lowest valid albedo; the darkness comes from the light
            mat.SetFloat("_Smoothness", 0.05f);
            EditorUtility.SetDirty(mat);
        }

        // Masks are data, not colour: thresholds must see the stored values.
        static void EnsureLinear(string path)
        {
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null || !imp.sRGBTexture) return;
            imp.sRGBTexture = false;
            imp.SaveAndReimport();
        }

        static void EnsureNormalMap(string path)
        {
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null || imp.textureType == TextureImporterType.NormalMap) return;
            imp.textureType = TextureImporterType.NormalMap;
            imp.SaveAndReimport();
        }

        static void BuildDecal(string name, string id, int order, string basePath = null, string norPath = null)
        {
            var mat = GetOrCreate($"{MatDir}/M_Decal_{name}.mat", DecalShader);
            mat.shader = Shader.Find(DecalShader);
            var baseTex = AssetDatabase.LoadAssetAtPath<Texture2D>(basePath ?? $"{Decals}/{id}/{id.ToLowerInvariant()}_diff.png");
            if (norPath != null) EnsureNormalMap(norPath);
            var norTex = norPath != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(norPath)
                : id == null ? null : AssetDatabase.LoadAssetAtPath<Texture2D>($"{Decals}/{id}/{id.ToLowerInvariant()}_nor_gl.png");
            if (baseTex != null)
            {
                var imp = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(baseTex));
                if (!imp.alphaIsTransparency || imp.wrapMode != TextureWrapMode.Repeat)
                {
                    imp.alphaIsTransparency = true;
                    imp.wrapMode = TextureWrapMode.Repeat;
                    imp.SaveAndReimport();
                }
            }
            mat.SetTexture("Base_Map", baseTex);
            mat.SetTexture("Normal_Map", norTex);
            mat.SetFloat("Normal_Blend", norTex != null ? 0.6f : 0f);
            mat.SetFloat("_DrawOrder", order);
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
        }

        // Adds URP's Decal renderer feature once (DBuffer, albedo + normal + MAOS).
        static void EnsureDecalFeature()
        {
            var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (rd.rendererFeatures.Any(f => f is DecalRendererFeature)) return;
            var feature = ScriptableObject.CreateInstance<DecalRendererFeature>();
            feature.name = "Decal";
            AssetDatabase.AddObjectToAsset(feature, rd);
            rd.rendererFeatures.Add(feature);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);
            var so = new SerializedObject(rd);
            var map = so.FindProperty("m_RendererFeatureMap");
            map.arraySize++;
            map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
            so.ApplyModifiedProperties();
            rd.SetDirty();
            EditorUtility.SetDirty(rd);
            AssetDatabase.SaveAssets();
            Debug.Log("[Lockdown] Decal renderer feature added.");
        }

        // ---------- Helpers ----------

        static Material GetOrCreate(string path, string shader)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find(shader));
                AssetDatabase.CreateAsset(mat, path);
            }
            return mat;
        }

        // During one BuildAll the same folders are listed and the same images decoded many times (one scan feeds
        // several materials), so both are cached for that build only; any other caller reads the disk as before.
        static Dictionary<string, string[]> filesCache;
        static Dictionary<string, Color> meanCache;

        static string Find(string dir, string prefix)
        {
            if (!Directory.Exists(dir)) return null;
            string[] files = null;
            if (filesCache != null && !filesCache.TryGetValue(dir, out files)) filesCache[dir] = files = Directory.GetFiles(dir);
            files ??= Directory.GetFiles(dir);
            return files.Where(f => !f.EndsWith(".meta") && Path.GetFileName(f).StartsWith(prefix))
                .Select(f => f.Replace('\\', '/')).FirstOrDefault();
        }

        // Mean colour of an image file in sRGB 0-1, read straight from disk (the import's compression doesn't matter).
        public static Color MeanSrgb(string path)
        {
            if (meanCache != null && meanCache.TryGetValue(path, out var cached)) return cached;
            Color mean = ReadMeanSrgb(path);
            if (meanCache != null) meanCache[path] = mean;
            return mean;
        }

        static Color ReadMeanSrgb(string path)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false, false);
            tex.LoadImage(File.ReadAllBytes(path));
            var px = tex.GetPixels32();
            double r = 0, g = 0, b = 0;
            for (int i = 0; i < px.Length; i += 7) { r += px[i].r; g += px[i].g; b += px[i].b; }
            double n = (px.Length + 6) / 7;
            Object.DestroyImmediate(tex);
            return new Color((float)(r / n / 255.0), (float)(g / n / 255.0), (float)(b / n / 255.0));
        }

        static string Rgb(Color c) => $"({c.r * 255f:0},{c.g * 255f:0},{c.b * 255f:0})";

        static void SetKeyword(Material m, string k, bool on)
        {
            if (on) m.EnableKeyword(k); else m.DisableKeyword(k);
        }
    }
}
