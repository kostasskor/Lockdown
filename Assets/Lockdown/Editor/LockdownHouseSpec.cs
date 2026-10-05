using System;
using System.Collections.Generic;
using UnityEngine;

namespace Lockdown.EditorTools
{
    // A house described as data. Units are metres in the house's local space:
    // +X along the street facade (left to right seen from the street), +Z into the plot, Y up, ground at y = 0.
    // Review fixes change these numbers, never the builder code.

    public enum Facing { South, North, West, East } // direction the wall's outer face looks (South = -Z, toward the street)
    public enum OpeningKind { Window, GlazedDoor, Door, Void, Recess, Slot } // Slot = through-opening with painted reveals
    public enum ShutterKind { None, LouvredOpen, PanelOpen }
    // Door leaves, after the village's real doors (03): white aluminium balcony door, brown wooden glazed door,
    // ledged plank door, or an open doorway into a dim room.
    public enum DoorStyle { Auto, AluminiumGlazed, WoodGlazed, WoodPlank, Open }
    public enum CurtainKind { Auto, None, Cafe, TiedLeft, TiedRight, DrawnPair }

    [Serializable]
    public class OpeningSpec
    {
        public OpeningKind kind = OpeningKind.Window;
        public float w0, w1;          // range along the wall's axis, in house coordinates (X for S/N walls, Z for W/E walls)
        public float y0, y1;          // bottom (sill or threshold) and top of the opening
        public bool surround = true;  // painted band around the opening (the village's blue frames)
        public Slot surroundSlot = Slot.PaintBlue;
        public float surroundWidth = 0.07f;
        public Slot frameSlot = Slot.FrameWhite;
        public ShutterKind shutters = ShutterKind.None;
        public Slot shutterSlot = Slot.FrameWhite;
        public bool sill;
        public float inset = 0.12f;   // depth of the window/door frame behind the outer face
        public int mullions = 1;      // vertical bars in a window (1 = two casements)
        public DoorStyle doorStyle;   // Auto = by kind: GlazedDoor -> AluminiumGlazed, Door -> WoodPlank, Void -> Open
        public bool interior;         // a sealed room behind the opening (off: nothing inside the houses yet, user, Task 4)
        public float interiorDepth;   // 0 = default (1.6 m windows, 2.2 m doors)
        public CurtainKind curtain;   // Auto = seeded per opening
        public bool hingeLeft = true; // door leaves: hinges on the low-s side (seen from outside: the left)
        public float openAngle = -1f; // door leaves stand open inward by this many degrees; -1 = seeded (65-100)
        // Filled by the weathering pass (seeded), so no two openings are dressed the same:
        [NonSerialized] public float stain;          // 0 = almost clean .. 1 = heavily stained (sill streaks, dirty frame)
        [NonSerialized] public float frameFade = 0.3f; // painted frame: vertex R of the paint/faded-wood layers
        [NonSerialized] public float[] shutterAngle;   // per leaf, degrees away from the wall
        [NonSerialized] public float[] shutterFade;    // per leaf, like frameFade
        [NonSerialized] public CurtainKind curtainKind; // resolved curtain (seeded when curtain = Auto)
        [NonSerialized] public float curtainTone;       // 0 white .. 1 cream
        [NonSerialized] public float curtainReach;      // tied / drawn curtains: how far across the opening (0..1)
        [NonSerialized] public bool dustyGlass;
        [NonSerialized] public float swing;             // resolved open angle of the door leaf, degrees
        [NonSerialized] public float handleDroop;       // old lever spring: how far the lever sags, metres at its tip
        [NonSerialized] public float roomTone;          // interior walls: 0 white .. 1 older greyer render
        [NonSerialized] public bool roomCabinet;        // a wardrobe against the back wall (a depth cue)
    }

    [Serializable]
    public class WallSpec
    {
        public string name;
        public Facing facing;
        public float plane;           // coordinate of the outer face (Z for S/N walls, X for W/E walls)
        public float min, max;        // extent along the wall's axis
        public float y0, y1;
        public float thickness = 0.3f;
        public Slot slot = Slot.Render;
        public float splitY = float.NegativeInfinity; // below this height the wall uses lowerSlot (rougher ground-floor render)
        public Slot lowerSlot = Slot.RenderOld;
        public bool topFree;          // nothing rests on its top (parapets): the top sags and both top edges are worn
        public List<OpeningSpec> openings = new();
        public bool lined = true;     // the inner face is a room wall: clean interior paint (off for parapets and bare-block rooms)
        [NonSerialized] public float dirt; // weathering pass: how much dirtier/greyer this whole wall is (vertex colour B)
    }

