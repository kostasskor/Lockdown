#ifndef LOCKDOWN_PLASTER_INPUT_INCLUDED
#define LOCKDOWN_PLASTER_INPUT_INCLUDED

// Two-layer plaster for house walls: layer A (upper render) and layer B (rougher ground-floor render),
// blended by vertex colour R (how far below the old split line a point is: 0.5 = on it, +-4 m range)
// with a world-space noise breaking up the edge. Layout mirrors URP 17.2's LitInput.hlsl so URP's own
// shadow / depth / meta passes work unchanged.

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DBuffer.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/DebugMipmapStreamingMacros.hlsl"

// NOTE: same layout in every pass, so the SRP Batcher can batch it.
CBUFFER_START(UnityPerMaterial)
float4 _BaseMap_ST;
float4 _BaseMap_TexelSize;
half4 _BaseColor;
half4 _BaseColorB;
float _TilingB;
half _Cutoff;
half _Smoothness;
half _SmoothnessB;
half _Metallic;
half _BumpScale;
half _BumpScaleB;
half _OcclusionStrength;
float _NoiseScale;
half _NoiseAmp;
half _BlendWidth;
half _DesaturateA;
half _DesaturateB;
half _VertexDarken;
float _MacroScale;
half _MacroStrength;
float _PatchScale;
half _PatchThreshold;
half4 _PatchTint;
half _PatchSmoothness;
half _PatchRim;
float _SpeckleScale;
half _Speckle;
float _HexCell;
half _HexRotate;
half _HexContrast;
float _ToneScale;
half _ToneDirt;
half _ToneGrey;
half _ToneBleach;
half _Surface;
UNITY_TEXTURE_STREAMING_DEBUG_VARS;
CBUFFER_END

TEXTURE2D(_MaskMap);    SAMPLER(sampler_MaskMap);   // layer A: R metallic, G AO, A smoothness (the project's _MAOS)
TEXTURE2D(_BaseMapB);   SAMPLER(sampler_BaseMapB);
TEXTURE2D(_BumpMapB);   SAMPLER(sampler_BumpMapB);
TEXTURE2D(_MaskMapB);   SAMPLER(sampler_MaskMapB);
TEXTURE2D(_NoiseMap);   SAMPLER(sampler_NoiseMap);
TEXTURE2D(_MacroNormal); SAMPLER(sampler_MacroNormal);
TEXTURE2D(_MacroNoise); SAMPLER(sampler_MacroNoise);
TEXTURE2D(_ToneMap);    SAMPLER(sampler_ToneMap);

// Masks are sampled turned by fixed, unrelated angles, so their own tiles never line up with each other, with the
// texture grid, or with the (axis-aligned) windows.
float2 Rot2(float2 v, float2 cs) { return float2(cs.x * v.x - cs.y * v.y, cs.y * v.x + cs.x * v.y); }
#define MASK_ROT_NOISE  float2(0.8191520, 0.5735764)   // 35 deg
#define MASK_ROT_PATCH  float2(0.4383711, 0.8987940)   // 64 deg
#define MASK_ROT_MACRO  float2(-0.6156615, 0.7880108)  // 128 deg
#define MASK_ROT_TONE   float2(0.9396926, -0.3420201)  // -20 deg

// 0 = layer A, 1 = layer B. uvMetres is the mesh UV (1 unit = 1 m; the generator gives every wall plane its own
// seeded offset). vcol = vertex colour: R drives the layer blend, G darkens (rain runs), B = per-wall dirt bias.
half PlasterBlend(float2 uvMetres, half3 vcol)
{
    half noise = SAMPLE_TEXTURE2D(_NoiseMap, sampler_NoiseMap, Rot2(uvMetres, MASK_ROT_NOISE) * _NoiseScale).r * 2.0h - 1.0h;
    half below = (vcol.r - 0.5h) * 8.0h;         // metres below the old split line
    half blend = smoothstep(-_BlendWidth, _BlendWidth, below + noise * _NoiseAmp);
#if defined(_SPECKLE)
    // Paint chipped off in small flakes all over, showing layer B (rust under paint).
    half fleck = SAMPLE_TEXTURE2D(_MacroNoise, sampler_MacroNoise, uvMetres * _SpeckleScale).g;
    blend = max(blend, smoothstep(1.0h - _Speckle, 1.0h - _Speckle + 0.04h, fleck));
#endif
    return blend;
}

