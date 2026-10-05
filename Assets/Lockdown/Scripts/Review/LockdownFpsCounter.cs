using UnityEngine;
using UnityEngine.InputSystem;

namespace Lockdown
{
    /// <summary>
    /// On-screen fps counter (average over 0.5 s, worst frame in that window) plus walker state.
    /// F1 toggles it. Kept in builds so standalone performance checks can use it.
    /// </summary>
    public class LockdownFpsCounter : MonoBehaviour
    {
        public float sampleWindow = 0.5f;
        public LockdownWalker walker;
        public bool visible = true;

        float _accumTime;
        int _frames;
        float _worstFrame;
        float _fps;
        float _worstMs;
        GUIStyle _style;
        readonly System.Text.StringBuilder _text = new System.Text.StringBuilder(128);

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.f1Key.wasPressedThisFrame) visible = !visible;

            float dt = Time.unscaledDeltaTime;
            _accumTime += dt;
            _frames++;
            if (dt > _worstFrame) _worstFrame = dt;
            if (_accumTime >= sampleWindow)
            {
                _fps = _frames / _accumTime;
                _worstMs = _worstFrame * 1000f;
                _accumTime = 0f;
                _frames = 0;
                _worstFrame = 0f;
            }
        }

        void OnGUI()
        {
            if (!visible) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
                _style.normal.textColor = Color.white;
            }

            _text.Clear();
            _text.Append(_fps.ToString("0")).Append(" fps   worst ").Append(_worstMs.ToString("0.0")).Append(" ms");
            if (walker != null)
            {
                _text.Append("\nFOV ").Append(walker.IsWideFov ? "64° (BO2 80)" : "51° (BO2 65)");
                _text.Append("   ").Append(walker.HorizontalSpeed.ToString("0.0")).Append(" m/s   ");
                _text.Append(walker.IsCrouching ? "crouch" : (walker.IsSprinting ? "sprint" : "stand"));
            }

            // Input readout: which movement keys the game sees, the walker's move value, ground contact.
            var keyboard = Keyboard.current;
            if (walker != null)
            {
                _text.Append("\nkeys ");
                if (keyboard == null) _text.Append("(no keyboard)");
                else
                {
                    _text.Append(keyboard.wKey.isPressed ? 'W' : '-').Append(keyboard.aKey.isPressed ? 'A' : '-')
                         .Append(keyboard.sKey.isPressed ? 'S' : '-').Append(keyboard.dKey.isPressed ? 'D' : '-');
                }
                Vector2 m = walker.MoveInput;
                _text.Append("   move ").Append(m.x.ToString("0.0")).Append(", ").Append(m.y.ToString("0.0"))
                     .Append(walker.IsGrounded ? "   grounded" : "   in air");
            }

            int lines = walker != null ? 3 : 1;
            var rect = new Rect(12, 8, 420, 26 * lines);
            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.DrawTexture(new Rect(6, 6, 330, 24 * lines + 2), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(rect, _text.ToString(), _style);

            // Keyboard and mouse only reach the game once the Game view has focus: say so until it does.
            if (Cursor.lockState != CursorLockMode.Locked)
            {
                var hint = new Rect(0, Screen.height * 0.5f - 20, Screen.width, 40);
                var style = new GUIStyle(_style) { alignment = TextAnchor.MiddleCenter, fontSize = 22 };
                GUI.color = new Color(0f, 0f, 0f, 0.45f);
                GUI.DrawTexture(hint, Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(hint, "Click the Game view to play  (Esc frees the mouse)", style);
            }
        }
    }
}