    [Serializable]
    public class BoxSpec
    {
        public string name;
        public Vector3 min, max;
        public Slot slot = Slot.Render;
        public float chamfer;         // 0 = sharp box
        public bool collide = true;
        public bool detail;           // LOD0 only
        public SagKind sag;           // hand-built imperfection: this part's top or bottom edge sags
        public string sagGroup;       // parts in one group share one sag (e.g. a slab and its drip), so they stay together
    }

    [Serializable]
    public class CylinderSpec
    {
        public string name;
        public Vector3 a, b;
        public float radius = 0.06f;
        public Slot slot = Slot.Metal;
        public bool detail;
    }

    [Serializable]
    public class RailingSpec
    {
        public string name;
        public Vector3 a, b;          // base line (on the floor it stands on)
        public float height = 1.0f;
        public float barSpacing = 0.11f;
    }

    [Serializable]
    public class StairSpec
    {
        public string name;
        public float x0, x1;          // width across X; the stair rises toward +Z
        public float zStart, zTop;    // front edge of the first step, back of the top step
        public float yBase, yTop;
        public int risers = 18;
        public Slot bodySlot = Slot.Render, tileSlot = Slot.Tile;
    }

    [Serializable]
    public class PergolaSpec
    {
        public string name;
        public float x0, x1, z0, z1;  // z0 = street side
        public float yUnder;          // underside of the rafters
        public float rafterSpacing = 0.6f;
        public List<Vector3> posts = new(); // (x, yBase, z) of each post
        // Filled by the imperfection pass before building (hand-cut, uneven rafters):
        [NonSerialized] public List<float> rafterX;
        [NonSerialized] public List<float> rafterTwist; // degrees about the rafter's long axis
        [NonSerialized] public List<float> rafterShade; // 0 = bleached grey .. 1 = brown
    }

    [Serializable]
    public class QuoinSpec
    {
        public string name;
        public Vector3 corner;        // (x, 0, z) of the outer corner
        public Vector3 dirA, dirB;    // unit directions along the two faces, away from the corner
        public float y0, y1;
    }

    [Serializable]
    public class PrismSpec
    {
        public string name;
        public float x0, x1;
        public List<Vector2> zy = new();
        public Slot slot = Slot.Render;
    }

    public enum DecalKind { Damp, Grime, Rust, Streak, Peel, Chip, Crack }

    // A URP decal projected into a facade: weathering that a tiling texture can't place (damp bands, streaks).
    [Serializable]
    public class DecalSpec
    {
        public string name;
        public DecalKind kind;
        public Facing facing;          // the facade it lands on
        public Vector3 center;         // on the facade surface
        public float width, height, depth = 0.12f; // shallow: covers the face (and a 3 cm plinth), never reaches far into jambs
        public float opacity = 1f;
        // Tiling is not set here: the builder derives it from the texture's width/height so texels stay square.
        public Vector3 direction;      // outward direction, overrides `facing` when set (diagonal onto a corner)
        public int variant = -1;       // cell of a 4-cell atlas decal (every kind but Rust)
        public bool flipX, flipY;
        public float roll;             // degrees about the projection axis
        public int layer;              // 0/1: overlapping stains of one kind alternate between two draw orders (no sort flicker)
        public float angleFadeStart = 30f, angleFadeEnd = 60f;
        public float drawDistance = 120f;
    }

