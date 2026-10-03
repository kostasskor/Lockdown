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

            var rect = new Rect(12, 8, 420, 52);
            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.DrawTexture(new Rect(6, 6, 330, walker != null ? 50 : 28), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(rect, _text.ToString(), _style);
        }
    }
}
