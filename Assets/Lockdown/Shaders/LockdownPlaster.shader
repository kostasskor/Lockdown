// House-wall plaster: two plaster layers (upper render A, rougher ground-floor render B) blended by vertex
// colour R with a noisy, soft edge, so there is never a straight seam between them. Opaque, metallic workflow.
// The same two-layer shader also weathers wood (A bleached grey, B brown) and metal (A paint, B rust).
// Hand-built imperfections (each behind a keyword): soft macro bumps, repaired patches in a slightly different
// white, speckled paint chips, and vertex colour G darkening (where rain runs).
// No visible repetition (each behind a keyword): stochastic hex tiling of both layers, and a large-scale tone map
// (dirtier / greyer / bleached areas) with a per-wall bias in vertex colour B.
// Passes follow URP 17.2's Lit shader (forward, shadow caster, depth, depth-normals, meta) so shadows, SSAO,
// decals, probe volumes, lightmaps, fog and the rendering debugger all work.
Shader "Lockdown/Plaster"
{
    Properties
    {
        [MainTexture] _BaseMap("A Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("A Tint", Color) = (1,1,1,1)
        [Normal] _BumpMap("A Normal", 2D) = "bump" {}
        _BumpScale("A Normal Scale", Float) = 1.0
        _MaskMap("A MAOS (R metal, G AO, A smooth)", 2D) = "white" {}
        _Smoothness("A Smoothness Scale", Range(0, 1)) = 1.0

        _BaseMapB("B Albedo", 2D) = "white" {}
        _BaseColorB("B Tint", Color) = (1,1,1,1)
        [Normal] _BumpMapB("B Normal", 2D) = "bump" {}
        _BumpScaleB("B Normal Scale", Float) = 1.0
        _MaskMapB("B MAOS", 2D) = "white" {}
        _SmoothnessB("B Smoothness Scale", Range(0, 1)) = 1.0
        _TilingB("B Tiling (per metre)", Float) = 0.4

        _Metallic("Metallic Scale", Range(0, 1)) = 1.0
        _OcclusionStrength("Occlusion Strength", Range(0, 1)) = 1.0

        _NoiseMap("Blend Noise", 2D) = "gray" {}
        _NoiseScale("Noise Scale (per metre)", Float) = 0.35
        _NoiseAmp("Noise Amplitude (m)", Float) = 0.45
        _BlendWidth("Blend Softness (m)", Float) = 0.12
        _DesaturateA("A Desaturate (bleached)", Range(0, 1)) = 0
        _DesaturateB("B Desaturate", Range(0, 1)) = 0
        _VertexDarken("Vertex G Darkening", Range(0, 1)) = 0

        [Toggle(_MACRONORMAL)] _UseMacro("Macro Bumps", Float) = 0
        [Normal] _MacroNormal("Macro Normal", 2D) = "bump" {}
        _MacroScale("Macro Scale (per metre)", Float) = 0.166
        _MacroStrength("Macro Strength", Range(0, 2)) = 0.6

        [Toggle(_PATCHES)] _UsePatches("Repaired Patches", Float) = 0
        _MacroNoise("Patch (R) / Speckle (G) Noise", 2D) = "black" {}
        _PatchScale("Patch Scale (per metre)", Float) = 0.125
        _PatchThreshold("Patch Threshold", Range(0, 1)) = 0.72
        _PatchTint("Patch Tint", Color) = (1.03, 1.02, 0.99, 1)
        _PatchSmoothness("Patch Smoothness Shift", Range(-0.5, 0.5)) = 0.05
        _PatchRim("Patch Rim Darkening", Range(0, 0.3)) = 0.06

        [Toggle(_SPECKLE)] _UseSpeckle("Speckle (paint chips)", Float) = 0
        _SpeckleScale("Speckle Scale (per metre)", Float) = 2
        _Speckle("Speckle Amount", Range(0, 1)) = 0.12

        // No visible repetition: every hex cell samples the textures with its own offset and rotation.
        [Toggle(_HEXTILE)] _UseHex("Stochastic Hex Tiling", Float) = 0
        _HexCell("Hex Cell (m)", Float) = 1.5
        _HexRotate("Hex Rotation (0 none, 1 any angle)", Range(0, 1)) = 1
        _HexContrast("Hex Blend Contrast", Range(0, 1)) = 0.6

        // Large-scale tone: soft multi-metre areas dirtier, greyer or bleached; vertex colour B adds a per-wall bias.
        [Toggle(_TONE)] _UseTone("Large-scale Tone", Float) = 0
        _ToneMap("Tone (R dirt, G grey, B bleach)", 2D) = "gray" {}
        _ToneScale("Tone Scale (per metre)", Float) = 0.015625
        _ToneDirt("Tone Dirt", Range(0, 1)) = 0.5
        _ToneGrey("Tone Grey", Range(0, 1)) = 0.4
        _ToneBleach("Tone Bleach", Range(0, 1)) = 0.5

        // Kept for URP's shared pass code (opaque, no alpha clip).
        [HideInInspector] _Cutoff("Alpha Cutoff", Range(0, 1)) = 0.5
        [HideInInspector] _Surface("__surface", Float) = 0.0
        [HideInInspector] _Cull("__cull", Float) = 2.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "IgnoreProjector" = "True"
        }
        LOD 300

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex PlasterPassVertex
            #pragma fragment PlasterPassFragment
            #pragma shader_feature_local_fragment _MACRONORMAL
            #pragma shader_feature_local_fragment _PATCHES
            #pragma shader_feature_local_fragment _SPECKLE
            #pragma shader_feature_local_fragment _HEXTILE
            #pragma shader_feature_local_fragment _TONE

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #include_with_pragmas "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRenderingKeywords.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fragment _ LIGHTMAP_BICUBIC_SAMPLING
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ USE_LEGACY_LIGHTMAPS
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_fragment _ DEBUG_DISPLAY
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"

            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "LockdownPlasterInput.hlsl"
            #include "LockdownPlasterPasses.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "LockdownPlasterInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include "LockdownPlasterInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            Cull[_Cull]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex PlasterDepthNormalsVertex
            #pragma fragment PlasterDepthNormalsFragment
            #pragma shader_feature_local_fragment _MACRONORMAL
            #pragma shader_feature_local_fragment _PATCHES
            #pragma shader_feature_local_fragment _SPECKLE
            #pragma shader_feature_local_fragment _HEXTILE
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include "LockdownPlasterInput.hlsl"
            #include "LockdownPlasterPasses.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "Meta"
            Tags { "LightMode" = "Meta" }
            Cull Off

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex UniversalVertexMeta
            #pragma fragment UniversalFragmentMetaLit
            #pragma shader_feature EDITOR_VISUALIZATION
            #include "LockdownPlasterInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitMetaPass.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
