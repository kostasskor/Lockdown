// Review labels (EditorOnly): TextMesh text that always faces the camera that renders it (Scene view, Game
// view and capture cameras alike), turned in the vertex shader so no script has to rotate anything.
// _Offset shifts the glyphs in object units, used for the dark outline copies drawn behind the text.
Shader "Lockdown/ReviewLabel"
{
    Properties
    {
        _MainTex ("Font Texture", 2D) = "white" {}
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _Offset ("Offset (object units)", Vector) = (0, 0, 0, 0)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+20" "RenderType" = "Transparent" "IgnoreProjector" = "True" "DisableBatching" = "True" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest LEqual
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "ReviewLabel"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Tint;
                float4 _Offset;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                float3 centreVS = TransformWorldToView(TransformObjectToWorld(float3(0, 0, 0)));
                float scale = length(float3(UNITY_MATRIX_M[0].x, UNITY_MATRIX_M[1].x, UNITY_MATRIX_M[2].x));
                // Close up the labels shrink (full size from 40 m, a third at 8 m) and fade out within 6 m,
                // so they never fill the screen while walking.
                float dist = -centreVS.z;
                scale *= clamp(dist / 40.0, 0.33, 1.0);
                float2 offset = (input.positionOS.xy + _Offset.xy) * scale;
                o.positionCS = TransformWViewToHClip(centreVS + float3(offset, 0));
                o.color = input.color * _Tint;
                o.color.a *= saturate((dist - 3.0) / 3.0);
                o.uv = input.uv;
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half a = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).a;
                return half4(input.color.rgb, input.color.a * a);
            }
            ENDHLSL
        }
    }
}
