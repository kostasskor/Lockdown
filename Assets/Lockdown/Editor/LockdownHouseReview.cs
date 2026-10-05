using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Lockdown.EditorTools
{
    // Review cameras that reproduce the review walk-through screenshots of the test house (walker eye height,
    // BO2 FOV), plus jittered capture sequences for spotting flicker (z-fighting, decal sort swaps).
    public static class LockdownHouseReview
    {
        const float Eye = 1.6f, FloorEye = 0.25f + 1.6f, TerraceEye = 3.05f + 1.6f;

        static readonly (string name, Vector3 pos, Vector3 target)[] UserViews =
        {
            ("UserView_01", new Vector3(4.6f, Eye, 11.9f), new Vector3(4.6f, 3.6f, 7f)),           // back wall, frontal
            ("UserView_02", new Vector3(16.5f, Eye, 2.5f), new Vector3(9f, 3.2f, 1.5f)),           // east side, stair wall
            ("UserView_03", new Vector3(11.5f, Eye, -6.5f), new Vector3(5f, 3.3f, 1f)),                // south-east corner, low
            ("UserView_04", new Vector3(4.8f, Eye, -10.5f), new Vector3(4.8f, 3.2f, 0f)),          // front, frontal
            ("UserView_05", new Vector3(6.2f, FloorEye, -1.5f), new Vector3(9f, 1.3f, -0.4f)),     // porch: dark doorway
            ("UserView_06", new Vector3(6.8f, FloorEye, -1.6f), new Vector3(9.1f, 1.2f, -0.6f)),   // porch: dark doorway, closer
            ("UserView_07", new Vector3(3.4f, FloorEye, -2.9f), new Vector3(0.6f, 1.2f, -0.3f)),   // porch: west inside corner
            ("UserView_08", new Vector3(-5f, Eye, 0.5f), new Vector3(0f, 1.5f, -0.5f)),            // west wall, low, by the small window
            ("UserView_09", new Vector3(-8f, Eye, -0.5f), new Vector3(0f, 1.3f, 0f)),              // west wall, further
            ("UserView_10", new Vector3(-3f, Eye, 10f), new Vector3(1f, 5f, 6f)),                  // north-west corner, looking up
            ("UserView_11", new Vector3(7.2f, TerraceEye, 0.9f), new Vector3(2f, 3.9f, -1.2f)),    // terrace, quoins
            ("UserView_12", new Vector3(9.8f, TerraceEye, 1.0f), new Vector3(9.8f, 2f, -3.5f)),    // top of the stair, looking down
            ("UserView_13", new Vector3(7.6f, FloorEye, -1.5f), new Vector3(9.0f, 0.9f, 0f)),      // porch: corner right of the dark doorway, close
            ("UserView_14", new Vector3(5.6f, TerraceEye, -0.6f), new Vector3(9.8f, 3.6f, 0.6f)),  // terrace: looking at the stair top / landing
            // Hand-built pass close-ups
            ("UserView_15", new Vector3(-2.5f, Eye, 9.5f), new Vector3(0f, 1.8f, 7f)),             // north-west convex corner, close
            ("UserView_16", new Vector3(8.0f, TerraceEye, -0.6f), new Vector3(8.6f, 5.75f, 0.4f)),  // pergola from the terrace, looking up
            ("UserView_17", new Vector3(8.0f, TerraceEye, 0.4f), new Vector3(8.5f, 3.4f, -1.9f)),   // terrace railing, close
            ("UserView_18", new Vector3(2.6f, Eye, -6.5f), new Vector3(2.6f, 4.0f, -1f)),           // balcony slab, parapet and upper windows from the street
            ("UserView_19", new Vector3(12.5f, Eye, 4.6f), new Vector3(9.2f, 2.9f, 4.6f)),          // east wall windows, close
            ("UserView_20", new Vector3(-1.1f, Eye, 8.3f), new Vector3(0f, 1.5f, 7f)),              // north-west corner at arm's length
            ("UserView_21", new Vector3(-0.9f, Eye, 9.5f), new Vector3(0.1f, 2.6f, 0f)),            // west wall in grazing sun (bumps, patches, lean)
            // Doors and windows round (user screenshots, 2026-10-05)
            ("UserView_22", new Vector3(4.3f, TerraceEye, -1.6f), new Vector3(1.4f, 4.3f, 0.05f)),  // balcony: aluminium door + louvred window, oblique
            ("UserView_23", new Vector3(6.4f, TerraceEye, -1.1f), new Vector3(7.0f, 4.2f, 1.2f)),   // terrace: brown glazed door + window
            ("UserView_24", new Vector3(3.5f, TerraceEye, -1.7f), new Vector3(2.3f, 4.2f, 0.05f)), // balcony: aluminium door, close
            ("UserView_25", new Vector3(8.2f, FloorEye, -2.4f), new Vector3(8.2f, 1.3f, 0.5f)),     // porch: open doorway, frontal
            ("UserView_26", new Vector3(-1.5f, Eye, -7f), new Vector3(2.8f, 4.3f, 0f)),              // street: facade glass at a slant (reflections)
        };

        // Views with flicker marked in the review screenshots.
        public static readonly string[] FlickerViews = { "UserView_05", "UserView_06", "UserView_07", "UserView_08", "UserView_10" };

        public static void AddUserViews(GameObject parent)
        {
            foreach (var (name, pos, target) in UserViews)
            {
                var go = new GameObject(name);
                go.transform.SetParent(parent.transform, false);
                go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(target - pos, Vector3.up));
                var cam = go.AddComponent<Camera>();
                cam.fieldOfView = 64f; // the walker's BO2 FOV
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 2000f;
                cam.depth = -10f;
                cam.enabled = false;
                var data = go.AddComponent<UniversalAdditionalCameraData>();
                data.renderPostProcessing = true;
                data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            }
        }

        public static void CaptureUserViews(string prefix)
        {
            foreach (var (name, _, _) in UserViews)
                LockdownCapture.Capture(name, $"{prefix}_{name.Substring(name.Length - 2)}.png");
        }

        // Each wall face seen straight on (orthographic, like an elevation drawing), to look for repeated stains or a
        // mirrored half. The near plane sits just in front of the face, so balconies and columns don't hide it.
        public static void CaptureWallFaces(string prefix, HouseSpec spec)
        {
            foreach (var w in spec.walls)
            {
                bool alongX = w.facing == Facing.South || w.facing == Facing.North;
                Vector3 outward = w.facing switch
                {
                    Facing.South => Vector3.back, Facing.North => Vector3.forward, Facing.West => Vector3.left, _ => Vector3.right,
                };
                float mid = (w.min + w.max) * 0.5f, len = w.max - w.min, h = w.y1 - w.y0;
                Vector3 c = alongX ? new Vector3(mid, (w.y0 + w.y1) * 0.5f, w.plane) : new Vector3(w.plane, (w.y0 + w.y1) * 0.5f, mid);
                const float dist = 30f;
                var go = new GameObject("WallCam_" + w.name) { tag = "EditorOnly" };
                go.transform.SetPositionAndRotation(c + outward * dist, Quaternion.LookRotation(-outward, Vector3.up));
                var cam = go.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = h * 0.5f * 1.04f;
                cam.nearClipPlane = dist - 0.6f;
                cam.farClipPlane = dist + 1f;
                cam.enabled = false;
                go.AddComponent<UniversalAdditionalCameraData>().renderPostProcessing = true;
                int width = 1600, height = Mathf.Clamp(Mathf.RoundToInt(width * h * 1.04f / (len * 1.04f)), 240, 1600);
                if (w == spec.walls[0]) LockdownCapture.Capture(go.name, null, width, height); // the first readback of a batch can be stale
                LockdownCapture.Capture(go.name, $"walls/{prefix}_{w.name}.png", width, height);
                Object.DestroyImmediate(go);
            }
        }

        // Every pot/plant prop in the scene from outside the house at 0.8 m (looking down into the pot), 2 m and 6 m
        // (where LOD1 takes over), for before/after comparisons of the game-ready props.
        public static void CaptureProps(string prefix)
        {
            var review = GameObject.Find("_Review");
            if (review == null) return;
            int i = 0;
            foreach (Transform t in review.transform)
            {
                if (!(t.name.Contains("potted") || t.name.Contains("planter") || t.name.Contains("Plant") || t.name.Contains("Pot"))) continue;
                var rs = t.GetComponentsInChildren<Renderer>();
                if (rs.Length == 0) continue;
                var b = rs[0].bounds;
                foreach (var r in rs) b.Encapsulate(r.bounds);
                Vector3 c = b.center, away = c.x < 0f ? Vector3.left : Vector3.back;
                float ground = t.position.y;
                i++;
                foreach (var (label, dist, height) in new[] { ("near", 0.8f, b.max.y + 0.45f), ("mid", 2f, ground + 1.6f), ("far", 6f, ground + 1.6f) })
                {
                    var go = new GameObject("PropCam") { tag = "EditorOnly" };
                    Vector3 pos = c + away * dist;
                    pos.y = height;
                    go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(c - pos, Vector3.up));
                    var cam = go.AddComponent<Camera>();
                    // Far uses the player FOV at full HD (LOD choice depends on screen size); crop it afterwards.
                    cam.fieldOfView = label == "far" ? 64f : 50f;
                    cam.nearClipPlane = 0.05f;
                    cam.enabled = false;
                    var data = go.AddComponent<UniversalAdditionalCameraData>();
                    data.renderPostProcessing = true;
                    data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                    if (label == "far") LockdownCapture.Capture(go.name, $"props/{prefix}_{i}_{label}.png");
                    else LockdownCapture.Capture(go.name, $"props/{prefix}_{i}_{label}.png", 1280, 960);
                    Object.DestroyImmediate(go);
                }
            }
        }

        // One prop at one distance, from eye height at the player FOV in full HD, rendered once with LOD0 and once with
        // LOD1 forced (props/lod_{prop}_{distance}m_{0|1}.png). Diffing the pairs over a sweep of distances gives the
        // nearest distance where LOD1 is indistinguishable: that is where the prefab switches (LockdownPropBuilder).
        public static void CaptureLodPair(string prop, float distance)
        {
            var review = GameObject.Find("_Review");
            Transform t = null;
            if (review != null)
                foreach (Transform child in review.transform)
                    if (child.name.StartsWith(prop)) t = child;
            var group = t != null ? t.GetComponentInChildren<LODGroup>() : null;
            if (group == null) { Debug.LogWarning($"[Lockdown] No {prop} with a LODGroup under _Review."); return; }
            var b = group.GetComponentsInChildren<Renderer>()[0].bounds;
            foreach (var r in group.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
            Vector3 c = b.center, away = c.x < 0f ? Vector3.left : Vector3.back;
            var go = new GameObject("PropCam") { tag = "EditorOnly" };
            Vector3 pos = c + away * distance;
            pos.y = t.position.y + 1.6f;
            go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(c - pos, Vector3.up));
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = 64f;
            cam.nearClipPlane = 0.05f;
            cam.enabled = false;
            var data = go.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            try
            {
                for (int lod = 0; lod < 2; lod++)
                {
                    group.ForceLOD(lod);
                    LockdownCapture.Capture(go.name, $"props/lod_{prop}_{distance:0}m_{lod}.png");
                }
            }
            finally
            {
                group.ForceLOD(-1);
                Object.DestroyImmediate(go);
            }
        }

        // Walks a capsule the size of the player's (copied from the Walker prefab) along a route over the house:
        // street, up the stair, onto the landing and the terrace, along the balcony, back down, onto the porch.
        // A stretch fails if the capsule stops making progress (blocked, or would need a jump).
        static readonly (string name, Vector3 target)[] WalkRoute =
        {
            ("street to the stair foot", new Vector3(9.83f, 0f, -5.4f)),
            ("up the stair", new Vector3(9.83f, 3.05f, -0.15f)),
            ("onto the landing", new Vector3(9.83f, 3.05f, 0.6f)),
            ("landing onto the terrace", new Vector3(8.2f, 3.07f, 0.6f)),
            ("along the balcony", new Vector3(1.5f, 3.07f, -1.0f)),
            ("to the balcony door", new Vector3(3.15f, 3.07f, -0.6f)),
            ("in through the balcony door", new Vector3(3.15f, 3.07f, 1.2f)),
            ("across the upper floor", new Vector3(6.65f, 3.07f, 2.2f)),
            ("out through the brown door", new Vector3(6.65f, 3.07f, 0.6f)),
            ("back to the landing", new Vector3(9.83f, 3.05f, 0.6f)),
            ("down the stair", new Vector3(9.83f, 0f, -5.4f)),
            ("street to the porch front", new Vector3(5.5f, 0f, -3.2f)),
            ("onto the porch", new Vector3(4.0f, 0.25f, -1.0f)),
            ("to the porch doorway", new Vector3(8.2f, 0.25f, -0.6f)),
            ("into the ground floor", new Vector3(8.2f, 0.25f, 1.5f)),
            ("across the ground floor", new Vector3(3.0f, 0.25f, 4.0f)),
            ("back out to the porch", new Vector3(8.2f, 0.25f, 1.0f)),
            ("out through the doorway", new Vector3(8.2f, 0.25f, -0.8f)),
        };

        public static List<string> WalkTest()
        {
            var lines = new List<string>();
            var go = new GameObject("WalkTester");
            var cc = go.AddComponent<CharacterController>();
            var walker = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Lockdown/Prefabs/Player/Walker.prefab");
            var src = walker != null ? walker.GetComponent<CharacterController>() : null;
            if (src != null)
            {
                cc.radius = src.radius; cc.height = src.height; cc.center = src.center;
                cc.stepOffset = src.stepOffset; cc.slopeLimit = src.slopeLimit; cc.skinWidth = src.skinWidth;
            }
            go.transform.position = new Vector3(9.83f, 0.05f, -7f);
            Physics.SyncTransforms();
            const float dt = 0.02f, speed = 3.5f;
            float vy = 0f;
            try
            {
                foreach (var (name, target) in WalkRoute)
                {
                    bool reached = false;
                    float stuckTime = 0f, t = 0f;
                    Vector3 last = go.transform.position;
                    while (t < 30f)
                    {
                        Vector3 p = go.transform.position;
                        Vector3 flat = new Vector3(target.x - p.x, 0f, target.z - p.z);
                        if (flat.magnitude < 0.2f) { reached = true; break; }
                        vy = cc.isGrounded ? -2f : vy - 9.81f * dt;
                        Vector3 step = flat.normalized * speed * dt;
                        cc.Move(new Vector3(step.x, vy * dt, step.z));
                        t += dt;
                        if ((go.transform.position - last).magnitude < speed * dt * 0.2f) stuckTime += dt; else stuckTime = 0f;
                        last = go.transform.position;
                        if (stuckTime > 1f) break;
                    }
                    Vector3 e = go.transform.position;
                    lines.Add($"{(reached ? "PASS" : "FAIL")}  {name}  ({t:0.0} s, ended at {e.x:0.00}, {e.y:0.00}, {e.z:0.00})");
                    if (!reached) break;
                }
            }
            finally { Object.DestroyImmediate(go); }
            return lines;
        }

        // Moves the camera a few millimetres per frame, the way a player's view drifts: z-fighting and
        // decal sort swaps flicker under that motion while real detail barely changes.
        public static void CaptureFlicker(string prefix, int frames = 8, float step = 0.003f, float degStep = 0.05f, string[] views = null)
        {
            foreach (var name in views ?? FlickerViews)
            {
                Camera cam = null;
                foreach (var c in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (c.name == name) cam = c;
                if (cam == null) continue;
                var t = cam.transform;
                Vector3 p0 = t.position;
                Quaternion r0 = t.rotation;
                for (int i = 0; i < frames; i++)
                {
                    t.SetPositionAndRotation(p0 + t.right * (step * i) + Vector3.up * (step * 0.5f * i), r0 * Quaternion.Euler(0f, degStep * i, 0f));
                    LockdownCapture.Capture(name, $"flicker/{prefix}_{name.Substring(name.Length - 2)}_{i}.png", 960, 540);
                }
                t.SetPositionAndRotation(p0, r0);
            }
        }
    }
}