    // Hand-built imperfections, applied to every house by default (project rule). Kept subtle: noticed only as
    // "it feels real". Walkable surfaces stay flat, the collision mesh stays simple boxes.
    [Serializable]
    public class ImperfectionSettings
    {
        public bool enabled = true;
        public float gridLod0 = 1f, gridLod0Y = 2f, gridLod1 = 2f; // subdivision of warped surfaces (global grid, m)
        public float leanMax = 0.03f;                    // out of plumb at leanHeight (value noise rarely reaches it)
        public float leanHeight = 6f, leanWavelength = 7f;
        public float bulgeAmp = 0.007f, bulgeWavelength = 4f; // soft and wide; fine unevenness is the plaster's macro normal
        public float parapetSag = 0.02f, roofEdgeSag = 0.015f, slabSag = 0.012f, pergolaSag = 0.015f;
        public float edgeRadius = 0.02f, edgeRadiusJitter = 0.005f; // worn convex edges
        public float openingJitter = 0.015f;             // window/door size and position (thresholds never move)
        public float rafterSpacingJitter = 0.04f, rafterTwistDeg = 1.5f, twistedRafterShare = 0.35f;
        public float rustAmount = 1f;                    // rust at railing joints and bases
        public float chipsPer3m = 1.5f;                  // chipped plaster per 3 m of exposed corner
        public float crackChance = 0.5f;                 // hairline crack per opening corner
        // Shared plaster material (every house uses it): soft bumps and repaired patches.
        public static float PlasterBumps = 1f, PlasterPatches = 1f;
    }

    [Serializable]
    public class HouseSpec
    {
        public string id;
        public string reference;      // reference photo folder
        public int seed;              // 0 = derived from id. Drives every random choice, so a rebuild gives the same house
        public Facing streetFacing = Facing.South; // the traffic side: heaviest road splash
        public List<WallSpec> walls = new();
        public List<BoxSpec> boxes = new();
        public List<CylinderSpec> cylinders = new();
        public List<RailingSpec> railings = new();
        public List<StairSpec> stairs = new();
        public List<PergolaSpec> pergolas = new();
        public List<QuoinSpec> quoins = new();
        public List<PrismSpec> prisms = new();
        public List<DecalSpec> decals = new();
        public ImperfectionSettings imperfections = new();
    }