// Repaired patches: blobs of newer plaster, 1 inside, with a soft but visible edge.
half PatchMask(float2 uvMetres)
{
#if defined(_PATCHES)
    half n = SAMPLE_TEXTURE2D(_MacroNoise, sampler_MacroNoise, Rot2(uvMetres, MASK_ROT_PATCH) * _PatchScale).r;
    return smoothstep(_PatchThreshold, _PatchThreshold + 0.025h, n);
#else
    return 0.0h;
#endif
}

// Both layers' textures at one point (albedo untinted, MAOS, tangent-space normal).
struct PlasterLayers
{
    half3 albedoA, albedoB;
    half4 maosA, maosB;
    half3 normalA, normalB;
};

#if defined(_HEXTILE)
// Stochastic hex tiling (after Mikkelsen 2022, "Practical Real-Time Hex-Tiling"): a triangle grid covers the
// surface; every grid vertex is the centre of a hex cell that samples the textures with its own random offset and
// rotation, plus a random mirror flip (which keeps a scan's relief running the way it was shot, unlike a big
// turn), and each pixel blends its 3 nearest cells. Gradients turn with the cell, so mip levels stay right.
struct HexTile
{
    float2 uv[3];   // per-cell texture position, metres
    float2 cs[3];   // per-cell rotation (cos, sin)
    float2 fl[3];   // per-cell mirror (+-1, +-1), applied before the rotation
    float3 w;       // barycentric weights
    float2 dx, dy;  // screen derivatives of the surface position
};

float2 HexHash(float2 p)
{
    float2 r = mul(float2x2(127.1, 311.7, 269.5, 183.3), p);
    return frac(sin(r) * 43758.5453);
}

HexTile HexSetup(float2 uvMetres)
{
    HexTile h;
    h.dx = ddx(uvMetres);
    h.dy = ddy(uvMetres);
    float2 sk = mul(float2x2(1.0, -0.57735027, 0.0, 1.15470054), uvMetres / _HexCell);
    float2 base = floor(sk);
    float3 t = float3(frac(sk), 0.0);
    t.z = 1.0 - t.x - t.y;
    float s = step(0.0, -t.z);
    float s2 = 2.0 * s - 1.0;
    h.w = float3(-t.z * s2, s - t.y * s2, s - t.x * s2);
    float2 v[3] = { base + float2(s, s), base + float2(s, 1.0 - s), base + float2(1.0 - s, s) };
    [unroll] for (int i = 0; i < 3; i++)
    {
        float2 rnd = HexHash(v[i]);
        float2 rnd2 = HexHash(v[i] + 17.0);
        float a = (rnd.x - 0.5) * TWO_PI * _HexRotate;
        h.cs[i] = float2(cos(a), sin(a));
        h.fl[i] = float2(rnd2.x < 0.5 ? -1.0 : 1.0, rnd2.y < 0.5 ? -1.0 : 1.0);
        float2 centre = mul(float2x2(1.0, 0.5, 0.0, 0.8660254), v[i]) * _HexCell;
        h.uv[i] = Rot2((uvMetres - centre) * h.fl[i], h.cs[i]) + centre + rnd * 41.0;
    }
    return h;
}

half4 HexSample(TEXTURE2D_PARAM(tex, smp), HexTile h, int i, float2 tiling, float2 offset)
{
    return SAMPLE_TEXTURE2D_GRAD(tex, smp, h.uv[i] * tiling + offset,
        Rot2(h.dx * h.fl[i], h.cs[i]) * tiling, Rot2(h.dy * h.fl[i], h.cs[i]) * tiling);
}

