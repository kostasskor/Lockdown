using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Lockdown.EditorTools
{
    // Renders a (usually disabled) review camera to a 1920x1080 PNG in <project>/Screenshots/,
    // which is never imported or committed.
    public static class LockdownCapture
    {
        // In this editor the readback lags one render behind (each capture used to return the previous
        // camera's frame), so every camera is rendered twice and the second frame is kept.
        public static string Capture(string cameraName, string fileName, int width = 1920, int height = 1080)
        {
            CaptureOnce(cameraName, null, width, height);
            return CaptureOnce(cameraName, fileName, width, height);
        }

        static string CaptureOnce(string cameraName, string fileName, int width, int height)
        {
            Camera cam = null;
            foreach (var c in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (c.name == cameraName) { cam = c; break; }
            if (cam == null) { Debug.LogError($"[Lockdown] Camera '{cameraName}' not found."); return null; }

            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;
            rt.Create();
            // URP's render request renders this camera straight into rt. (Camera.Render() can hand back the
            // previous camera's frame once another tool or a debug mode has rendered in between.)
            var request = new UniversalRenderPipeline.SingleCameraRequest { destination = rt };
            if (RenderPipeline.SupportsRenderRequest(cam, request)) RenderPipeline.SubmitRenderRequest(cam, request);
            else { cam.targetTexture = rt; cam.Render(); }
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            cam.targetTexture = prevTarget;
            RenderTexture.active = prevActive;

            if (fileName == null)
            {
                Object.DestroyImmediate(tex);
                rt.Release();
                Object.DestroyImmediate(rt);
                return null;
            }
            string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Screenshots");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            rt.Release();
            Object.DestroyImmediate(rt);
            return path;
        }
    }
}
