using UnityEngine;
using UnityEngine.InputSystem;

namespace Lockdown
{
    /// <summary>
    /// First-person review walker at BO2 player scale: walk, sprint, crouch, jump, mouse look,
    /// and a BO2-matched field of view (64° vertical = BO2 FOV 80, toggle to 51° = BO2 FOV 65).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class LockdownWalker : MonoBehaviour
    {
        [Header("Scale")]
        public float standHeight = 1.8f;
        public float standEyeHeight = 1.6f;
        public float crouchHeight = 1.2f;
        public float crouchEyeHeight = 1.05f;
        public float radius = 0.35f;
        public float crouchTransitionSpeed = 8f;

        [Header("Speed (m/s)")]
        public float walkSpeed = 4.5f;
        public float sprintSpeed = 6.5f;
        public float crouchSpeed = 2.2f;
        public float acceleration = 40f;
        public float airControl = 0.3f;

        [Header("Jump")]
        public float jumpHeight = 1.0f;
        public float gravity = -20f;

        [Header("Look")]
        public Camera viewCamera;
        public float mouseSensitivity = 0.1f;
        public float gamepadLookSpeed = 180f;
        public float pitchLimit = 85f;

        [Header("Field of view (vertical)")]
        public float fovBo2_80 = 64f;
        public float fovBo2_65 = 51f;

        public bool IsCrouching { get; private set; }
        public bool IsSprinting { get; private set; }
        public bool IsWideFov { get; private set; } = true;
        public float HorizontalSpeed { get; private set; }

        CharacterController _controller;
        InputAction _move, _look, _jump, _sprint, _crouch, _fov;
        Vector3 _velocity;
        float _pitch;
        float _currentHeight;

        void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _controller.radius = radius;
            _controller.slopeLimit = 36f; // BO2 metric: ramps up to 35° must be walkable
            _controller.stepOffset = 0.4f;
            _controller.skinWidth = 0.03f;
            _currentHeight = standHeight;
            ApplyHeight(standHeight);
            if (viewCamera == null) viewCamera = GetComponentInChildren<Camera>();
            ApplyFov();
            CreateActions();
        }

        void CreateActions()
        {
            _move = new InputAction("Move", InputActionType.Value);
            _move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
            _move.AddBinding("<Gamepad>/leftStick");

            _look = new InputAction("Look", InputActionType.Value);
            _look.AddBinding("<Mouse>/delta");
            _look.AddBinding("<Gamepad>/rightStick");

            _jump = new InputAction("Jump", InputActionType.Button);
            _jump.AddBinding("<Keyboard>/space");
            _jump.AddBinding("<Gamepad>/buttonSouth");

            _sprint = new InputAction("Sprint", InputActionType.Button);
            _sprint.AddBinding("<Keyboard>/leftShift");
            _sprint.AddBinding("<Gamepad>/leftStickPress");

            _crouch = new InputAction("Crouch", InputActionType.Button);
            _crouch.AddBinding("<Keyboard>/c");
            _crouch.AddBinding("<Keyboard>/leftCtrl");
            _crouch.AddBinding("<Gamepad>/buttonEast");

            _fov = new InputAction("ToggleFov", InputActionType.Button);
            _fov.AddBinding("<Keyboard>/f");
            _fov.AddBinding("<Gamepad>/select");
        }

        void OnEnable()
        {
            if (_move == null) return;
            _move.Enable(); _look.Enable(); _jump.Enable(); _sprint.Enable(); _crouch.Enable(); _fov.Enable();
        }

        void OnDisable()
        {
            if (_move == null) return;
            _move.Disable(); _look.Disable(); _jump.Disable(); _sprint.Disable(); _crouch.Disable(); _fov.Disable();
        }

        void OnDestroy()
        {
            _move?.Dispose(); _look?.Dispose(); _jump?.Dispose(); _sprint?.Dispose(); _crouch?.Dispose(); _fov?.Dispose();
        }

