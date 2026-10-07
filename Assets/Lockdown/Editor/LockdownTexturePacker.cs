using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Lockdown.EditorTools
{
    // Packs Poly Haven roughness / metal / AO / displacement maps into the formats URP wants:
    //  _MAOS  (URP Lit):         R metallic, G AO, B 0,      A smoothness (= 1 - roughness)
    //  _TMASK (URP Terrain Lit): R metallic, G AO, B height, A smoothness
    // _MAOS goes in both the Metallic slot (smoothness from Metallic alpha) and the Occlusion slot.
    // Select any map of a texture set (e.g. brick_wall_rough_2k) and run the menu item.
    public static class LockdownTexturePacker
    {
        const int MaxSize = 2048;

        [MenuItem("Assets/Lockdown/Pack URP Mask (MAOS)")]
        static void PackMaosMenu() => PackSelection(false);

        [MenuItem("Assets/Lockdown/Pack Terrain Mask (TMASK)")]
        static void PackTerrainMenu() => PackSelection(true);

        [MenuItem("Assets/Lockdown/Pack URP Mask (MAOS)", true)]
        [MenuItem("Assets/Lockdown/Pack Terrain Mask (TMASK)", true)]
        static bool ValidateSelection() => Selection.activeObject is Texture2D;

        static void PackSelection(bool terrain)
        {
            string output = Pack(AssetDatabase.GetAssetPath(Selection.activeObject), terrain);
            if (output != null) Selection.activeObject = AssetDatabase.LoadAssetAtPath<Texture2D>(output);
        }

        // Returns the packed texture's asset path, or null if the set has no roughness map.
        public static string Pack(string anyMapPath, bool terrain)
        {
            string dir = Path.GetDirectoryName(anyMapPath).Replace('\\', '/');
            if (!TryParse(Path.GetFileNameWithoutExtension(anyMapPath), out string baseName, out _))
            {
                Debug.LogError($"[Lockdown] Can't read a map type from {anyMapPath} (expected e.g. name_rough_2k).");
                return null;
            }

            Texture2D rough = null, metal = null, ao = null, arm = null, height = null;
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { dir }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetDirectoryName(path).Replace('\\', '/') != dir) continue;
                if (!TryParse(Path.GetFileNameWithoutExtension(path), out string b, out string map)) continue;
                if (!b.Equals(baseName, StringComparison.OrdinalIgnoreCase)) continue;

                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                switch (map)
                {
                    case "rough": rough ??= tex; break;
                    case "metal": metal ??= tex; break;
                    case "ao": ao ??= tex; break;
                    case "arm": arm ??= tex; break;
                    case "height": height ??= tex; break;
                }
            }

            if (rough == null && arm == null)
            {
                Debug.LogError($"[Lockdown] No roughness (_rough or _arm) map found for '{baseName}' in {dir}.");
                return null;
            }

            int w = 0, h = 0;
            foreach (var t in new[] { rough, metal, ao, arm, height })
            {
                if (t == null) continue;
                WarnIfSrgb(t);
                w = Mathf.Max(w, t.width);
                h = Mathf.Max(h, t.height);
            }
            w = Mathf.Min(w, MaxSize);
            h = Mathf.Min(h, MaxSize);

            // _arm: R = AO, G = roughness, B = metal. Separate maps win over it (so it is only read if one is missing).
            Color[] armPx = rough != null && metal != null && ao != null ? null : Read(arm, w, h);
            Color[] roughPx = Read(rough, w, h);
            Color[] metalPx = Read(metal, w, h);
            Color[] aoPx = Read(ao, w, h);
            Color[] heightPx = terrain ? Read(height, w, h) : null;

            var outPx = new Color[w * h];
            for (int i = 0; i < outPx.Length; i++)
            {
                float r = roughPx != null ? roughPx[i].r : armPx[i].g;
                float m = metalPx != null ? metalPx[i].r : armPx != null ? armPx[i].b : 0f;
                float o = aoPx != null ? aoPx[i].r : armPx != null ? armPx[i].r : 1f;
                float hgt = terrain ? (heightPx != null ? heightPx[i].r : 0.5f) : 0f;
                outPx[i] = new Color(m, o, hgt, 1f - r);
            }

            var outTex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            outTex.SetPixels(outPx);
            outTex.Apply();
            string outPath = $"{dir}/{baseName}_{(terrain ? "TMASK" : "MAOS")}.png";
            File.WriteAllBytes(outPath, outTex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(outTex);
            AssetDatabase.ImportAsset(outPath);

            Debug.Log($"[Lockdown] Packed {outPath} ({w}x{h}) from" +
                      $"{(rough ? " rough" : "")}{(metal ? " metal" : "")}{(ao ? " ao" : "")}{(arm ? " arm" : "")}{(terrain && height ? " height" : "")}.");
            return outPath;
        }

        // Splits "brick_wall_02_rough_2k" into base "brick_wall_02" and map "rough".
        static bool TryParse(string fileName, out string baseName, out string map)
        {
            string[] parts = fileName.Split('_');
            for (int i = parts.Length - 1; i > 0; i--)
            {
                map = Normalize(parts[i].ToLowerInvariant());
                if (map == null) continue;
                baseName = string.Join("_", parts, 0, i);
                return true;
            }
            baseName = map = null;
            return false;
        }

        static string Normalize(string token)
        {
            switch (token)
            {
                case "rough": case "roughness": return "rough";
                case "metal": case "metallic": case "metalness": return "metal";
                case "ao": case "occlusion": return "ao";
                case "arm": return "arm";
                case "disp": case "displacement": case "height": return "height";
                case "diff": case "diffuse": case "albedo": case "col": case "color": case "basecolor":
                case "nor": case "normal": case "nrm": case "spec": case "mask": case "maos": case "tmask":
                    return "other";
                default: return null;
            }
        }

        static void WarnIfSrgb(Texture2D tex)
        {
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) as TextureImporter;
            if (importer != null && importer.sRGBTexture)
                Debug.LogWarning($"[Lockdown] {tex.name} is imported as sRGB; data maps must be linear, values will be off.");
        }

        // Reads a texture's raw values at the given size through a linear GPU blit,
        // so any imported format (JPG, PNG, EXR, compressed) works.
        static Color[] Read(Texture2D tex, int w, int h)
        {
            if (tex == null) return null;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var read = new Texture2D(w, h, TextureFormat.RGBAHalf, false, true);
            read.ReadPixels(new Rect(0, 0, w, h), 0, 0); // CPU copy; GetPixels reads it, no GPU upload (Apply) needed
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            Color[] px = read.GetPixels();
            UnityEngine.Object.DestroyImmediate(read);
            return px;
        }
    }
}
