// Greybox blocks (Tasks 8-19): a flat tint with a world-space 1 m / 10 m grid, so scale reads on any block
// whatever its size (primitive cubes stretch their UVs). The grid is projected along the face's dominant axis.
// Uses URP Lit's inputs, so the shadow caster, depth, depth-normals and meta passes are URP's own pass files; only
// the forward pass is written here (URP 17.2's LitForwardPass, cut down: no normal map, no alpha, no clear coat).
Shader "Lockdown/GreyboxGrid"
{
    Properties
    {
        [MainColor] _BaseColor("Tint", Color) = (0.5, 0.5, 0.5, 1)
        [MainTexture] _BaseMap("Albedo (unused, white)", 2D) = "white" {}
        _Smoothness("Smoothness", Range(0, 1)) = 0.15
        _Metallic("Metallic", Range(0, 1)) = 0

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
            #pragma vertex GreyboxVertex
            #pragma fragment GreyboxFragment

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
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ USE_LEGACY_LIGHTMAPS
            #pragma multi_compile_fragment _ DEBUG_DISPLAY
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #define REQUIRES_WORLD_SPACE_POS_INTERPOLATOR
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // Not in LitInput's material buffer (kept out of it so URP's own passes stay batch-compatible):
            // constants per shader instead of per material.
            static const half kMinor = 0.10h;
            static const half kMajor = 0.22h;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 staticLightmapUV  : TEXCOORD1;
                float2 dynamicLightmapUV : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float3 positionWS : TEXCOORD1;
                float3 normalWS   : TEXCOORD2;
                half   fogFactor  : TEXCOORD5;
                DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 8);
            #ifdef DYNAMICLIGHTMAP_ON
                float2 dynamicLightmapUV : TEXCOORD9;
            #endif
            #ifdef USE_APV_PROBE_OCCLUSION
                float4 probeOcclusion : TEXCOORD10;
            #endif
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings GreyboxVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                VertexPositionInputs vp = GetVertexPositionInputs(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionWS = vp.positionWS;
                output.positionCS = vp.positionCS;
                output.fogFactor = ComputeFogFactor(vp.positionCS.z);
                OUTPUT_LIGHTMAP_UV(input.staticLightmapUV, unity_LightmapST, output.staticLightmapUV);
            #ifdef DYNAMICLIGHTMAP_ON
                output.dynamicLightmapUV = input.dynamicLightmapUV.xy * unity_DynamicLightmapST.xy + unity_DynamicLightmapST.zw;
            #endif
                OUTPUT_SH4(vp.positionWS, output.normalWS, GetWorldSpaceNormalizeViewDir(vp.positionWS), output.vertexSH, output.probeOcclusion);
                return output;
            }

            // Anti-aliased grid lines: 1 where a line of the given period and half-width (metres) passes.
            half GridLine(float2 uv, float period, float halfWidth)
            {
                float2 d = abs(frac(uv / period + 0.5) - 0.5) * period;
                float2 aa = max(fwidth(uv), 1e-4);
                float2 l = 1.0 - smoothstep(halfWidth - aa, halfWidth + aa, d);
                // Fade lines out where they get denser than a few pixels, so distant blocks don't turn grey.
                float2 fade = saturate(1.0 - aa * 4.0 / period);
                return (half)max(l.x * fade.x, l.y * fade.y);
            }

            void GreyboxFragment(Varyings input, out half4 outColor : SV_Target0
            #ifdef _WRITE_RENDERING_LAYERS
                , out uint outRenderingLayers : SV_Target1
            #endif
            )
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float3 n = normalize(input.normalWS);
                float3 an = abs(n);
                float3 p = input.positionWS;
                float2 uv = an.y >= max(an.x, an.z) ? p.xz : (an.x >= an.z ? p.zy : p.xy);
                half grid = 1.0h - kMinor * GridLine(uv, 1.0, 0.012) - kMajor * GridLine(uv, 10.0, 0.035);

                SurfaceData s = (SurfaceData)0;
                s.albedo = _BaseColor.rgb * grid;
                s.metallic = _Metallic;
                s.specular = half3(0, 0, 0);
                s.smoothness = _Smoothness;
                s.normalTS = half3(0, 0, 1);
                s.occlusion = 1.0h;
                s.alpha = 1.0h;

                InputData d = (InputData)0;
                d.positionWS = p;
            #if defined(DEBUG_DISPLAY)
                d.positionCS = input.positionCS;
            #endif
                d.normalWS = n;
                d.viewDirectionWS = GetWorldSpaceNormalizeViewDir(p);
            #if defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                d.shadowCoord = TransformWorldToShadowCoord(p);
            #endif
                d.fogCoord = InitializeInputDataFog(float4(p, 1.0), input.fogFactor);
                d.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
            #if defined(DYNAMICLIGHTMAP_ON)
                d.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.dynamicLightmapUV, input.vertexSH, d.normalWS);
                d.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
            #elif !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
                d.bakedGI = SAMPLE_GI(input.vertexSH, GetAbsolutePositionWS(d.positionWS), d.normalWS,
                    d.viewDirectionWS, input.positionCS.xy, input.probeOcclusion, d.shadowMask);
            #else
                d.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, d.normalWS);
                d.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
            #endif
            #if defined(_DBUFFER)
                ApplyDecalToSurfaceData(input.positionCS, s, d);
            #endif
                half4 color = UniversalFragmentPBR(d, s);
                color.rgb = MixFog(color.rgb, d.fogCoord);
                color.a = 1.0h;
                outColor = color;
            #ifdef _WRITE_RENDERING_LAYERS
                outRenderingLayers = EncodeMeshRenderingLayer();
            #endif
            }
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
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
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
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
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
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitDepthNormalsPass.hlsl"
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
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitMetaPass.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