// Cell i's tangent-space normal, turned and mirrored back into the surface's frame.
half3 HexNormal(TEXTURE2D_PARAM(tex, smp), HexTile h, int i, float2 tiling, float2 offset, half scale)
{
    half3 n = UnpackNormalScale(HexSample(TEXTURE2D_ARGS(tex, smp), h, i, tiling, offset), scale);
    n.xy = Rot2(n.xy, float2(h.cs[i].x, -h.cs[i].y)) * h.fl[i];
    return n;
}
#endif

PlasterLayers SampleLayers(float2 uvMetres)
{
    PlasterLayers L;
    float2 tA = _BaseMap_ST.xy, oA = _BaseMap_ST.zw, tB = float2(_TilingB, _TilingB);
#if defined(_HEXTILE)
    HexTile h = HexSetup(uvMetres);
    half3 c[3];
    float3 lum;
    [unroll] for (int i = 0; i < 3; i++)
    {
        c[i] = HexSample(TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap), h, i, tA, oA).rgb;
        lum[i] = dot(c[i], half3(0.2126h, 0.7152h, 0.0722h));
    }
    // Contrast-preserving weights: short transitions, and where cells meet the one with more detail wins.
    float3 W = lerp(1.0, lum, _HexContrast) * pow(max(h.w, 0.0), 7.0);
    W /= max(W.x + W.y + W.z, 1e-6);
    L.albedoA = c[0] * W.x + c[1] * W.y + c[2] * W.z;
    L.albedoB = 0; L.maosA = 0; L.maosB = 0; L.normalA = 0; L.normalB = 0;
    [unroll] for (int j = 0; j < 3; j++)
    {
        L.albedoB += W[j] * HexSample(TEXTURE2D_ARGS(_BaseMapB, sampler_BaseMapB), h, j, tB, 0).rgb;
        L.maosA += W[j] * HexSample(TEXTURE2D_ARGS(_MaskMap, sampler_MaskMap), h, j, tA, oA);
        L.maosB += W[j] * HexSample(TEXTURE2D_ARGS(_MaskMapB, sampler_MaskMapB), h, j, tB, 0);
        L.normalA += W[j] * HexNormal(TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), h, j, tA, oA, _BumpScale);
        L.normalB += W[j] * HexNormal(TEXTURE2D_ARGS(_BumpMapB, sampler_BumpMapB), h, j, tB, 0, _BumpScaleB);
    }
    L.normalA = normalize(L.normalA);
    L.normalB = normalize(L.normalB);
#else
    float2 uvA = uvMetres * tA + oA, uvB = uvMetres * tB;
    L.albedoA = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uvA).rgb;
    L.albedoB = SAMPLE_TEXTURE2D(_BaseMapB, sampler_BaseMapB, uvB).rgb;
    L.maosA = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, uvA);
    L.maosB = SAMPLE_TEXTURE2D(_MaskMapB, sampler_MaskMapB, uvB);
    L.normalA = SampleNormal(uvA, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), _BumpScale);
    L.normalB = SampleNormal(uvB, TEXTURE2D_ARGS(_BumpMapB, sampler_BumpMapB), _BumpScaleB);
#endif
    return L;
}

half3 PlasterNormalTS(PlasterLayers L, float2 uvMetres, half blend, half patch)
{
    half3 n = normalize(lerp(L.normalA, L.normalB, blend));
    n.xy *= 1.0h - 0.5h * patch;                 // a newer patch is trowelled smoother
#if defined(_MACRONORMAL)
    // Soft, wide unevenness of hand-applied plaster, added on top of the fine texture (UDN blend).
    half3 m = SampleNormal(Rot2(uvMetres, MASK_ROT_MACRO) * _MacroScale, TEXTURE2D_ARGS(_MacroNormal, sampler_MacroNormal), _MacroStrength);
    m.xy = Rot2(m.xy, float2(MASK_ROT_MACRO.x, -MASK_ROT_MACRO.y));
    n = normalize(half3(n.xy + m.xy, n.z));
#endif
    return n;
}