    public static class HouseSpecs
    {
        // houses/white_house_crenellated_balcony/02 + 03 (01 is a neighbour). Two storeys, whitewash with blue trim,
        // a long balcony with a slotted parapet over a porch, a tiled outside stair under a timber roof.
        // No reference for the back and the east side: built in the style of the front.
        public static HouseSpec BlueTrimWhite()
        {
            const float porch = 0.25f, floor1 = 3.05f, ceil1 = 5.85f, roofTop = 6.05f; // porch clear height ~2.6 m (03)
            const float balZ = -1.95f;                     // street edge of the balcony slab
            const float xL = 0f, xQ = 5.5f, xR = 9.2f;     // left end, the quoin corner, right end
            const float zBack = 7f, zRecess = 1.2f;        // back wall, recessed upper wall on the right

            var s = new HouseSpec { id = "House_BlueTrimWhite", reference = "houses/white_house_crenellated_balcony (02, 03)" };

            // ---- Walls ----
            s.walls.Add(new WallSpec
            {
                name = "West", splitY = floor1 - 0.3f, facing = Facing.West, plane = xL, min = balZ, max = zBack, y0 = -0.2f, y1 = ceil1,
                openings =
                {
                    Win(3.0f, 4.0f, 3.95f, 5.15f, ShutterKind.LouvredOpen),      // upper window, white louvred shutters (02)
                    Win(0.4f, 1.0f, 1.1f, 2.1f, ShutterKind.None, 0.9f),       // small ground-floor window (02); a shallow corner room, clear of the one behind the porch window
                },
            });
            s.walls.Add(new WallSpec
            {
                name = "South_Left", splitY = floor1 - 0.3f, facing = Facing.South, plane = 0f, min = xL + 0.3f, max = xQ, y0 = -0.2f, y1 = ceil1,
                openings =
                {
                    Win(0.9f, 1.9f, 3.95f, 5.15f, ShutterKind.LouvredOpen),      // upper window (03)
                    new OpeningSpec { kind = OpeningKind.GlazedDoor, w0 = 2.6f, w1 = 3.7f, y0 = floor1 + 0.02f, y1 = 5.3f,
                        doorStyle = DoorStyle.AluminiumGlazed, curtain = CurtainKind.TiedLeft, openAngle = 80f }, // 1.1 m (user: a little wider, twice), half open // white aluminium balcony door, curtain tied aside (03); threshold 5 mm above the tiles
                    Win(1.4f, 1.9f, 1.25f, 2.05f, ShutterKind.None),           // small blue-framed window under the balcony (03)
                },
            });
            s.walls.Add(new WallSpec
            {
                name = "South_RightGround", splitY = floor1 - 0.3f, facing = Facing.South, plane = 0f, min = xQ, max = xR, y0 = -0.2f, y1 = floor1 - 0.3f,
                openings = { new OpeningSpec { kind = OpeningKind.Void, w0 = 7.65f, w1 = 8.75f, y0 = porch, y1 = 2.4f, surround = false,
                    doorStyle = DoorStyle.Open, frameSlot = Slot.Wood, interiorDepth = 2.0f } }, // open doorway into the dim store under the terrace (03)
            });
            s.walls.Add(new WallSpec { name = "East_QuoinReturn", facing = Facing.East, plane = xQ, min = 0.3f, max = zRecess + 0.3f, y0 = floor1 - 0.2f, y1 = ceil1 });
            s.walls.Add(new WallSpec
            {
                name = "South_RightUpper", facing = Facing.South, plane = zRecess, min = xQ, max = xR - 0.3f, y0 = floor1 - 0.2f, y1 = ceil1,
                openings =
                {
                    new OpeningSpec { kind = OpeningKind.GlazedDoor, w0 = 6.1f, w1 = 7.2f, y0 = floor1 + 0.015f, y1 = 5.25f, openAngle = 110f, // 1.1 m, wide open
                        surround = false, frameSlot = Slot.Wood, shutters = ShutterKind.LouvredOpen, shutterSlot = Slot.Wood,
                        doorStyle = DoorStyle.WoodGlazed, hingeLeft = false }, // brown glazed wooden door + louvred shutters (03)
                    Win(7.9f, 8.7f, 4.05f, 5.15f, ShutterKind.None),
                },
            });
            s.walls.Add(new WallSpec { name = "East_UnderTerrace", splitY = floor1, facing = Facing.East, plane = xR, min = 0.3f, max = zRecess, y0 = -0.2f, y1 = floor1 - 0.3f });
            s.walls.Add(new WallSpec
            {
                name = "East", splitY = floor1 - 0.3f, facing = Facing.East, plane = xR, min = zRecess, max = zBack, y0 = -0.2f, y1 = ceil1,
                openings = { Win(4.0f, 5.0f, 3.95f, 5.15f, ShutterKind.LouvredOpen), Win(4.2f, 5.0f, 1.2f, 2.2f, ShutterKind.None) },
            });
            s.walls.Add(new WallSpec
            {
                name = "North", splitY = floor1 - 0.3f, facing = Facing.North, plane = zBack, min = xL + 0.3f, max = xR - 0.3f, y0 = -0.2f, y1 = ceil1,
                openings =
                {
                    Win(1.5f, 2.5f, 3.95f, 5.15f, ShutterKind.LouvredOpen), Win(6.0f, 7.0f, 3.95f, 5.15f, ShutterKind.LouvredOpen),
                    Win(2.0f, 3.0f, 1.2f, 2.3f, ShutterKind.None), Win(6.5f, 7.5f, 1.2f, 2.3f, ShutterKind.None),
                },
            });

            // ---- Balcony parapet with blue slots (03): irregular slot heights as in the photo ----
            var parapet = new WallSpec { name = "Parapet", facing = Facing.South, plane = balZ, min = xL + 0.3f, max = 7.25f, y0 = floor1, y1 = floor1 + 1.0f, thickness = 0.15f, topFree = true, lined = false };
            float[] slotX = { 0.55f, 0.95f, 1.75f, 2.35f, 2.95f, 3.55f, 4.35f, 4.95f, 5.6f, 6.3f, 6.85f };
            float[] slotH = { 0.40f, 0.30f, 0.45f, 0.30f, 0.42f, 0.36f, 0.45f, 0.30f, 0.40f, 0.34f, 0.44f };
            for (int i = 0; i < slotX.Length; i++)
                parapet.openings.Add(new OpeningSpec
                {
                    kind = OpeningKind.Slot, w0 = slotX[i], w1 = slotX[i] + 0.07f,
                    y0 = floor1 + 0.75f - slotH[i], y1 = floor1 + 0.75f, surround = false,
                });
            s.walls.Add(parapet);

            // ---- Floors inside (the house is walkable; nothing else inside yet) ----
            // Ground floor at porch level (the doorway's threshold); the upper floor slab, tiled like the balcony, is
            // L-shaped round the recessed upper wall and the quoin return (the balcony tiles already run in to zRecess). The slab runs under the recessed wall and the quoin return,
            // 1 cm short of their outer faces, so the ground-floor ceiling has no slot.
            s.boxes.Add(new BoxSpec { name = "Floor0", min = new Vector3(xL + 0.3f, -0.2f, 0.3f), max = new Vector3(xR - 0.3f, porch, zBack - 0.3f), slot = Slot.Concrete, collide = true });
            s.boxes.Add(new BoxSpec { name = "Floor1_Back", min = new Vector3(xL + 0.3f, floor1 - 0.3f, zRecess + 0.01f), max = new Vector3(xR - 0.25f, floor1, zBack - 0.3f), slot = Slot.InteriorWall, collide = true }); // east end tucked 5 cm into the east wall (no coplanar end faces)
            s.boxes.Add(new BoxSpec { name = "Floor1_Front", min = new Vector3(xL + 0.25f, floor1 - 0.3f, 0.25f), max = new Vector3(xQ - 0.01f, floor1, zRecess + 0.01f), slot = Slot.InteriorWall, collide = true }); // west and south edges tucked into the walls
            s.boxes.Add(new BoxSpec { name = "Floor1_TilesBack", min = new Vector3(xL + 0.3f, floor1, zRecess + 0.3f), max = new Vector3(xR - 0.3f, floor1 + 0.015f, zBack - 0.3f), slot = Slot.Tile, collide = false });
            s.boxes.Add(new BoxSpec { name = "Floor1_TilesFront", min = new Vector3(xL + 0.3f, floor1, zRecess), max = new Vector3(xQ - 0.3f, floor1 + 0.015f, zRecess + 0.3f), slot = Slot.Tile, collide = false });

            // Upstairs ceiling: a painted skim 1 cm under the roof slab (whose underside is the plain eave outside).
            s.boxes.Add(new BoxSpec { name = "Ceiling1_Back", min = new Vector3(xL + 0.25f, ceil1 - 0.012f, zRecess + 0.01f), max = new Vector3(xR - 0.25f, ceil1 - 0.002f, zBack - 0.25f), slot = Slot.InteriorWall, collide = false });
            s.boxes.Add(new BoxSpec { name = "Ceiling1_Front", min = new Vector3(xL + 0.25f, ceil1 - 0.012f, 0.25f), max = new Vector3(xQ - 0.01f, ceil1 - 0.002f, zRecess + 0.01f), slot = Slot.InteriorWall, collide = false });

            s.boxes.Add(new BoxSpec { name = "Ceiling0_UnderTerrace", min = new Vector3(xQ - 0.01f, floor1 - 0.312f, 0.25f), max = new Vector3(xR - 0.25f, floor1 - 0.302f, zRecess + 0.01f), slot = Slot.InteriorWall, collide = false }); // ground-floor ceiling under the terrace slab

            // ---- Slabs, columns, roof ----
            s.boxes.Add(new BoxSpec { name = "PorchFloor", min = new Vector3(xL + 0.3f, -0.2f, balZ), max = new Vector3(xR, porch, 0f), slot = Slot.Concrete, chamfer = 0.02f });
            s.boxes.Add(new BoxSpec { name = "BalconySlab", min = new Vector3(xL + 0.3f, floor1 - 0.3f, balZ), max = new Vector3(xR, floor1, 0f), chamfer = 0.04f, sag = SagKind.Bottom, sagGroup = "BalconyEdge" }); // deep, heavy 1970s slab (01-03)
            s.boxes.Add(new BoxSpec { name = "BalconyDrip", min = new Vector3(xL + 0.29f, floor1 - 0.36f, balZ + 0.01f), max = new Vector3(xR - 0.01f, floor1 - 0.29f, balZ + 0.13f), chamfer = 0.015f, collide = false, sag = SagKind.Bottom, sagGroup = "BalconyEdge" });
            s.boxes.Add(new BoxSpec { name = "TerraceSlab", min = new Vector3(xQ, floor1 - 0.3f, 0f), max = new Vector3(xR, floor1, zRecess) });
            s.boxes.Add(new BoxSpec { name = "BalconyTiles", min = new Vector3(xL + 0.29f, floor1, balZ + 0.15f), max = new Vector3(xR, floor1 + 0.015f, zRecess), slot = Slot.Tile, collide = false });
            s.boxes.Add(new BoxSpec { name = "Column_Mid", min = new Vector3(3.45f, porch, balZ + 0.02f), max = new Vector3(3.75f, floor1 - 0.3f, balZ + 0.32f), chamfer = 0.02f });
            s.boxes.Add(new BoxSpec { name = "Pillar_StairEnd", min = new Vector3(7.25f, porch, balZ - 0.01f), max = new Vector3(7.55f, floor1 + 1.15f, balZ + 0.3f), chamfer = 0.015f });
            s.boxes.Add(new BoxSpec { name = "Roof_Left", min = new Vector3(xL - 0.1f, ceil1, balZ - 0.05f), max = new Vector3(xQ, roofTop, zBack + 0.1f), chamfer = 0.02f });
            s.boxes.Add(new BoxSpec { name = "Roof_Right", min = new Vector3(xQ, ceil1, zRecess - 0.1f), max = new Vector3(xR + 0.1f, roofTop, zBack + 0.1f), chamfer = 0.02f });
            // Blue fascia along the overhang's street edge and its east end (02, 03)
            s.boxes.Add(new BoxSpec { name = "Fascia_Front", min = new Vector3(xL - 0.11f, ceil1 - 0.01f, balZ - 0.07f), max = new Vector3(xQ + 0.04f, roofTop + 0.01f, balZ - 0.03f), slot = Slot.PaintBlue, collide = false, sag = SagKind.Bottom });
            s.boxes.Add(new BoxSpec { name = "Fascia_End", min = new Vector3(xQ, ceil1 - 0.01f, balZ - 0.03f), max = new Vector3(xQ + 0.04f, roofTop + 0.01f, 0f), slot = Slot.PaintBlue, collide = false });
            // Low roof upstand
            s.boxes.Add(new BoxSpec { name = "Upstand_W", min = new Vector3(xL - 0.1f, roofTop, balZ - 0.05f), max = new Vector3(xL + 0.05f, roofTop + 0.18f, zBack + 0.1f), chamfer = 0.01f, sag = SagKind.Top });
            s.boxes.Add(new BoxSpec { name = "Upstand_N", min = new Vector3(xL + 0.05f, roofTop, zBack - 0.05f), max = new Vector3(xR - 0.05f, roofTop + 0.18f, zBack + 0.1f), chamfer = 0.01f, sag = SagKind.Top });
            s.boxes.Add(new BoxSpec { name = "Upstand_E", min = new Vector3(xR - 0.05f, roofTop, zRecess - 0.1f), max = new Vector3(xR + 0.1f, roofTop + 0.18f, zBack + 0.1f), chamfer = 0.01f, sag = SagKind.Top });
            s.boxes.Add(new BoxSpec { name = "Upstand_S", min = new Vector3(xL + 0.05f, roofTop, balZ - 0.05f), max = new Vector3(xQ, roofTop + 0.18f, balZ + 0.1f), chamfer = 0.01f, sag = SagKind.Top });
            s.boxes.Add(new BoxSpec { name = "Upstand_SR", min = new Vector3(xQ, roofTop, zRecess - 0.1f), max = new Vector3(xR - 0.05f, roofTop + 0.18f, zRecess + 0.05f), chamfer = 0.01f, sag = SagKind.Top });
            // Kerb along the porch front
            s.boxes.Add(new BoxSpec { name = "PorchKerb", min = new Vector3(xL, -0.05f, balZ - 0.14f), max = new Vector3(xR, porch + 0.02f, balZ), slot = Slot.RenderOld, chamfer = 0.03f }); // whitewashed kerb (02)

            // ---- Rooftop: bare concrete-block room and the white flue chimney (02) ----
            s.walls.Add(new WallSpec { name = "RoofRoom_S", facing = Facing.South, plane = 4.4f, min = 0.6f, max = 3.2f, y0 = roofTop, y1 = roofTop + 2.2f, thickness = 0.2f, slot = Slot.Concrete,
                openings = { new OpeningSpec { kind = OpeningKind.Door, w0 = 2.05f, w1 = 3.15f, y0 = roofTop, y1 = roofTop + 1.95f, surround = false, frameSlot = Slot.Wood, doorStyle = DoorStyle.WoodPlank, interior = false } }, lined = false });
            s.walls.Add(new WallSpec { name = "RoofRoom_W", facing = Facing.West, plane = 0.4f, min = 4.4f, max = 6.9f, y0 = roofTop, y1 = roofTop + 2.2f, thickness = 0.2f, slot = Slot.Concrete, lined = false });
            s.walls.Add(new WallSpec { name = "RoofRoom_E", facing = Facing.East, plane = 3.4f, min = 4.4f, max = 6.9f, y0 = roofTop, y1 = roofTop + 2.2f, thickness = 0.2f, slot = Slot.Concrete, lined = false });
            s.walls.Add(new WallSpec { name = "RoofRoom_N", facing = Facing.North, plane = 6.9f, min = 0.6f, max = 3.2f, y0 = roofTop, y1 = roofTop + 2.2f, thickness = 0.2f, slot = Slot.Concrete, lined = false });
            s.boxes.Add(new BoxSpec { name = "RoofRoom_Slab", min = new Vector3(0.3f, roofTop + 2.2f, 4.3f), max = new Vector3(3.5f, roofTop + 2.35f, 7.0f), slot = Slot.Concrete, chamfer = 0.015f });
            s.cylinders.Add(new CylinderSpec { name = "Chimney", a = new Vector3(0.75f, roofTop + 0.05f, 3.7f), b = new Vector3(0.75f, roofTop + 2.6f, 3.7f), radius = 0.15f, slot = Slot.Concrete });
            s.boxes.Add(new BoxSpec { name = "ChimneyBase", min = new Vector3(0.5f, roofTop, 3.45f), max = new Vector3(1.0f, roofTop + 0.35f, 3.95f), chamfer = 0.02f, collide = false });

            // ---- Stove flue (black steel, 02/03), corner downpipe (02) ----
            s.cylinders.Add(new CylinderSpec { name = "Flue_Up", a = new Vector3(0.45f, 5.45f, -0.3f), b = new Vector3(0.45f, roofTop + 1.4f, -0.3f), radius = 0.065f, slot = Slot.Metal });
            s.cylinders.Add(new CylinderSpec { name = "Flue_Out", a = new Vector3(0.45f, 5.45f, 0.05f), b = new Vector3(0.45f, 5.45f, -0.3f), radius = 0.065f, slot = Slot.Metal });
            s.cylinders.Add(new CylinderSpec { name = "Flue_Cap", a = new Vector3(0.45f, roofTop + 1.4f, -0.3f), b = new Vector3(0.45f, roofTop + 1.55f, -0.3f), radius = 0.11f, slot = Slot.Metal, detail = true });
            s.cylinders.Add(new CylinderSpec { name = "Downpipe", a = new Vector3(-0.08f, 0.1f, balZ + 0.25f), b = new Vector3(-0.08f, ceil1, balZ + 0.25f), radius = 0.045f, slot = Slot.Concrete, detail = true });

            // ---- Quoins at the step in the upper floor (03) ----
            s.quoins.Add(new QuoinSpec { name = "Quoins", corner = new Vector3(xQ, 0f, 0f), dirA = Vector3.left, dirB = Vector3.forward, y0 = floor1 + 0.02f, y1 = ceil1 });

            // ---- Outside stair (03): tiled treads and risers, rendered side wall ----
            // Stair 1.25 m wide. The flight (17 x 0.18 m rise, 0.286 m going) ends at zLanding on a landing level with the
            // terrace, so the walker steps off straight onto it through a 1.5 m opening (it used to end against the wall).
            const float zLanding = 0f, zStairStart = zLanding - 17 * 0.286f, xStairOut = xR + 1.25f;
            s.stairs.Add(new StairSpec { name = "Stair", x0 = xR, x1 = xStairOut, zStart = zStairStart, zTop = zLanding, yBase = 0f, yTop = floor1, risers = 17 });
            s.prisms.Add(new PrismSpec
            {
                name = "StairWall", x0 = xStairOut, x1 = xStairOut + 0.2f,
                zy = { new Vector2(zStairStart, -0.2f), new Vector2(zRecess + 0.15f, -0.2f), new Vector2(zRecess + 0.15f, floor1 + 1.0f), new Vector2(zLanding, floor1 + 1.0f), new Vector2(zStairStart, 1.0f) },
            });

            // Landing at the top of the flight, tiled like the terrace, closed at the back by a rendered wall.
            s.boxes.Add(new BoxSpec { name = "Landing", min = new Vector3(xR, -0.2f, zLanding), max = new Vector3(xStairOut, floor1, zRecess) });
            s.boxes.Add(new BoxSpec { name = "LandingTiles", min = new Vector3(xR, floor1, zLanding), max = new Vector3(xStairOut, floor1 + 0.015f, zRecess), slot = Slot.Tile, collide = false });
            s.boxes.Add(new BoxSpec { name = "LandingWall", min = new Vector3(xR, -0.2f, zRecess), max = new Vector3(xStairOut, floor1 + 1.0f, zRecess + 0.15f) });

            // ---- Railings: terrace front and the terrace edge above the stair (03) ----
            s.railings.Add(new RailingSpec { name = "Rail_TerraceFront", a = new Vector3(7.55f, floor1 + 0.015f, balZ + 0.06f), b = new Vector3(xR, floor1 + 0.015f, balZ + 0.06f) });
            s.railings.Add(new RailingSpec { name = "Rail_StairEdge", a = new Vector3(xR - 0.05f, floor1 + 0.015f, balZ + 0.105f), b = new Vector3(xR - 0.05f, floor1 + 0.015f, zLanding - 0.3f) });

            // ---- Timber roof over the terrace and stair top (03), one post at the stair foot ----
            s.pergolas.Add(new PergolaSpec
            {
                name = "Pergola", x0 = xQ + 0.04f, x1 = xStairOut + 0.25f, z0 = balZ - 0.05f, z1 = zRecess + 0.1f, yUnder = 5.67f,
                posts = { new Vector3(xStairOut + 0.1f, 1.0f + (balZ + 0.05f - zStairStart) / (zLanding - zStairStart) * floor1 - 0.05f, balZ + 0.05f) },
            });

            // ---- Cement plinth at the base of the outside walls (01, 02) ----
            const float pH = 0.35f, pP = 0.03f;
            s.boxes.Add(new BoxSpec { name = "Plinth_W", min = new Vector3(xL - pP, -0.05f, balZ - pP), max = new Vector3(xL, pH, zBack + pP), slot = Slot.Concrete, chamfer = 0.012f, collide = false });
            s.boxes.Add(new BoxSpec { name = "Plinth_N", min = new Vector3(xL, -0.05f, zBack), max = new Vector3(xR, pH, zBack + pP), slot = Slot.Concrete, chamfer = 0.012f, collide = false });
            s.boxes.Add(new BoxSpec { name = "Plinth_E", min = new Vector3(xR, -0.05f, zRecess + 0.95f), max = new Vector3(xR + pP, pH, zBack + pP), slot = Slot.Concrete, chamfer = 0.012f, collide = false });
            foreach (var (a, b) in new[] { (xL + 0.3f, 7.63f), (8.77f, xR) })
                s.boxes.Add(new BoxSpec { name = "Plinth_Porch", min = new Vector3(a, porch, -pP), max = new Vector3(b, porch + 0.22f, 0f), slot = Slot.Concrete, chamfer = 0.01f, collide = false });

            // ---- Weathering decals ----
            // Damp, road splash, peeling, roof-edge drips and sill streaks are placed by the generator's weathering
            // pass (LockdownHouseBuilder.Weathering.cs), from where water and dirt really go. Only stains tied to one
            // specific object are listed here.
            // Rust running down from the iron railing and the flue (01, 03).
            s.decals.Add(new DecalSpec { name = "Rust_Railing", kind = DecalKind.Rust, facing = Facing.South, center = new Vector3(8.4f, floor1 - 0.35f, balZ), width = 1.7f, height = 0.9f, opacity = 0.9f });
            s.decals.Add(new DecalSpec { name = "Rust_Pillar", kind = DecalKind.Rust, facing = Facing.South, center = new Vector3(7.4f, floor1 + 0.4f, balZ - 0.01f), width = 0.28f, height = 1.4f, opacity = 0.6f });
            s.decals.Add(new DecalSpec { name = "Rust_Flue", kind = DecalKind.Rust, facing = Facing.South, center = new Vector3(0.45f, 4.9f, 0f), width = 0.5f, height = 1.1f, opacity = 0.7f });

            return s;
        }

        static OpeningSpec Win(float w0, float w1, float y0, float y1, ShutterKind shutters, float interiorDepth = 0f) => new()
        {
            kind = OpeningKind.Window, w0 = w0, w1 = w1, y0 = y0, y1 = y1, shutters = shutters, sill = true, interiorDepth = interiorDepth,
        };
    }
}
