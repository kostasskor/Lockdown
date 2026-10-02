using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Lockdown.EditorTools
{
    // Sets every texture under Assets/Lockdown/ correctly on import, by file name and folder:
    // color maps sRGB, data maps linear, OpenGL normal maps, size caps, anisotropic filtering
    // on surfaces seen at grazing angles. Rules: brief Phase 0, texture import rules.
    public class LockdownTextureImporter : AssetPostprocessor
    {
        const string Root = "Assets/Lockdown/";

        public enum MapKind { Unknown, Color, Normal, NormalDX, Data }

        static readonly string[] ColorTokens = { "diff", "diffuse", "albedo", "col", "color", "basecolor" };
        static readonly string[] NormalTokens = { "nor", "normal", "nrm" };
        static readonly string[] DataTokens =
        {
            "rough", "roughness", "metal", "metallic", "ao", "occlusion", "arm",
            "disp", "displacement", "height", "mask", "spec", "maos", "tmask",
        };

        // Surfaces seen at grazing angles: walls, ground, roads.
        static readonly string[] GrazingWords =
        {
            "ground", "road", "wall", "terrain", "cobble", "asphalt", "dirt", "rock", "stone",
            "plaster", "pavement", "paving", "tile", "floor", "gravel", "concrete", "brick",
            "soil", "path",
        };

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) return;
            var importer = (TextureImporter)assetImporter;
            if (importer.textureType == TextureImporterType.Sprite ||
                importer.textureType == TextureImporterType.GUI) return;

            string name = Path.GetFileNameWithoutExtension(assetPath);
            switch (Classify(name))
            {
                case MapKind.Color:
                    importer.textureType = TextureImporterType.Default;
                    importer.sRGBTexture = true;
                    break;
                case MapKind.NormalDX:
                    Debug.LogWarning($"[Lockdown] {assetPath} is a DirectX normal map: its bumps light the wrong way in Unity. Use the _nor_gl version.");
                    importer.textureType = TextureImporterType.NormalMap;
                    break;
                case MapKind.Normal:
                    importer.textureType = TextureImporterType.NormalMap;
                    break;
                case MapKind.Data:
                    importer.textureType = TextureImporterType.Default;
                    importer.sRGBTexture = false;
                    break;
            }

            importer.mipmapEnabled = true;
            importer.maxTextureSize = MaxSizeFor(assetPath);

            if (IsGrazingSurface(assetPath))
            {
                importer.anisoLevel = 8;
                importer.filterMode = FilterMode.Trilinear;
            }
        }

        // The map type is the last map token in the name, so "metal_plate_diff_2k" is a color map.
        public static MapKind Classify(string fileName)
        {
            string[] tokens = fileName.ToLowerInvariant().Split('_', '-', ' ', '.');
            for (int i = tokens.Length - 1; i >= 0; i--)
            {
                string t = tokens[i];
                if (Array.IndexOf(NormalTokens, t) >= 0)
                    return Array.IndexOf(tokens, "dx") >= 0 ? MapKind.NormalDX : MapKind.Normal;
                if (Array.IndexOf(DataTokens, t) >= 0) return MapKind.Data;
                if (Array.IndexOf(ColorTokens, t) >= 0) return MapKind.Color;
            }
            return MapKind.Unknown;
        }

        // 2K by default, 1K inside a "Props" folder, 512 inside a "SmallProps" folder.
        static int MaxSizeFor(string path)
        {
            string[] folders = Path.GetDirectoryName(path).Replace('\\', '/').Split('/');
            if (Array.Exists(folders, f => f.Equals("SmallProps", StringComparison.OrdinalIgnoreCase))) return 512;
            if (Array.Exists(folders, f => f.Equals("Props", StringComparison.OrdinalIgnoreCase))) return 1024;
            return 2048;
        }

        static bool IsGrazingSurface(string path)
        {
            string p = path.Substring(Root.Length).ToLowerInvariant();
            return Array.Exists(GrazingWords, w => p.Contains(w));
        }
    }
}