half3 PlasterSurfaceNormal(float2 uvMetres, half3 vcol)
{
    return PlasterNormalTS(SampleLayers(uvMetres), uvMetres, PlasterBlend(uvMetres, vcol), PatchMask(uvMetres));
}

// Large-scale tone: multi-metre areas dirtier (darker, warmer), greyer, or bleached / repainted (lighter), from a
// 64 m tone map; wallDirt (vertex colour B) leans a whole wall dirtier and greyer.
half3 ApplyTone(half3 albedo, float2 uvMetres, half wallDirt)
{
#if defined(_TONE)
    half3 t = SAMPLE_TEXTURE2D(_ToneMap, sampler_ToneMap, Rot2(uvMetres, MASK_ROT_TONE) * _ToneScale).rgb;
    half dirt = smoothstep(0.5h, 0.78h, t.r + wallDirt * 0.2h);
    half grey = smoothstep(0.48h, 0.78h, t.g + wallDirt * 0.1h);
    half bleach = smoothstep(0.52h, 0.8h, t.b) * (1.0h - dirt);
    half lum = dot(albedo, half3(0.2126h, 0.7152h, 0.0722h));
    albedo = lerp(albedo, lum * half3(1.0h, 0.99h, 0.97h), grey * _ToneGrey);
    albedo *= lerp(half3(1.0h, 1.0h, 1.0h), half3(0.84h, 0.82h, 0.78h), saturate(dirt * _ToneDirt + wallDirt * 0.25h));
    albedo = lerp(albedo, min(albedo * 1.07h + 0.01h, 0.72h), bleach * _ToneBleach); // never above ~220 sRGB
#endif
    return albedo;
}

void InitializePlasterSurfaceData(float2 uvMetres, half3 vcol, out SurfaceData outSurfaceData)
{
    outSurfaceData = (SurfaceData)0;
    half blend = PlasterBlend(uvMetres, vcol);
    half patch = PatchMask(uvMetres);
    PlasterLayers L = SampleLayers(uvMetres);

    half3 texA = lerp(L.albedoA, dot(L.albedoA, half3(0.2126h, 0.7152h, 0.0722h)).xxx, _DesaturateA);
    half3 texB = lerp(L.albedoB, dot(L.albedoB, half3(0.2126h, 0.7152h, 0.0722h)).xxx, _DesaturateB);
    half3 albedo = lerp(texA * _BaseColor.rgb, texB * _BaseColorB.rgb, blend);
#if defined(_PATCHES)
    half rim = patch * (1.0h - patch) * 4.0h;
    albedo = lerp(albedo, albedo * _PatchTint.rgb, patch) * (1.0h - _PatchRim * rim);
#endif
    albedo = ApplyTone(albedo, uvMetres, vcol.b);
    albedo *= 1.0h - _VertexDarken * vcol.g;
    outSurfaceData.albedo = albedo;
    outSurfaceData.metallic = lerp(L.maosA.r, L.maosB.r, blend) * _Metallic;
    outSurfaceData.specular = half3(0.0h, 0.0h, 0.0h);
    outSurfaceData.smoothness = saturate(lerp(L.maosA.a * _Smoothness, L.maosB.a * _SmoothnessB, blend) + _PatchSmoothness * patch);
    outSurfaceData.occlusion = LerpWhiteTo(lerp(L.maosA.g, L.maosB.g, blend), _OcclusionStrength);
    outSurfaceData.normalTS = PlasterNormalTS(L, uvMetres, blend, patch);
    outSurfaceData.emission = half3(0.0h, 0.0h, 0.0h);
    outSurfaceData.alpha = 1.0h;
}

// Used by URP's LitMetaPass (lightmap baking has no vertex colours): layer A.
inline void InitializeStandardLitSurfaceData(float2 uv, out SurfaceData outSurfaceData)
{
    InitializePlasterSurfaceData(uv, half3(0.0h, 0.0h, 0.0h), outSurfaceData);
}

#endif