        void Update()
        {
            UpdateCursor();
            if (_fov.WasPressedThisFrame()) { IsWideFov = !IsWideFov; ApplyFov(); }
            if (_crouch.WasPressedThisFrame())
            {
                if (IsCrouching) { if (CanStand()) IsCrouching = false; }
                else IsCrouching = true;
            }

            UpdateLook();
            UpdateHeight();
            UpdateMovement();
        }

        void UpdateCursor()
        {
            var mouse = Mouse.current;
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        void UpdateLook()
        {
            bool locked = Cursor.lockState == CursorLockMode.Locked;
            Vector2 delta = Vector2.zero;
            if (_look.activeControl != null && _look.activeControl.device is Gamepad)
                delta = _look.ReadValue<Vector2>() * gamepadLookSpeed * Time.deltaTime;
            else if (locked)
                delta = _look.ReadValue<Vector2>() * mouseSensitivity;

            transform.Rotate(0f, delta.x, 0f, Space.Self);
            _pitch = Mathf.Clamp(_pitch - delta.y, -pitchLimit, pitchLimit);
            if (viewCamera != null) viewCamera.transform.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        void UpdateHeight()
        {
            float target = IsCrouching ? crouchHeight : standHeight;
            if (Mathf.Approximately(_currentHeight, target)) return;
            _currentHeight = Mathf.MoveTowards(_currentHeight, target, crouchTransitionSpeed * Time.deltaTime);
            ApplyHeight(_currentHeight);
        }

        void ApplyHeight(float height)
        {
            _controller.height = height;
            _controller.center = new Vector3(0f, height * 0.5f, 0f);
            if (viewCamera != null)
            {
                float t = Mathf.InverseLerp(standHeight, crouchHeight, height);
                float eye = Mathf.Lerp(standEyeHeight, crouchEyeHeight, t);
                viewCamera.transform.localPosition = new Vector3(0f, eye, 0f);
            }
        }

        bool CanStand()
        {
            Vector3 bottom = transform.position + Vector3.up * radius;
            Vector3 top = transform.position + Vector3.up * (standHeight - radius);
            int mask = ~(1 << gameObject.layer);
            return !Physics.CheckCapsule(bottom + Vector3.up * 0.05f, top, radius * 0.95f, mask, QueryTriggerInteraction.Ignore);
        }

        void UpdateMovement()
        {
            Vector2 input = Vector2.ClampMagnitude(_move.ReadValue<Vector2>(), 1f);
            IsSprinting = _sprint.IsPressed() && !IsCrouching && input.y > 0.1f && _controller.isGrounded;

            float speed = IsCrouching ? crouchSpeed : (IsSprinting ? sprintSpeed : walkSpeed);
            Vector3 wish = (transform.right * input.x + transform.forward * input.y) * speed;

            Vector3 horizontal = new Vector3(_velocity.x, 0f, _velocity.z);
            float accel = _controller.isGrounded ? acceleration : acceleration * airControl;
            horizontal = Vector3.MoveTowards(horizontal, wish, accel * Time.deltaTime);

            if (_controller.isGrounded)
            {
                if (_velocity.y < 0f) _velocity.y = -2f;
                if (_jump.WasPressedThisFrame())
                {
                    if (IsCrouching) { if (CanStand()) IsCrouching = false; }
                    else _velocity.y = Mathf.Sqrt(2f * jumpHeight * -gravity);
                }
            }
            _velocity.y += gravity * Time.deltaTime;

            _velocity = new Vector3(horizontal.x, _velocity.y, horizontal.z);
            CollisionFlags flags = _controller.Move(_velocity * Time.deltaTime);
            if ((flags & CollisionFlags.Above) != 0 && _velocity.y > 0f) _velocity.y = 0f;

            Vector3 actual = _controller.velocity;
            HorizontalSpeed = new Vector2(actual.x, actual.z).magnitude;
        }

        void ApplyFov()
        {
            if (viewCamera != null) viewCamera.fieldOfView = IsWideFov ? fovBo2_80 : fovBo2_65;
        }
    }
}
