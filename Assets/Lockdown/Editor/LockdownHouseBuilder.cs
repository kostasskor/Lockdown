using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using static Lockdown.EditorTools.LockdownEditorUtil;

namespace Lockdown.EditorTools
{
    // Turns a HouseSpec into mesh assets (LOD0, LOD1, collision) and a prefab with a LODGroup.
    // Idempotent: re-running overwrites the same mesh assets in place (GUIDs kept) and re-saves the prefab.
    // Materials are created once with placeholder colours and never reset here, so textured materials survive rebuilds.
    public static partial class LockdownHouseBuilder
    {
        const string MeshDir = "Assets/Lockdown/Models/Generated/Houses";
        const string PrefabDir = "Assets/Lockdown/Prefabs/Houses";
        const string MatDir = "Assets/Lockdown/Materials/Houses";
        const string ScenePath = "Assets/Lockdown/Scenes/Review_TestHouse.unity";
        const string EditorOnly = "EditorOnly";

        // ---------- Menu ----------

        [MenuItem("Lockdown/Build/Test House")]
        public static void BuildTestHouse() => Build(HouseSpecs.BlueTrimWhite());

        [MenuItem("Lockdown/Review/Build Test House Scene")]
        public static void BuildTestHouseScene()
        {
            if (EditorApplication.isPlaying) { Debug.LogError("[Lockdown] Exit Play mode first."); return; }
            const string houseId = "House_BlueTrimWhite";
            string prefabPath = $"{PrefabDir}/{houseId}.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null) BuildTestHouse();

            EditorSceneManager.SaveOpenScenes();
            if (!File.Exists(ScenePath))
            {
                var created = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                var templateCam = GameObject.Find("Main Camera");
                if (templateCam != null) Object.DestroyImmediate(templateCam);
                EditorSceneManager.SaveScene(created, ScenePath);
            }
            else if (EditorSceneManager.GetActiveScene().path != ScenePath)
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var scene = EditorSceneManager.GetActiveScene();

            SetupSun();
            SetupSky();
            SetupReviewVolume();

            // The house itself (a prefab instance; rebuilding the prefab updates it).
            if (GameObject.Find(houseId) == null)
            {
                var house = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath), scene);
                house.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity); // street facade faces south, like the village's main street
            }

            // Walker for walking it at player scale.
            if (Object.FindFirstObjectByType<Lockdown.LockdownWalker>() == null)
            {
                var walkerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Lockdown/Prefabs/Player/Walker.prefab");
                if (walkerPrefab != null)
                {
                    var walker = (GameObject)PrefabUtility.InstantiatePrefab(walkerPrefab, scene);
                    walker.transform.SetPositionAndRotation(new Vector3(-4f, 0.05f, -9f), Quaternion.Euler(0f, 25f, 0f));
                }
            }

            // Review helpers (EditorOnly): ground, scale dummies, photo-matching cameras.
            GameObject review = GameObject.Find("_Review") ?? new GameObject("_Review");
            foreach (Transform child in review.transform.Cast<Transform>().ToList()) Object.DestroyImmediate(child.gameObject);

            // A patch of village street: worn asphalt, the porch kerb is part of the house.
            var groundMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Lockdown/Materials/Ground/M_Street.mat");
            var streetPart = new MeshPart(); // metre UVs, so the asphalt tiles at its real scale
            streetPart.Box(new Vector3(5f, -0.1f, 0f), new Vector3(60f, 0.2f, 60f), Quaternion.identity, Slot.Render);
            EnsureFolder("Assets/Lockdown/Models/Generated/ReviewScenes");
            var streetMesh = SaveMesh(streetPart.ToMesh("Review_Street", out _), "Assets/Lockdown/Models/Generated/ReviewScenes/Review_Street.asset");
            var ground = new GameObject("Street");
            ground.transform.SetParent(review.transform, false);
            ground.AddComponent<MeshFilter>().sharedMesh = streetMesh;
            ground.AddComponent<MeshRenderer>().sharedMaterial = groundMat;
            ground.AddComponent<MeshCollider>().sharedMesh = streetMesh;

            // Potted plants against the walls (02, 03).
            PlaceProp(review, "potted_plant_02", new Vector3(-0.45f, 0f, 1.6f), 70f, 1.2f);
            PlaceProp(review, "potted_plant_01", new Vector3(3.0f, 0.27f, -0.45f), 200f, 1.0f);

            // Reflection probe so glass and smooth paint pick up the real sky.
            var probeGo = new GameObject("ReflectionProbe");
            probeGo.transform.SetParent(review.transform, false);
            probeGo.transform.localPosition = new Vector3(4.5f, 3f, -4f);
            var probe = probeGo.AddComponent<ReflectionProbe>();
            probe.size = new Vector3(40f, 20f, 40f);
            probe.resolution = 512;
            probe.boxProjection = true; // reflections in the facade glass line up with the street, not with infinity
            // Baked to a cubemap (a realtime probe has no texture in edit mode, which breaks offscreen captures).
            // Review-only baked data: gitignored, regenerated here.
            probe.mode = ReflectionProbeMode.Custom;
            const string probePath = "Assets/Lockdown/Textures/Review/ReviewProbe_TestHouse.exr";
            if (Lightmapping.BakeReflectionProbe(probe, probePath))
                probe.customBakedTexture = AssetDatabase.LoadAssetAtPath<Texture>(probePath);

            var cams = new GameObject("Cameras");
            cams.transform.SetParent(review.transform, false);
            ReviewCamera(cams, "Cam_Photo01", new Vector3(11.5f, 1.9f, -5.8f), new Vector3(3.5f, 2.6f, -0.5f), 60f);
            ReviewCamera(cams, "Cam_Photo02", new Vector3(-6f, 2.4f, -9.5f), new Vector3(2.5f, 3.0f, 0f), 58f);
            ReviewCamera(cams, "Cam_Photo03", new Vector3(5.0f, 2.3f, -7.3f), new Vector3(5.0f, 3.4f, 0f), 60f);
            ReviewCamera(cams, "Cam_Rear34", new Vector3(17f, 7f, 13f), new Vector3(4.5f, 3f, 3f), 50f);
            ReviewCamera(cams, "Cam_SunSide", new Vector3(-11f, 1.7f, 1.5f), new Vector3(0f, 3.2f, 2.5f), 64f);
            LockdownHouseReview.AddUserViews(cams);

            foreach (var t in review.GetComponentsInChildren<Transform>(true)) t.gameObject.tag = EditorOnly;
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[Lockdown] Test house review scene ready: " + ScenePath);
            var walk = LockdownHouseReview.WalkTest();
            bool ok = walk.TrueForAll(l => l.StartsWith("PASS"));
            if (ok) Debug.Log("[Lockdown] Walk test: all stretches PASS\n  " + string.Join("\n  ", walk));
            else Debug.LogWarning("[Lockdown] Walk test FAILED\n  " + string.Join("\n  ", walk));
        }

        // ---------- Build ----------

        public static readonly Dictionary<string, List<string>> LastAudit = new();
        public static Dictionary<string, int> LastTrisByPart = new(); // LOD0 triangles of the warped parts, per part

        public static void Build(HouseSpec spec)
        {
            EnsureFolder(MeshDir);
            EnsureFolder(PrefabDir);
            EnsureFolder(MatDir);

            // Hand-built imperfections: openings jittered, pergola cut by hand, chips/cracks/nail stains placed, and
            // the whole rendered house taken out of true by one smooth field (collision stays straight boxes).
            var imp = spec.imperfections != null && spec.imperfections.enabled ? spec.imperfections : null;
            int seed = spec.seed != 0 ? spec.seed : Seed(spec.id);
            if (imp != null) ApplyImperfections(spec, imp, seed);
            // No repetition or symmetry: stains placed by logic, openings dressed differently, every wall plane its own
            // texture offset (so no two walls show the same patch), all from the same per-house seed.
            ApplyWeathering(spec, imp, seed);
            DressInteriors(spec, seed);

            var hm = new HouseMesh();
            hm.UvSeed = seed;
            Rooms.Clear();
            if (imp != null) hm.SetWarp(new WarpField(imp, seed), imp);
            foreach (var w in spec.walls) BuildWall(hm, w, spec, imp);
            foreach (var b in spec.boxes)
            {
                hm.Tag = b.name;
                hm.Detail = b.detail;
                hm.Profile = imp != null ? BoxSag(spec, b, imp) : default;
                if (b.chamfer > 0f) hm.ChamferBox(b.min, b.max, b.chamfer, b.slot, b.collide);
                else hm.Box(b.min, b.max, b.slot, b.collide);
            }
            hm.Profile = default;
            foreach (var c in spec.cylinders) { hm.Tag = c.name; hm.Detail = c.detail; hm.Cylinder(c.a, c.b, c.radius, c.slot); }
            hm.Detail = false;
            foreach (var q in spec.quoins) BuildQuoins(hm, q);
            foreach (var st in spec.stairs) BuildStair(hm, st);
            foreach (var r in spec.railings) BuildRailing(hm, r, imp);
            foreach (var p in spec.pergolas) BuildPergola(hm, p, imp);
            foreach (var p in spec.prisms) { hm.Tag = p.name; hm.Detail = false; hm.PrismX(p.x0, p.x1, p.zy, p.slot, true); }
            LastTrisByPart = hm.Lod0.TrisByPart;
            if (imp != null)
            {
                float dy = Mathf.Max(hm.Lod0.MaxWalkableDy, hm.Lod1.MaxWalkableDy);
                if (dy > 1e-5f) Debug.LogWarning($"[Lockdown] {spec.id}: walkable tiles moved vertically by {dy * 1000f:0.0} mm (must be 0).");
                else Debug.Log($"[Lockdown] {spec.id}: imperfections on, walkable tiles flat (max vertical move 0 mm).");
            }

            // Z-fighting audit: same-facing coplanar faces from different parts must never overlap.
            foreach (var (label, part) in new[] { ("LOD0", hm.Lod0), ("LOD1", hm.Lod1) })
            {
                var overlaps = LockdownMeshAudit.FindCoplanarOverlaps(part);
                LastAudit[label] = overlaps;
                if (overlaps.Count == 0) Debug.Log($"[Lockdown] {spec.id} {label} audit: 0 coplanar overlaps.");
                else Debug.LogWarning($"[Lockdown] {spec.id} {label} audit: {overlaps.Count} coplanar overlaps:\n  " + string.Join("\n  ", overlaps));
            }

            Mesh m0 = SaveMesh(hm.Lod0.ToMesh(spec.id + "_LOD0", out var slots0), $"{MeshDir}/{spec.id}_LOD0.asset");
            Mesh m1 = SaveMesh(hm.Lod1.ToMesh(spec.id + "_LOD1", out var slots1), $"{MeshDir}/{spec.id}_LOD1.asset");
            Mesh mc = SaveMesh(hm.Collision.ToMesh(spec.id + "_Collision", out _), $"{MeshDir}/{spec.id}_Collision.asset");

            var root = new GameObject(spec.id);
            var r0 = AddRenderer(root, "LOD0", m0, slots0);
            var r1 = AddRenderer(root, "LOD1", m1, slots1);
            var colGo = new GameObject("Collision");
            colGo.transform.SetParent(root.transform, false);
            colGo.AddComponent<MeshCollider>().sharedMesh = mc;
            BuildDecals(root, spec);
            var lodGroup = root.AddComponent<LODGroup>();
            lodGroup.SetLODs(new[] { new LOD(0.12f, new Renderer[] { r0 }), new LOD(0.012f, new Renderer[] { r1 }) });
            lodGroup.RecalculateBounds();
            var flags = StaticEditorFlags.ContributeGI | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic;
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags);

            string prefabPath = $"{PrefabDir}/{spec.id}.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Lockdown] {spec.id}: LOD0 {m0.vertexCount} verts / {m0.subMeshCount} materials, LOD1 {m1.vertexCount} verts, collision {mc.vertexCount} verts -> {prefabPath}");
        }

        const float DecalCornerInset = 0.02f;

        static void BuildDecals(GameObject root, HouseSpec spec)
        {
            if (spec.decals.Count == 0) return;
            var parent = new GameObject("Decals");
            parent.transform.SetParent(root.transform, false);
            foreach (var d in spec.decals)
            {
                Vector3 outward = d.direction != Vector3.zero ? d.direction.normalized : d.facing switch
                {
                    Facing.South => Vector3.back, Facing.North => Vector3.forward, Facing.West => Vector3.left, _ => Vector3.right,
                };
                var go = new GameObject(d.name);
                go.transform.SetParent(parent.transform, false);
                go.transform.SetLocalPositionAndRotation(d.center, Quaternion.LookRotation(-outward, Vector3.up) * Quaternion.Euler(0f, 0f, d.roll));
                var dp = go.AddComponent<DecalProjector>();
                dp.material = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/M_Decal_{d.kind}{(d.layer == 1 ? "_B" : "")}.mat");
                // Stop 2 cm short of the facade's ends, so no projector box ends exactly on a perpendicular face
                // (pixels on that plane would fall in and out of the box as the camera moves).
                float width = Mathf.Max(0.05f, d.width - 2f * DecalCornerInset);
                dp.size = new Vector3(width, d.height, d.depth);
                dp.pivot = Vector3.zero;
                // Square texels: one texture repeat spans height x aspect metres across. Atlas decals (chips,
                // cracks) show one of their 4 cells instead; flips mirror a crack to the corner it starts from.
                var tex = dp.material != null ? dp.material.GetTexture("Base_Map") : null;
                float aspect = tex != null ? (float)tex.width / tex.height : 1f;
                Vector2 scale = new Vector2(width / (d.height * aspect), 1f), bias = Vector2.zero;
                if (d.variant >= 0) { scale = new Vector2(0.25f, 1f); bias = new Vector2(0.25f * d.variant, 0f); }
                if (d.flipX) { bias.x += scale.x; scale.x = -scale.x; }
                if (d.flipY) { bias.y += scale.y; scale.y = -scale.y; }
                dp.uvScale = scale;
                dp.uvBias = bias;
                dp.fadeFactor = d.opacity;
                dp.drawDistance = d.drawDistance;
                // Angle fade (needs the LockdownDecal graph, which has it switched on): by default faces turned more
                // than 30 degrees away from the projector fade out by 60, so nothing stretches onto jambs or side
                // walls. Corner chips project diagonally (45 degrees onto both faces) and fade later.
                dp.startAngleFade = d.angleFadeStart;
                dp.endAngleFade = d.angleFadeEnd;
            }
        }

        static MeshRenderer AddRenderer(GameObject root, string name, Mesh mesh, List<Slot> slots)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = slots.Select(GetMaterial).ToArray();
            mr.shadowCastingMode = ShadowCastingMode.On;
            return mr;
        }

        // ---------- Walls and openings ----------

        struct Frame
        {
            public Vector3 origin, along, outward;
            public Vector3 P(float s, float y, float d) => origin + along * s + Vector3.up * y - outward * d;
            public Quaternion Rot => Quaternion.LookRotation(-outward, Vector3.up); // local x = along, z = inward
        }

        static Frame GetFrame(WallSpec w, out float length, out System.Func<float, float> toS)
        {
            length = w.max - w.min;
            float min = w.min, max = w.max;
            var f = new Frame();
            switch (w.facing)
            {
                case Facing.South: f.outward = Vector3.back; f.along = Vector3.right; f.origin = new Vector3(min, 0f, w.plane); toS = c => c - min; break;
                case Facing.North: f.outward = Vector3.forward; f.along = Vector3.left; f.origin = new Vector3(max, 0f, w.plane); toS = c => max - c; break;
                case Facing.West: f.outward = Vector3.left; f.along = Vector3.back; f.origin = new Vector3(w.plane, 0f, max); toS = c => max - c; break;
                default: f.outward = Vector3.right; f.along = Vector3.forward; f.origin = new Vector3(w.plane, 0f, min); toS = c => c - min; break;
            }
            return f;
        }

        static void BoxW(HouseMesh hm, Frame f, float s0, float s1, float y0, float y1, float d0, float d1, Slot slot, bool collide = false)
        {
            Vector3 a = f.P(s0, y0, d0), b = f.P(s1, y1, d1);
            hm.Box(Vector3.Min(a, b), Vector3.Max(a, b), slot, collide);
        }

        static void ChamferW(HouseMesh hm, Frame f, float s0, float s1, float y0, float y1, float d0, float d1, float ch, Slot slot)
        {
            Vector3 a = f.P(s0, y0, d0), b = f.P(s1, y1, d1);
            hm.ChamferBox(Vector3.Min(a, b), Vector3.Max(a, b), ch, slot);
        }

        static void BuildWall(HouseMesh hm, WallSpec w, HouseSpec spec, ImperfectionSettings imp)
        {
            hm.Detail = false;
            hm.Tag = w.name;
            // Vertex colour R = how far below the old split line a point is (0.5 = on it, +-4 m range). It is
            // linear in height, so it interpolates exactly; the plaster shader blends the two plaster layers
            // on it with a noisy, soft edge instead of a straight material seam.
            bool blend = !float.IsNegativeInfinity(w.splitY);
            float split = w.splitY;
            // B = this wall's own tone bias (dirtier / greyer), set by the weathering pass.
            float dirt = w.dirt;
            hm.ColorFn = blend ? (q, _) => new Color(Mathf.Clamp01(0.5f + (split - q.y) / 8f), 0f, dirt, 1f) : (q, _) => new Color(0f, 0f, dirt, 1f);
            Frame f = GetFrame(w, out float length, out var toS);
            // The room side of the wall (faces turned inward) gets clean interior paint; reveals and frames keep theirs.
            Vector3 outward = f.outward;
            Slot wallSlot = w.slot;
            hm.SlotFn = w.lined ? (c, n, s) => s == wallSlot && Vector3.Dot(n, outward) < -0.9f ? Slot.InteriorWall : s : null;
            hm.Profile = imp != null && w.topFree ? new WarpProfile
            {
                active = true, a = f.P(0f, 0f, 0f), b = f.P(length, 0f, 0f), yPin = w.y0, yFree = w.y1, amp = imp.parapetSag, seed = Seed(w.name),
            } : default;
            float radius = imp == null ? 0f : imp.edgeRadius + (Noise.Value(new Vector3(Seed(w.name) % 97, 0.5f, 0.5f), 5) * 2f - 1f) * imp.edgeRadiusJitter;
            var ops = w.openings.Select(o =>
            {
                float a = Mathf.Clamp(toS(o.w0), 0f, length), b = Mathf.Clamp(toS(o.w1), 0f, length);
                return (o, s0: Mathf.Min(a, b), s1: Mathf.Max(a, b));
            }).ToList();

            // Solid wall = the wall rectangle minus the openings, as vertical strips split at every opening edge.
            var breaks = new SortedSet<float> { 0f, length };
            foreach (var op in ops) { breaks.Add(op.s0); breaks.Add(op.s1); }
            var xs = breaks.ToList();
            for (int i = 0; i < xs.Count - 1; i++)
            {
                float a = xs[i], b = xs[i + 1];
                if (b - a < 1e-4f) continue;
                float m = (a + b) * 0.5f;
                var blocked = ops.Where(op => op.s0 < m && m < op.s1).Select(op => (lo: op.o.y0, hi: op.o.y1)).OrderBy(v => v.lo);
                float y = w.y0;
                foreach (var (lo, hi) in blocked)
                {
                    if (lo > y + 1e-4f) Solid(a, b, y, Mathf.Min(lo, w.y1));
                    y = Mathf.Max(y, hi);
                }
                if (y < w.y1 - 1e-4f) Solid(a, b, y, w.y1);
            }

            for (int i = 0; i < ops.Count; i++)
            {
                hm.Tag = $"{w.name}/op{i}";
                BuildOpening(hm, f, w, ops[i].o, ops[i].s0, ops[i].s1, spec);
            }
            hm.Detail = false;
            hm.ColorFn = null;
            hm.SlotFn = null;
            hm.Profile = default;

            // A solid piece of wall, cut further into cells wherever a worn edge starts or stops: the outer edges
            // at an exposed (convex) wall end, the reveals of openings without a painted surround, the top of a
            // free-standing parapet. Everything else stays a plain box, so wall pieces meet without grooves.
            void Solid(float a, float b, float lo, float hi)
            {
                if (radius <= 0f) { BoxW(hm, f, a, b, lo, hi, 0f, w.thickness, w.slot, true); return; }
                bool atStart = a < 1e-4f, atEnd = b > length - 1e-4f;
                var ys = new SortedSet<float> { lo, hi };
                foreach (var op in ops)
                {
                    if (!Worn(op.o) || (Mathf.Abs(op.s1 - a) > 1e-4f && Mathf.Abs(op.s0 - b) > 1e-4f)) continue;
                    if (op.o.y0 > lo && op.o.y0 < hi) ys.Add(op.o.y0);
                    if (op.o.y1 > lo && op.o.y1 < hi) ys.Add(op.o.y1);
                }
                if (atStart) foreach (float c in ExposureBreaks(spec, w, f, 0f, -1f, lo, hi)) ys.Add(c);
                if (atEnd) foreach (float c in ExposureBreaks(spec, w, f, length, 1f, lo, hi)) ys.Add(c);
                var yl = ys.ToList();
                // Collision: one box per wall piece, as without imperfections (the cells are visual only).
                Vector3 ca = f.P(a, lo, 0f), cb = f.P(b, hi, w.thickness);
                hm.Collision.Box((ca + cb) * 0.5f, Abs(cb - ca), Quaternion.identity, Slot.Render);
                for (int k = 0; k < yl.Count - 1; k++)
                {
                    float y0 = yl[k], y1 = yl[k + 1], ym = (y0 + y1) * 0.5f;
                    if (y1 - y0 < 1e-4f) continue;
                    bool bL = (atStart && EndExposed(spec, w, f, 0f, -1f, ym)) ||
                              ops.Any(op => Worn(op.o) && Mathf.Abs(op.s1 - a) < 1e-4f && op.o.y0 <= y0 + 1e-4f && op.o.y1 >= y1 - 1e-4f);
                    bool bR = (atEnd && EndExposed(spec, w, f, length, 1f, ym)) ||
                              ops.Any(op => Worn(op.o) && Mathf.Abs(op.s0 - b) < 1e-4f && op.o.y0 <= y0 + 1e-4f && op.o.y1 >= y1 - 1e-4f);
                    bool bB = ops.Any(op => Worn(op.o) && Mathf.Abs(op.o.y1 - y0) < 1e-4f && op.s0 <= a + 1e-4f && op.s1 >= b - 1e-4f);
                    bool bT = w.topFree && Mathf.Abs(y1 - w.y1) < 1e-4f;
                    if (b - a < 2.5f * radius) bL = bR = false;
                    if (bL || bR || y1 - y0 < 2.5f * radius) bB = bT = false;
                    if (!(bL || bR || bB || bT)) { BoxW(hm, f, a, b, y0, y1, 0f, w.thickness, w.slot); continue; }
                    BevelCellW(hm, f, a, b, y0, y1, 0f, w.thickness, w.slot, radius, bL, bR, bB, bT, bT, false);
                }
            }
        }

        // Openings whose reveal edges wear round: the ones without a painted surround (the band wears instead).
        static bool Worn(OpeningSpec o) => !o.surround && o.kind is OpeningKind.Window or OpeningKind.GlazedDoor or OpeningKind.Door or OpeningKind.Void;

        // A wall cell (wall frame: s along, y up, d inward from the outer face d0) with rounded edges: bL/bR are the
        // outer vertical edges, bB/bT the outer horizontal ones, bTBack the top edge of the inner face. Vertical and
        // horizontal rounding never meet in one cell (the caller keeps it so), so the cell is a prism.
        static void BevelCellW(HouseMesh hm, Frame f, float s0, float s1, float y0, float y1, float d0, float d1, Slot slot, float r,
            bool bL, bool bR, bool bB, bool bT, bool bTBack, bool collide)
        {
            if (bL || bR)
            {
                var sec = Rounded(new[] { (new Vector2(s0, d0), bL), (new Vector2(s1, d0), bR), (new Vector2(s1, d1), false), (new Vector2(s0, d1), false) }, r);
                hm.Lod0.Extrude(sec.Select(v => f.P(v.x, 0f, v.y)).ToList(), Vector3.up, y0, y1, slot);
            }
            else
            {
                var sec = Rounded(new[] { (new Vector2(y0, d0), bB), (new Vector2(y1, d0), bT), (new Vector2(y1, d1), bTBack), (new Vector2(y0, d1), false) }, r);
                hm.Lod0.Extrude(sec.Select(v => f.P(0f, v.x, v.y)).ToList(), f.along, s0, s1, slot);
            }
            Vector3 a = f.P(s0, y0, d0), b = f.P(s1, y1, d1);
            Vector3 c = (a + b) * 0.5f, size = Abs(b - a);
            if (!hm.Detail) hm.Lod1.Box(c, size, Quaternion.identity, slot);
            if (collide) hm.Collision.Box(c, size, Quaternion.identity, Slot.Render);
        }

        // Rectangle corners -> polygon with the flagged corners rounded (quarter circle in 2 segments).
        static List<Vector2> Rounded((Vector2 p, bool round)[] corners, float r)
        {
            var res = new List<Vector2>();
            int n = corners.Length;
            for (int i = 0; i < n; i++)
            {
                var (c, round) = corners[i];
                if (!round) { res.Add(c); continue; }
                Vector2 dp = (corners[(i + n - 1) % n].p - c).normalized, dn = (corners[(i + 1) % n].p - c).normalized;
                res.Add(c + dp * r);
                res.Add(c + (dp + dn) * (r * (1f - 0.70711f)));
                res.Add(c + dn * r);
            }
            return res;
        }

        static void BuildOpening(HouseMesh hm, Frame f, WallSpec w, OpeningSpec o, float s0, float s1, HouseSpec spec)
        {
            float y0 = o.y0, y1 = o.y1, t = w.thickness, sw = o.surroundWidth;
            hm.Detail = false;
            // Frames, leaves and shutters: varnished brown wood a little sun-bleached (layered wood material), white
            // paint over grey wood showing where it has faded (vertex R = fade), and dirt by how stained this opening is
            // (vertex G). None of an opening's parts are wall plaster, so the wall's blend colour is replaced.
            hm.ColorFn = (q, _) => OpeningPaint(o, o.frameSlot, o.frameFade);

            if (o.kind == OpeningKind.Recess)
            {
                BoxW(hm, f, s0, s1, y0, y1, 0.03f, t - 0.03f, o.surroundSlot);
                return;
            }
            if (o.kind == OpeningKind.Slot)
            {
                const float liner = 0.006f; // paint on the four reveal faces
                BoxW(hm, f, s0, s0 + liner, y0, y1, 0f, t, o.surroundSlot);
                BoxW(hm, f, s1 - liner, s1, y0, y1, 0f, t, o.surroundSlot);
                BoxW(hm, f, s0 + liner, s1 - liner, y1 - liner, y1, 0f, t, o.surroundSlot);
                BoxW(hm, f, s0 + liner, s1 - liner, y0, y0 + liner, 0f, t, o.surroundSlot);
                return;
            }
            DoorStyle style = Style(o);
            if (o.kind == OpeningKind.Void && style != DoorStyle.Open)
            {
                BoxW(hm, f, s0, s1, y0, y1, t - 0.02f, t, Slot.Dark);
                return;
            }

            bool window = o.kind == OpeningKind.Window;
            string tag = hm.Lod0.Tag;
            hm.Tag = tag + "/surround";
            if (o.surround)
            {
                float yb = window && !o.sill ? y0 - sw : y0;
                const float br = 0.005f; // the painted band's edges, rounded by years of repainting
                BevelCellW(hm, f, s0 - sw, s0, yb, y1 + sw, -0.012f, 0f, o.surroundSlot, br, true, true, false, false, false, false);
                BevelCellW(hm, f, s1, s1 + sw, yb, y1 + sw, -0.012f, 0f, o.surroundSlot, br, true, true, false, false, false, false);
                BevelCellW(hm, f, s0, s1, y1, y1 + sw, -0.012f, 0f, o.surroundSlot, br, false, false, true, true, false, false);
                if (window && !o.sill) BevelCellW(hm, f, s0, s1, y0 - sw, y0, -0.012f, 0f, o.surroundSlot, br, false, false, true, true, false, false);
            }
            hm.Tag = tag + "/sill";
            if (o.sill) ChamferW(hm, f, s0 - 0.06f, s1 + 0.06f, y0 - 0.05f, y0 + 0.01f, -0.05f, o.inset, 0.01f, Slot.Concrete);
            hm.Tag = tag + "/frame";

            // Frame (LOD0 only), set back in the reveal; the aluminium door has its own white aluminium frame.
            const float fw = 0.055f;
            float d0 = o.inset, d1 = o.inset + 0.06f;
            float yIn0 = o.sill ? y0 + 0.01f : y0;
            Slot frameSlot = style == DoorStyle.AluminiumGlazed && o.kind == OpeningKind.GlazedDoor ? Slot.Aluminium : o.frameSlot;
            hm.ColorFn = (q, _) => OpeningPaint(o, frameSlot, o.frameFade);
            hm.Detail = window; // door frames show in LOD1 too: the open leaf no longer covers them
            hm.UvTurn = true; // jambs: grain upright
            BoxW(hm, f, s0, s0 + fw, yIn0, y1, d0, d1, frameSlot);
            BoxW(hm, f, s1 - fw, s1, yIn0, y1, d0, d1, frameSlot);
            hm.UvTurn = false;
            BoxW(hm, f, s0 + fw, s1 - fw, y1 - fw, y1, d0, d1, frameSlot);
            if (window)
            {
                BoxW(hm, f, s0 + fw, s1 - fw, yIn0, yIn0 + fw, d0, d1, o.frameSlot);
                for (int k = 1; k <= o.mullions; k++)
                {
                    float c = Mathf.Lerp(s0, s1, k / (o.mullions + 1f));
                    BoxW(hm, f, c - 0.03f, c + 0.03f, yIn0 + fw, y1 - fw, d0, d1, o.frameSlot);
                }
            }
            hm.Detail = false;
            hm.Tag = tag + "/leaf";
            switch (o.kind)
            {
                case OpeningKind.Window:
                    GlassW(hm, f, o, s0 + fw * 0.5f, s1 - fw * 0.5f, yIn0 + fw * 0.5f, y1 - fw * 0.5f, d0 + 0.03f);
                    break;
                case OpeningKind.GlazedDoor when style == DoorStyle.WoodGlazed:
                    BuildWoodGlazedDoor(hm, f, o, s0 + fw, s1 - fw, y0, y1 - fw, d0);
                    break;
                case OpeningKind.GlazedDoor:
                    BuildAluminiumDoor(hm, f, o, s0, s1, s0 + fw, s1 - fw, y0, y1 - fw, d0, d1);
                    break;
                case OpeningKind.Door:
                    BuildPlankDoor(hm, f, o, s0 + fw, s1 - fw, y0, y1 - fw, d0);
                    break;
            }
            // Windows stop the walker at the glass; doors stand open and doorways lead into the house.
            if (window) ColW(hm, f, s0, s1, y0, y1, d0, d1);
            if (o.interior && o.kind is OpeningKind.Window or OpeningKind.GlazedDoor or OpeningKind.Void)
            {
                hm.Tag = tag + "/room";
                BuildInterior(hm, f, w, o, s0, s1, spec);
            }

            hm.ColorFn = (q, _) => OpeningPaint(o, o.frameSlot, o.frameFade);
            hm.Tag = tag + "/shutters";
            if (o.shutters != ShutterKind.None) BuildShutters(hm, f, o, s0, s1);
        }

        static Color OpeningPaint(OpeningSpec o, Slot slot, float fade) => slot == Slot.FrameWhite
            ? new Color(fade, 0.1f + 0.5f * o.stain, 0f, 1f)
            : new Color(0.75f, 0.1f + 0.4f * o.stain, 0f, 1f);

        static void BuildShutters(HouseMesh hm, Frame f, OpeningSpec o, float s0, float s1)
        {
            float leafW = (s1 - s0) * 0.5f;
            float gap = o.surround ? o.surroundWidth + 0.01f : 0.03f;
            float y0 = o.y0 + 0.02f, y1 = o.y1;
            const float dFront = -0.042f, dBack = -0.014f;
            var leaves = new[] { s0 - gap - leafW, s1 + gap };
            for (int leaf = 0; leaf < 2; leaf++)
            {
                float l0 = leaves[leaf], l1 = l0 + leafW;
                // Each leaf stands off the wall by its own angle, turning about its hinge (the edge next to the window).
                float hingeS = leaf == 0 ? l1 : l0, farS = leaf == 0 ? l0 : l1;
                float angle = o.shutterAngle != null ? o.shutterAngle[leaf] : 0f;
                Vector3 hinge = f.P(hingeS, 0f, dBack);
                Vector3 arm = f.along * (farS - hingeS);
                float sign = Vector3.Dot(Quaternion.AngleAxis(1f, Vector3.up) * arm, f.outward) > 0f ? 1f : -1f;
                Quaternion swing = Quaternion.AngleAxis(sign * angle, Vector3.up);
                Vector3 Sw(Vector3 p) => hinge + swing * (p - hinge);
                float fade = o.shutterFade != null ? o.shutterFade[leaf] : o.frameFade;
                hm.ColorFn = (q, _) => OpeningPaint(o, o.shutterSlot, fade);

                void LeafBox(MeshPart part, float ss0, float ss1, float yy0, float yy1, float dd0, float dd1, Quaternion local)
                {
                    Vector3 p = f.P(ss0, yy0, dd0), q = f.P(ss1, yy1, dd1);
                    part.Box(Sw((p + q) * 0.5f), Abs(q - p), swing * local, o.shutterSlot);
                }
                if (o.shutters == ShutterKind.PanelOpen)
                {
                    LeafBox(hm.Lod0, l0, l1, y0, y1, dFront, dBack, Quaternion.identity);
                    LeafBox(hm.Lod1, l0, l1, y0, y1, dFront, dBack, Quaternion.identity);
                    foreach (float yb in new[] { y0 + 0.15f, (y0 + y1) * 0.5f - 0.06f, y1 - 0.27f }) // battens
                        LeafBox(hm.Lod0, l0 + 0.04f, l1 - 0.04f, yb, yb + 0.12f, dFront - 0.012f, dFront, Quaternion.identity);
                    continue;
                }
                // Louvred: LOD1 gets a plain panel, LOD0 the stiles, rails and angled slats.
                LeafBox(hm.Lod1, l0, l1, y0, y1, dFront, dBack, Quaternion.identity);
                var lod0 = hm.Lod0;
                void L0(float ss0, float ss1, float yy0, float yy1) => LeafBox(lod0, ss0, ss1, yy0, yy1, dFront, dBack, Quaternion.identity);
                const float stile = 0.05f, rail = 0.08f;
                L0(l0, l0 + stile, y0, y1);
                L0(l1 - stile, l1, y0, y1);
                L0(l0 + stile, l1 - stile, y0, y0 + rail);
                L0(l0 + stile, l1 - stile, y1 - rail, y1);
                float ym = (y0 + y1) * 0.5f;
                L0(l0 + stile, l1 - stile, ym - 0.03f, ym + 0.03f);
                foreach (var (sy0, sy1) in new[] { (y0 + rail, ym - 0.03f), (ym + 0.03f, y1 - rail) })
                {
                    int n = Mathf.Max(1, Mathf.FloorToInt((sy1 - sy0) / 0.038f));
                    float pitch = (sy1 - sy0) / n;
                    for (int k = 0; k < n; k++)
                    {
                        Vector3 c = f.P((l0 + l1) * 0.5f, sy0 + pitch * (k + 0.5f), (dFront + dBack) * 0.5f);
                        lod0.Box(Sw(c), new Vector3(leafW - 2f * stile + 0.01f, pitch * 1.25f, 0.005f), swing * f.Rot * Quaternion.Euler(-50f, 0f, 0f), o.shutterSlot);
                    }
                }
            }
        }

        static Vector3 Abs(Vector3 v) => new(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        // GLSL-style smoothstep (Mathf.SmoothStep interpolates between its first two arguments instead).
        static float Smooth(float e0, float e1, float x)
        {
            float t = Mathf.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3f - 2f * t);
        }

        // ---------- Other parts ----------

        // Dressed corner stones: alternating long/short blocks of slightly irregular size, set 6 cm into the wall
        // and standing ~3.5 cm proud of it, with mortar joints showing the wall between courses.
        static void BuildQuoins(HouseMesh hm, QuoinSpec q)
        {
            const float joint = 0.012f, embed = 0.06f, chamfer = 0.015f;
            int seed = 17;
            foreach (char c in q.name) seed = seed * 31 + c;
            var rnd = new System.Random(seed);
            float Rand(float lo, float hi) => lo + (hi - lo) * (float)rnd.NextDouble();

            float y = q.y0;
            for (int k = 0; y < q.y1 - 0.1f; k++)
            {
                float h = Rand(0.22f, 0.30f);
                if (q.y1 - (y + h) < 0.14f) h = q.y1 - y; // no sliver course at the top
                float yTop = y + h - joint;
                bool longA = k % 2 == 0;
                float la = longA ? Rand(0.32f, 0.44f) : Rand(0.18f, 0.26f);
                float lb = longA ? Rand(0.18f, 0.26f) : Rand(0.32f, 0.44f);
                float pa = Rand(0.03f, 0.04f), pb = Rand(0.03f, 0.04f); // how far each face stands proud
                hm.Tag = $"{q.name}/{k}";
                // Block on face A wraps the corner; the block on face B starts where A's embedded part ends.
                Block(-pb, la, -pa, embed);
                Block(-pb, embed, embed, lb);
                y += h;

                void Block(float a0, float a1, float b0, float b1)
                {
                    Vector3 p = q.corner + q.dirA * a0 + q.dirB * b0 + Vector3.up * y;
                    Vector3 r = q.corner + q.dirA * a1 + q.dirB * b1 + Vector3.up * yTop;
                    hm.ChamferBox(Vector3.Min(p, r), Vector3.Max(p, r), chamfer, Slot.Stone);
                }
            }
        }

        static void BuildStair(HouseMesh hm, StairSpec s)
        {
            float rise = (s.yTop - s.yBase) / s.risers, run = (s.zTop - s.zStart) / s.risers;
            const float tile = 0.02f;
            for (int i = 0; i < s.risers; i++)
            {
                hm.Tag = $"{s.name}/{i}";
                float yLo = s.yBase + i * rise, yHi = yLo + rise;
                float zFront = s.zStart + i * run, zNext = i == s.risers - 1 ? s.zTop : zFront + run;
                // Each layer runs from its riser to the top, so the stair is solid underneath.
                // Layers overlap by the tile thickness so no gap shows at the back or the sides.
                hm.Box(new Vector3(s.x0, i == 0 ? yLo : yLo - tile, zFront), new Vector3(s.x1, yHi - tile, s.zTop), s.bodySlot, true);
                hm.Tag = $"{s.name}/{i}/tread";
                hm.Box(new Vector3(s.x0, yHi - tile, zFront - 0.02f), new Vector3(s.x1, yHi, zNext), s.tileSlot);
                hm.Tag = $"{s.name}/{i}/riser";
                hm.Box(new Vector3(s.x0, yLo, zFront - 0.015f), new Vector3(s.x1, yHi - tile, zFront), s.tileSlot);
            }
        }

        static void BuildRailing(HouseMesh hm, RailingSpec r, ImperfectionSettings imp)
        {
            Vector3 d = r.b - r.a;
            float len = d.magnitude;
            Vector3 u = d / len;
            var rot = Quaternion.LookRotation(u, Vector3.up);
            Vector3 mid = (r.a + r.b) * 0.5f;
            hm.Detail = false;
            float postH = r.height - 0.04f; // posts stop under the top rail, so their tops never share its plane
            // Vertex colour R = rust: the rails stay mostly painted (chips come from the material), bars and posts
            // rust at their feet, at the bottom rail and under the top rail, where water sits.
            float rust = imp != null ? imp.rustAmount : 0f, yBase = r.a.y;
            hm.ColorFn = (q, _) => new Color(0.2f * rust, 0f, 0f, 1f);
            hm.Tag = r.name + "/top";
            hm.Box(mid + Vector3.up * (r.height - 0.02f), new Vector3(0.045f, 0.04f, len), rot, Slot.Metal);
            hm.ColorFn = (q, _) =>
            {
                float h = q.y - yBase;
                float k = Mathf.Max(1f - Smooth(0.03f, 0.2f, h), Smooth(r.height - 0.22f, r.height - 0.05f, h));
                return new Color(rust * k, 0f, 0f, 1f);
            };
            // Posts and bars in segments, so the rust ramps only near the joints.
            float[] cuts = { 0f, 0.12f, 0.24f, r.height - 0.2f };
            void Upright(Vector3 at, float top, Vector3 size)
            {
                for (int k = 0; k < cuts.Length; k++)
                {
                    float y0 = cuts[k], y1 = k + 1 < cuts.Length ? cuts[k + 1] : top;
                    if (y1 - y0 < 1e-3f) continue;
                    hm.Box(at + Vector3.up * ((y0 + y1) * 0.5f), new Vector3(size.x, y1 - y0, size.z), rot, Slot.Metal);
                }
            }
            hm.Tag = r.name + "/postA";
            Upright(r.a + u * 0.02f, postH, new Vector3(0.04f, 0f, 0.04f));
            hm.Tag = r.name + "/postB";
            Upright(r.b - u * 0.02f, postH, new Vector3(0.04f, 0f, 0.04f));
            hm.Collision.Box(mid + Vector3.up * (r.height * 0.5f), new Vector3(0.06f, r.height, len), rot, Slot.Render);
            hm.Detail = true;
            hm.Tag = r.name + "/bars";
            int n = Mathf.FloorToInt(len / r.barSpacing);
            for (int i = 1; i < n; i++)
            {
                // Bars run from 2 cm above the floor to under the top rail, as before.
                Vector3 at = r.a + u * (i * len / n);
                float bot = 0.02f, top = r.height - 0.02f;
                for (int k = 0; k < cuts.Length; k++)
                {
                    float y0 = Mathf.Max(bot, cuts[k]), y1 = k + 1 < cuts.Length ? cuts[k + 1] : top;
                    if (y1 - y0 < 1e-3f) continue;
                    hm.Box(at + Vector3.up * ((y0 + y1) * 0.5f), new Vector3(0.016f, y1 - y0, 0.016f), rot, Slot.Metal);
                }
            }
            hm.ColorFn = (q, _) => new Color(0.2f * rust, 0f, 0f, 1f);
            hm.Tag = r.name + "/bottom";
            hm.Box(mid + Vector3.up * 0.1f, new Vector3(0.03f, 0.03f, len - 0.004f), rot, Slot.Metal);
            hm.Detail = false;
            hm.ColorFn = null;
        }

        // A hand-hewn timber post: slightly tapered and out of plumb, uneven, chamfered, with iron collars, drying checks and
        // a diagonal knee brace mitred into both the post and the beam.
        static void BuildPost(HouseMesh hm, PergolaSpec p, Vector3 post, float beamH, ImperfectionSettings imp,
            System.Func<Vector3, Vector3, float, float, Color> Wood)
        {
            var rnd = new System.Random(Seed(p.name + "/post"));
            float R() => (float)rnd.NextDouble();
            float J(float a) => imp != null ? (R() * 2f - 1f) * a : 0f;
            float yPost = post.y, yTop = p.yUnder - beamH;
            float lean = J(0.6f); // degrees, about z
            var rot = Quaternion.Euler(0f, 0f, lean);
            float len = yTop - yPost;
            var c = new Vector3(post.x, yPost + len * 0.5f, post.z);

            // Rings of an octagon (square with big chamfers), each a little different in size and place.
            const int rings = 5;
            var half = new float[rings]; var off = new Vector2[rings];
            for (int j = 0; j < rings; j++)
            {
                half[j] = Mathf.Lerp(0.079f, 0.066f, j / (rings - 1f)) + J(0.002f);
                off[j] = new Vector2(J(0.003f), J(0.003f));
            }
            float HalfAt(float y) { float t = Mathf.Clamp01((y - yPost) / len) * (rings - 1); int j = Mathf.Min((int)t, rings - 2); return Mathf.Lerp(half[j], half[j + 1], t - j); }
            Vector3 Ring(int j, int k)
            {
                float h = half[j], ch = 0.016f, e = h - ch;
                Vector2 q = k switch { 0 => new(h, -e), 1 => new(h, e), 2 => new(e, h), 3 => new(-e, h), 4 => new(-h, e), 5 => new(-h, -e), 6 => new(-e, -h), _ => new(e, -h) };
                float y = yPost + len * j / (rings - 1f);
                return c + rot * (new Vector3(post.x + off[j].x + q.x, y, post.z + off[j].y + q.y) - c);
            }
            hm.Tag = p.name + "/post";
            float postShade = 0.5f + 0.15f * R();
            hm.ColorFn = (q, n) => Wood(q, n, postShade, Mathf.Clamp01(1f - (q.y - yPost) / 0.7f) * 0.85f);
            hm.UvTurn = true; // vertical faces: grain runs up the post
            for (int j = 0; j < rings - 1; j++)
                for (int k = 0; k < 8; k++)
                {
                    int k1 = (k + 1) % 8;
                    Vector3 mid = (Ring(j, k) + Ring(j, k1)) * 0.5f;
                    mid.y = c.y; // outward is horizontal
                    Vector3 outward = mid - c;
                    hm.Lod0.Quad(Ring(j, k), Ring(j, k1), Ring(j + 1, k1), Ring(j + 1, k), outward, Slot.Wood);
                }
            hm.Lod1.Box(c, new Vector3(0.145f, len, 0.145f), rot, Slot.Wood);
            hm.UvTurn = false;
            hm.Collision.Box(c, new Vector3(0.15f, len, 0.15f), rot, Slot.Render);

            // Knee brace toward the house: a 45 degree timber whose ends are cut flush to the post and to the beam underside.
            hm.ColorFn = (q, n) => Wood(q, n, 0.6f, 0f);
            hm.Tag = p.name + "/postBrace";
            const float run = 0.6f, tv = 0.12f, depth = 0.1f, emb = 0.01f;
            float xp = post.x - 0.05f, y0 = yTop - run;
            var poly = new[]
            {
                new Vector2(xp, y0), new Vector2(xp, y0 - tv),
                new Vector2(xp - run - tv - emb, yTop + emb), new Vector2(xp - run - emb, yTop + emb),
            };
            Vector2 cen = (poly[0] + poly[1] + poly[2] + poly[3]) * 0.25f;
            float zb0 = post.z - depth * 0.5f, zb1 = post.z + depth * 0.5f;
            hm.UvTurn = true;
            foreach (var part in new[] { hm.Lod0, hm.Lod1 })
            {
                for (int i = 0; i < 4; i++)
                {
                    Vector2 a = poly[i], b = poly[(i + 1) % 4], m = (a + b) * 0.5f - cen;
                    part.Quad(new Vector3(a.x, a.y, zb0), new Vector3(b.x, b.y, zb0), new Vector3(b.x, b.y, zb1), new Vector3(a.x, a.y, zb1), new Vector3(m.x, m.y, 0f), Slot.Wood);
                }
                part.Quad(new Vector3(poly[0].x, poly[0].y, zb0), new Vector3(poly[1].x, poly[1].y, zb0), new Vector3(poly[2].x, poly[2].y, zb0), new Vector3(poly[3].x, poly[3].y, zb0), Vector3.back, Slot.Wood);
                part.Quad(new Vector3(poly[0].x, poly[0].y, zb1), new Vector3(poly[1].x, poly[1].y, zb1), new Vector3(poly[2].x, poly[2].y, zb1), new Vector3(poly[3].x, poly[3].y, zb1), Vector3.forward, Slot.Wood);
            }
            hm.UvTurn = false;

            // Iron: two collars on the post, and a bolt head near each end of the brace.
            hm.ColorFn = null;
            hm.Tag = p.name + "/postIron";
            foreach (float yc in new[] { yTop - 0.12f, yPost + 0.3f })
            {
                float cw = HalfAt(yc) * 2f + 0.014f;
                var cc = c + rot * new Vector3(0f, yc - c.y, 0f);
                hm.Lod0.ChamferBox(cc, new Vector3(cw, 0.04f, cw), rot, 0.004f, Slot.Metal);
                hm.Lod1.Box(cc, new Vector3(cw, 0.04f, cw), rot, Slot.Metal);
            }
            hm.Detail = true;
            foreach (float bx in new[] { xp - 0.13f, xp - run + 0.06f })
            {
                float by = y0 - tv * 0.5f + (xp - bx);
                hm.Cylinder(new Vector3(bx, by, zb0 - 0.001f), new Vector3(bx, by, zb0 - 0.016f), 0.014f, Slot.Metal);
            }

            // Drying checks: thin dark cracks along the grain on each face (LOD0 only).
            hm.Tag = p.name + "/postChecks";
            for (int i = 0; i < 7; i++)
            {
                float cl = 0.25f + R() * 0.7f, cy = yPost + 0.45f + R() * (len - 0.9f - cl);
                float h = HalfAt(cy) - 0.03f, o = (R() * 2f - 1f) * h * 0.8f, hf = HalfAt(cy) + 0.001f;
                int face = i % 4;
                Vector3 size = face % 2 == 0 ? new Vector3(0.004f, cl, 0.003f) : new Vector3(0.003f, cl, 0.004f);
                Vector3 pos = face switch
                {
                    0 => new Vector3(post.x + o, cy, post.z - hf),
                    1 => new Vector3(post.x + hf, cy, post.z + o),
                    2 => new Vector3(post.x + o, cy, post.z + hf),
                    _ => new Vector3(post.x - hf, cy, post.z + o),
                };
                hm.Lod0.Box(c + rot * (pos - c), size, rot, Slot.Dark);
            }
            hm.Detail = false;
        }

        static void BuildPergola(HouseMesh hm, PergolaSpec p, ImperfectionSettings imp)
        {
            const float rafterH = 0.16f, beamH = 0.2f, board = 0.025f;
            hm.Detail = false;
            // The whole timber roof sags a little between the wall and the post.
            hm.Profile = imp != null ? new WarpProfile
            {
                active = true, a = new Vector3(p.x0, 0f, 0f), b = new Vector3(p.x1, 0f, 0f), yPin = 0f, yFree = 0f, amp = imp.pergolaSag, seed = Seed(p.name),
            } : default;
            // Vertex colour: R = 0 sun-bleached grey .. 1 brown wood (tops are always bleached), G = darkening
            // where rain runs: the street ends of the rafters and the beam, the foot of the posts.
            Color Wood(Vector3 q, Vector3 n, float shade, float wet) =>
                new Color(n.y > 0.5f ? shade * 0.25f : shade, wet, 0f, 1f);
            float WetEnd(Vector3 q) => Mathf.Clamp01(1f - (q.z - p.z0) / 0.7f) * 0.8f;
            int rafters = p.rafterX?.Count ?? Mathf.Max(2, Mathf.RoundToInt((p.x1 - p.x0 - 0.1f) / p.rafterSpacing) + 1);
            hm.UvSwap = true; // rafters run along Z: grain along them on their undersides too
            for (int i = 0; i < rafters; i++)
            {
                float x = p.rafterX != null ? p.rafterX[i] : Mathf.Lerp(p.x0 + 0.05f, p.x1 - 0.05f, i / (rafters - 1f));
                float twist = p.rafterTwist != null ? p.rafterTwist[i] : 0f, shade = p.rafterShade != null ? p.rafterShade[i] : 1f;
                hm.Tag = $"{p.name}/rafter{i}";
                hm.ColorFn = (q, n) => Wood(q, n, shade, WetEnd(q));
                // A twisted rafter rests on one bottom edge (lifted by what the twist drops), gapping on the other.
                float lift = 0.04f * Mathf.Sin(Mathf.Abs(twist) * Mathf.Deg2Rad) + rafterH * 0.5f * (1f - Mathf.Cos(twist * Mathf.Deg2Rad));
                var c = new Vector3(x, p.yUnder + rafterH * 0.5f + lift, (p.z0 + p.z1) * 0.5f);
                hm.Box(c, new Vector3(0.08f, rafterH, p.z1 - p.z0), Quaternion.Euler(0f, 0f, twist), Slot.Wood);
            }
            hm.UvSwap = false;
            hm.Tag = p.name + "/beam";
            hm.ColorFn = (q, n) => Wood(q, n, 0.6f, Mathf.Clamp01(1f - Mathf.Min(q.x - p.x0, p.x1 - q.x) / 0.8f) * 0.6f);
            hm.Box(new Vector3(p.x0, p.yUnder - beamH, p.z0), new Vector3(p.x1, p.yUnder, p.z0 + 0.14f), Slot.Wood);
            hm.Tag = p.name + "/posts";
            foreach (var post in p.posts)
                BuildPost(hm, p, post, beamH, imp, Wood);
            hm.ColorFn = (q, n) => Wood(q, n, 0.7f, WetEnd(q) * 0.5f);
            // Boards: individual planks on LOD0, one sheet on LOD1.
            float yb = p.yUnder + rafterH;
            hm.Lod1.Box(new Vector3((p.x0 + p.x1) * 0.5f, yb + board * 0.5f, (p.z0 + p.z1) * 0.5f), new Vector3(p.x1 - p.x0 + 0.06f, board, p.z1 - p.z0 + 0.05f), Quaternion.identity, Slot.Wood);
            hm.Collision.Box(new Vector3((p.x0 + p.x1) * 0.5f, yb + board * 0.5f, (p.z0 + p.z1) * 0.5f), new Vector3(p.x1 - p.x0, board, p.z1 - p.z0), Quaternion.identity, Slot.Render);
            float zStart = p.z0 - 0.05f, span = p.z1 - zStart;
            int planks = Mathf.CeilToInt(span / 0.146f);
            float pitch = span / planks;
            hm.Tag = p.name + "/boards";
            // Hand-laid boards: the joints wander a little (gaps stay 6 mm), each board its own shade.
            var rnd = new System.Random(Seed(p.name + "/boards"));
            float J() => imp != null ? ((float)rnd.NextDouble() * 2f - 1f) * 0.012f : 0f;
            float z0b = zStart;
            for (int i = 0; i < planks; i++)
            {
                float z1b = i == planks - 1 ? zStart + span : zStart + pitch * (i + 1) + J();
                float shade = imp != null ? 0.55f + 0.3f * (float)rnd.NextDouble() : 0.7f;
                hm.Lod0.ColorFn = (q, n) => Wood(q, n, shade, WetEnd(q) * 0.5f);
                hm.Lod0.Box(new Vector3((p.x0 + p.x1) * 0.5f, yb + board * 0.5f, (z0b + z1b - 0.006f) * 0.5f), new Vector3(p.x1 - p.x0 + 0.06f, board, z1b - z0b - 0.006f), Quaternion.identity, Slot.Wood);
                z0b = z1b;
            }
            hm.ColorFn = null;
            hm.Profile = default;
        }

        // ---------- Assets ----------

        static readonly Dictionary<Slot, (Color color, float smooth)> Placeholder = new()
        {
            { Slot.Render, (new Color(0.80f, 0.80f, 0.78f), 0.15f) },
            { Slot.RenderOld, (new Color(0.74f, 0.73f, 0.70f), 0.1f) },
            { Slot.Concrete, (new Color(0.55f, 0.55f, 0.53f), 0.15f) },
            { Slot.PaintBlue, (new Color(0.20f, 0.36f, 0.62f), 0.3f) },
            { Slot.Stone, (new Color(0.62f, 0.52f, 0.38f), 0.15f) },
            { Slot.Wood, (new Color(0.33f, 0.20f, 0.12f), 0.25f) },
            { Slot.Tile, (new Color(0.42f, 0.43f, 0.42f), 0.35f) },
            { Slot.Metal, (new Color(0.07f, 0.07f, 0.07f), 0.35f) },
            { Slot.Glass, (new Color(0.06f, 0.07f, 0.08f), 0.92f) },
            { Slot.FrameWhite, (new Color(0.84f, 0.84f, 0.82f), 0.4f) },
            { Slot.Dark, (new Color(0.02f, 0.02f, 0.02f), 0.0f) },
            { Slot.GlassDusty, (new Color(0.08f, 0.09f, 0.09f), 0.85f) },
            { Slot.Aluminium, (new Color(0.82f, 0.82f, 0.80f), 0.45f) },
            { Slot.Hardware, (new Color(0.70f, 0.70f, 0.68f), 0.55f) },
            { Slot.Interior, (new Color(0.60f, 0.59f, 0.56f), 0.1f) },
            { Slot.Curtain, (new Color(0.85f, 0.83f, 0.78f), 0.1f) },
            { Slot.Brass, (new Color(0.55f, 0.44f, 0.25f), 0.45f) },
            { Slot.InteriorWall, (new Color(0.85f, 0.84f, 0.81f), 0.2f) },
            { Slot.StoneRubble, (new Color(0.65f, 0.63f, 0.59f), 0.15f) },
        };

        public static Material GetMaterial(Slot slot)
        {
            string path = $"{MatDir}/M_House_{slot}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat;
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            var (color, smooth) = Placeholder[slot];
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Smoothness", smooth);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        internal static Mesh SaveMesh(Mesh mesh, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null) { AssetDatabase.CreateAsset(mesh, path); return mesh; }
            existing.Clear();
            EditorUtility.CopySerialized(mesh, existing);
            Object.DestroyImmediate(mesh);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        // ---------- Review scene helpers ----------

        static void SetupSun()
        {
            Light sun = RenderSettings.sun;
            if (sun == null)
                foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type == LightType.Directional) { sun = l; break; }
            if (sun == null)
            {
                sun = new GameObject("Sun").AddComponent<Light>();
                sun.type = LightType.Directional;
            }
            sun.name = "Sun";
            // Quick approximation of the Phase 6 target (Task 4 rule): ~4500 K, almost due west, 20 degrees up.
            sun.transform.rotation = Quaternion.Euler(LockdownGroundBuilder.SunElevation, LockdownGroundBuilder.SunAzimuth - 180f, 0f);
            sun.useColorTemperature = true;
            sun.colorTemperature = 4500f;
            sun.color = Color.white;
            sun.intensity = 2.5f;
            sun.shadows = LightShadows.Soft;
            RenderSettings.sun = sun;
        }

        // Quick approximation of the Phase 6 sky: deep blue overhead, pale warm glow toward the low western sun.
        static void SetupSky()
        {
            const string path = "Assets/Lockdown/Materials/Review/M_ReviewSky.mat";
            var sky = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (sky == null)
            {
                sky = new Material(Shader.Find("Skybox/Procedural"));
                AssetDatabase.CreateAsset(sky, path);
            }
            sky.SetFloat("_SunDisk", 2f);
            sky.SetFloat("_SunSize", 0.02f);
            sky.SetFloat("_SunSizeConvergence", 8f);
            sky.SetFloat("_AtmosphereThickness", 0.75f);
            sky.SetColor("_SkyTint", new Color(0.42f, 0.5f, 0.62f));
            sky.SetColor("_GroundColor", new Color(0.36f, 0.35f, 0.33f));
            sky.SetFloat("_Exposure", 1.15f);
            EditorUtility.SetDirty(sky);
            RenderSettings.skybox = sky;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity = 1f;
            DynamicGI.UpdateEnvironment();
        }

        // Game-ready prop prefabs only (LODs, budgeted), never the raw scans (project prop rule).
        internal static void PlaceProp(GameObject parent, string prop, Vector3 pos, float yaw, float scale)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(LockdownPropBuilder.PrefabPath(prop));
            if (prefab == null) { LockdownPropBuilder.BuildAll(); prefab = AssetDatabase.LoadAssetAtPath<GameObject>(LockdownPropBuilder.PrefabPath(prop)); }
            if (prefab == null) { Debug.LogWarning("[Lockdown] Missing prop " + prop); return; }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent.scene);
            go.transform.SetParent(parent.transform, false);
            go.transform.SetLocalPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            go.transform.localScale = Vector3.one * scale;
        }

        // Neutral tonemapping only, so bright plaster doesn't clip; the real review grade comes in Task 5.
        static void SetupReviewVolume()
        {
            const string profilePath = "Assets/Lockdown/Settings/Review/VP_TestHouse_Neutral.asset";
            EnsureFolder("Assets/Lockdown/Settings/Review");
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(profilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, profilePath);
                var tm = profile.Add<Tonemapping>(true);
                tm.mode.Override(TonemappingMode.Neutral);
                AssetDatabase.AddObjectToAsset(tm, profile);
                EditorUtility.SetDirty(profile);
                AssetDatabase.SaveAssets();
            }
            var go = GameObject.Find("ReviewVolume") ?? new GameObject("ReviewVolume");
            var vol = go.GetComponent<Volume>() ?? go.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.sharedProfile = profile;
            go.tag = EditorOnly;
        }

        internal static void ReviewCamera(GameObject parent, string name, Vector3 pos, Vector3 target, float fov)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(target - pos, Vector3.up));
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 2000f;
            cam.depth = -10f;
            cam.enabled = false; // rendered on demand for screenshots
            var data = go.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        }
    }
}
