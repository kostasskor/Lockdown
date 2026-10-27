using UnityEngine;
using UnityEngine.InputSystem;

namespace Lockdown
{
    /// <summary>
    /// Compass in the top-right corner: a dial that turns with the player's view so the heading always points up,
    /// with N / E / S / W, 15° ticks and the heading in degrees underneath. World +Z is true north and +X east
    /// (see PROGRESS.md "World frame"), so it shows real compass directions. F2 toggles it. Drawn with OnGUI,
    /// so review-camera captures never contain it.
    /// </summary>
    public class LockdownCompass : MonoBehaviour
    {
        public Camera viewCamera;
        public bool visible = true;
        [Tooltip("Dial diameter in pixels at 1080p; scales with the screen height.")]
        public float diameter = 132f;
        public float margin = 16f;

        static readonly string[] Names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        static readonly Color Ink = new Color(1f, 1f, 1f, 0.95f);
        static readonly Color North = new Color(0.95f, 0.25f, 0.2f, 1f);
        GUIStyle _letter, _heading;
        Texture2D _disc, _ring;

        void Awake()
        {
            useGUILayout = false;
            if (viewCamera == null) viewCamera = GetComponentInChildren<Camera>();
            _disc = MakeCircle(128, 0f);
            _ring = MakeCircle(128, 0.035f);
        }

        void OnDestroy()
        {
            if (_disc != null) Destroy(_disc);
            if (_ring != null) Destroy(_ring);
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.f2Key.wasPressedThisFrame) visible = !visible;
        }

        /// <summary>View heading in degrees clockwise from true north (0-360).</summary>
        public float Heading
        {
            get
            {
                Transform t = viewCamera != null ? viewCamera.transform : transform;
                Vector3 f = t.forward;
                f.y = 0f;
                if (f.sqrMagnitude < 1e-6f) f = t.up;   // looking straight up or down: use the camera's top edge
                f.y = 0f;
                return (Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg + 360f) % 360f;
            }
        }

        void OnGUI()
        {
            if (!visible || Event.current.type != EventType.Repaint) return;

            float s = Screen.height / 1080f;
            float d = diameter * s, r = d * 0.5f;
            var c = new Vector2(Screen.width - margin * s - r, margin * s + r);
            float heading = Heading;

            if (_letter == null)
            {
                _letter = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                _heading = new GUIStyle(_letter);
            }
            _letter.fontSize = Mathf.RoundToInt(20 * s);
            _heading.fontSize = Mathf.RoundToInt(17 * s);

            // Dial: dark disc with a thin ring.
            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(new Rect(c.x - r, c.y - r, d, d), _disc);
            GUI.color = new Color(1f, 1f, 1f, 0.55f);
            GUI.DrawTexture(new Rect(c.x - r, c.y - r, d, d), _ring);

            // Ticks every 15 degrees, longer every 45; they turn with the world, not with the view.
            Matrix4x4 saved = GUI.matrix;
            for (int b = 0; b < 360; b += 15)
            {
                bool major = b % 45 == 0;
                float len = (major ? 0.12f : 0.07f) * d, w = (major ? 2.4f : 1.4f) * s;
                GUI.matrix = saved;
                GUIUtility.RotateAroundPivot(b - heading, c);
                GUI.color = b == 0 ? North : new Color(1f, 1f, 1f, major ? 0.9f : 0.5f);
                GUI.DrawTexture(new Rect(c.x - w * 0.5f, c.y - r + 3f * s, w, len), Texture2D.whiteTexture);
            }
            GUI.matrix = saved;

            // Letters stay upright; they sit on a circle inside the ticks and move with the world.
            for (int i = 0; i < 8; i += 2)
            {
                float a = (i * 45f - heading) * Mathf.Deg2Rad;
                Vector2 p = c + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * (r * 0.56f);
                var box = new Rect(p.x - 18f * s, p.y - 14f * s, 36f * s, 28f * s);
                _letter.normal.textColor = i == 0 ? North : Ink;
                GUI.Label(box, Names[i], _letter);
            }

            // Fixed marker at the top: the direction you are facing.
            GUI.color = new Color(1f, 0.85f, 0.3f, 1f);
            GUIUtility.RotateAroundPivot(45f, new Vector2(c.x, c.y - r - 4f * s));
            GUI.DrawTexture(new Rect(c.x - 5f * s, c.y - r - 9f * s, 10f * s, 10f * s), Texture2D.whiteTexture);
            GUI.matrix = saved;
            GUI.color = Color.white;

            // Heading readout under the dial: "NE  047°".
            var label = new Rect(c.x - 60f * s, c.y + r + 2f * s, 120f * s, 24f * s);
            GUI.color = new Color(0f, 0f, 0f, 0.5f);
            GUI.DrawTexture(new Rect(label.x + 14f * s, label.y + 1f * s, label.width - 28f * s, label.height - 2f * s), Texture2D.whiteTexture);
            GUI.color = Color.white;
            _heading.normal.textColor = Ink;
            int deg = Mathf.RoundToInt(heading) % 360;
            GUI.Label(label, Names[Mathf.RoundToInt(heading / 45f) % 8] + "  " + deg.ToString("000") + "°", _heading);
        }

        /// <summary>A filled disc (ringWidth 0) or a ring of the given width, as a fraction of the diameter.</summary>
        static Texture2D MakeCircle(int size, float ringWidth)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            float half = size * 0.5f, outer = half - 1f, inner = ringWidth > 0f ? outer - ringWidth * size : -1f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(half, half));
                float a = Mathf.Clamp01(outer - dist + 0.5f);                       // soft outer edge
                if (inner > 0f) a *= Mathf.Clamp01(dist - inner + 0.5f);            // soft inner edge of the ring
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }
    }
}
