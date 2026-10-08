using UnityEngine;
using UnityEngine.InputSystem;

namespace Lockdown
{
    /// <summary>
    /// First-person review walker at BO2 player scale: walk, sprint, crouch, slide, jump, mouse look,
    /// camera motion (head bob, strafe lean, landing dip, FOV kick) and a BO2-matched field of view
    /// (64° vertical = BO2 FOV 80, toggle to 51° = BO2 FOV 65).
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
        public float crouchTransitionSpeed = 8f; // 1 / smooth time of the stand <-> crouch blend

        [Header("Speed (m/s)")]
        public float walkSpeed = 4.5f;
        public float sprintSpeed = 6.5f;
        public float crouchSpeed = 2.2f;
        public float acceleration = 40f;
        public float deceleration = 55f;
        public float airControl = 0.3f;

        [Header("Jump")]
        public float jumpHeight = 1.0f;
        public float gravity = -20f;
        public float coyoteTime = 0.1f;      // jump still works this long after leaving an edge
        public float jumpBufferTime = 0.12f; // jump pressed this early before landing still counts

        [Header("Slide (crouch while sprinting)")]
        public float slideBoost = 1.25f;         // x sprint speed at the start
        public float slideDeceleration = 6f;     // m/s^2 on average: gentle at first, harder as the speed drops
        public float slideMaxTime = 1.8f;
        public float slideSteerDegPerSec = 45f;
        public float slideCooldown = 0.35f;
        public float slideBufferTime = 0.3f;     // crouch pressed this early before landing slides on landing
        public float slideEyeDrop = 0.15f;       // extra camera drop on top of the crouch

        [Header("Camera motion (off = flat camera for review shots)")]
        public bool cameraMotion = true;
        public float bobCyclesPerMetre = 0.22f;  // left-right cycles per metre walked (walk ~1 Hz = 2 steps/s, sprint ~1.4 Hz)
        public float bobWalk = 0.022f;           // vertical bob amplitude (m)
        public float bobSprint = 0.04f;
        public float bobCrouch = 0.012f;
        public float bobSway = 0.3f;             // sideways sway, as a fraction of the vertical bob
        public float bobPitchDeg = 0.3f;         // nod on each footfall
        public float bobRollDeg = 0.1f;
        public float bobRandomness = 0.25f;      // each step is up to +-25% deeper or shallower
        public float cadenceJitter = 0.1f;       // stride speed drifts +-10%
        public float idleBreath = 0.014f;        // breathing rise and fall while standing (m)
        public float idleBreathHz = 0.3f;
        public float idleSwayDeg = 0.6f;         // slow Perlin drift of the view, always on
        public float idleDrift = 0.012f;         // slow Perlin drift of the eye position while standing (m)
        public float strafeRollDeg = 1.4f;
        public float landingImpulse = 0.25f;     // landing dip per m/s of fall speed
        public float landingMinSpeed = 4.5f;     // below this (about a 0.5 m drop) no dip
        public float jumpDip = 1.0f;             // push-off: the view settles a touch as the legs load, then rises
        public float airPitchPerMps = 0.12f;     // degrees the view tips up while rising and down while falling, per m/s
        public float jumpFovKick = 1.5f;         // degrees added at take-off, fades in 0.5 s
        public float sprintFovKick = 2.5f;       // degrees added to the vertical FOV
        public float slideFovKick = 4f;

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
        public bool IsSliding { get; private set; }
        public bool IsWideFov { get; private set; } = true;
        public float HorizontalSpeed { get; private set; }
        public Vector2 MoveInput { get; private set; }
        public bool IsGrounded => _controller != null && _controller.isGrounded;

        CharacterController _controller;
        InputAction _move, _look, _jump, _sprint, _crouch, _fov;
        InputAction[] _actions; // all of the above, for enable / disable / dispose
        Vector3 _velocity;
        float _pitch;
        float _currentHeight;
        float _heightVel;

        float _coyote, _jumpBuffer;

        Vector3 _slideDir;
        float _slideSpeed, _slideStartSpeed, _slideTime, _slideCooldownLeft, _slideBuffer;
        float _jumpKick, _airPitch, _airPitchVel;

        float _bobPhase, _bobAmp, _bobAmpVel;
        float _seed, _stepTarget = 1f, _stepAmp = 1f, _stepAmpVel;
        int _lastStep;
        float _idleBlend, _idleBlendVel;
        float _roll, _rollVel;
        float _slideBlend, _slideBlendVel;
        float _fovAdd, _fovAddVel;
        float _landPos, _landVel;

        void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _controller.radius = radius;
            _controller.slopeLimit = 36f; // BO2 metric: ramps up to 35° must be walkable
            _controller.stepOffset = 0.4f;
            _controller.skinWidth = 0.03f;
            _seed = Random.value * 100f; // different drift every run
            _currentHeight = standHeight;
            ApplyHeight(standHeight);
            if (viewCamera == null) viewCamera = GetComponentInChildren<Camera>();
            CreateActions();
            UpdateCameraMotion();
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

            _actions = new[] { _move, _look, _jump, _sprint, _crouch, _fov };
        }

        void OnEnable()
        {
            if (_actions == null) return;
            foreach (var a in _actions) a.Enable();
        }

        void OnDisable()
        {
            if (_actions == null) return;
            foreach (var a in _actions) a.Disable();
        }

        void OnDestroy()
        {
            if (_actions == null) return;
            foreach (var a in _actions) a.Dispose();
        }

        void Update()
        {
            UpdateCursor();
            if (_fov.WasPressedThisFrame()) IsWideFov = !IsWideFov;
            if (_crouch.WasPressedThisFrame())
            {
                if (IsSliding) EndSlide(); // stays crouched
                else if (CanSlide()) StartSlide();
                else if (!_controller.isGrounded && SprintingForward()) _slideBuffer = slideBufferTime; // slide as soon as you land
                else if (IsCrouching) { if (CanStand()) IsCrouching = false; }
                else IsCrouching = true;
            }

            UpdateLook();
            UpdateHeight();
            UpdateMovement();
            UpdateCameraMotion();
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
        }

        void UpdateHeight()
        {
            float target = IsCrouching ? crouchHeight : standHeight;
            if (Mathf.Abs(_currentHeight - target) < 0.001f)
            {
                if (_currentHeight != target) { _currentHeight = target; ApplyHeight(target); }
                _heightVel = 0f;
                return;
            }
            float smooth = 1f / Mathf.Max(crouchTransitionSpeed, 0.01f);
            if (IsSliding) smooth *= 0.6f; // drop into the slide quicker
            _currentHeight = Mathf.SmoothDamp(_currentHeight, target, ref _heightVel, smooth);
            ApplyHeight(_currentHeight);
        }

        void ApplyHeight(float height)
        {
            _controller.height = height;
            _controller.center = new Vector3(0f, height * 0.5f, 0f);
        }

        bool CanStand()
        {
            Vector3 bottom = transform.position + Vector3.up * radius;
            Vector3 top = transform.position + Vector3.up * (standHeight - radius);
            int mask = ~(1 << gameObject.layer);
            return !Physics.CheckCapsule(bottom + Vector3.up * 0.05f, top, radius * 0.95f, mask, QueryTriggerInteraction.Ignore);
        }

        bool SprintingForward() => _sprint.IsPressed() && _move.ReadValue<Vector2>().y > 0.1f;

        bool CanSlide() =>
            _controller.isGrounded && !IsCrouching && !IsSliding && _slideCooldownLeft <= 0f
            && SprintingForward() && HorizontalSpeed > walkSpeed * 0.8f;

        void StartSlide()
        {
            _slideBuffer = 0f;
            Vector3 h = new Vector3(_velocity.x, 0f, _velocity.z);
            _slideDir = h.sqrMagnitude > 0.1f ? h.normalized : transform.forward;
            _slideSpeed = Mathf.Max(h.magnitude, sprintSpeed) * slideBoost;
            _slideStartSpeed = _slideSpeed;
            _landVel = Mathf.Min(_landVel, -1.2f); // body drops into the slide
            _slideTime = 0f;
            IsSliding = true;
            IsCrouching = true;
        }

        void EndSlide()
        {
            if (!IsSliding) return;
            IsSliding = false;
            _slideCooldownLeft = slideCooldown;
        }

        void UpdateMovement()
        {
            float dt = Time.deltaTime;
            Vector2 input = Vector2.ClampMagnitude(_move.ReadValue<Vector2>(), 1f);
            MoveInput = input;
            bool grounded = _controller.isGrounded; // from the last Move; it cannot change before the Move below

            if (grounded) _coyote = coyoteTime; else _coyote -= dt;
            _slideCooldownLeft -= dt;
            if (_slideBuffer > 0f)
            {
                _slideBuffer -= dt;
                if (grounded && CanSlide()) StartSlide();
            }
            if (_jump.WasPressedThisFrame()) _jumpBuffer = jumpBufferTime; else _jumpBuffer -= dt;

            IsSprinting = _sprint.IsPressed() && !IsCrouching && !IsSliding && input.y > 0.1f && grounded;

            Vector3 horizontal;
            if (IsSliding)
            {
                _slideTime += dt;
                Vector3 wishDir = transform.right * input.x + transform.forward * input.y;
                if (wishDir.sqrMagnitude > 0.01f)
                    _slideDir = Vector3.RotateTowards(_slideDir, wishDir.normalized, slideSteerDegPerSec * Mathf.Deg2Rad * dt, 0f);
                float lost = 1f - _slideSpeed / Mathf.Max(_slideStartSpeed, 0.01f); // 0 at the start, 1 when stopped
                _slideSpeed = Mathf.Max(0f, _slideSpeed - slideDeceleration * (0.6f + 0.9f * lost) * dt);
                horizontal = _slideDir * _slideSpeed;
                if (!grounded || _slideSpeed <= crouchSpeed + 0.3f || _slideTime >= slideMaxTime) EndSlide();
            }
            else
            {
                float speed = IsCrouching ? crouchSpeed : (IsSprinting ? sprintSpeed : walkSpeed);
                Vector3 wish = (transform.right * input.x + transform.forward * input.y) * speed;
                horizontal = new Vector3(_velocity.x, 0f, _velocity.z);
                float accel = wish.sqrMagnitude > 0.01f ? acceleration : deceleration;
                if (!grounded) accel *= airControl;
                horizontal = Vector3.MoveTowards(horizontal, wish, accel * dt);
            }

            if (grounded && _velocity.y < 0f) _velocity.y = -2f;

            if (_jumpBuffer > 0f && _coyote > 0f)
            {
                if (IsSliding)
                {
                    if (CanStand())
                    {
                        EndSlide();
                        IsCrouching = false;
                        Jump();
                    }
                }
                else if (IsCrouching)
                {
                    if (CanStand()) { IsCrouching = false; _jumpBuffer = 0f; } // jump from crouch only stands up
                }
                else Jump();
            }
            _velocity.y += gravity * dt;

            float vyBeforeMove = _velocity.y;
            _velocity = new Vector3(horizontal.x, _velocity.y, horizontal.z);
            CollisionFlags flags = _controller.Move(_velocity * dt);
            if ((flags & CollisionFlags.Above) != 0 && _velocity.y > 0f) _velocity.y = 0f;

            if (!grounded && _controller.isGrounded)
            {
                float fall = -vyBeforeMove;
                if (fall > landingMinSpeed) _landVel = -Mathf.Min(fall, 14f) * landingImpulse;
            }

            Vector3 actual = _controller.velocity;
            HorizontalSpeed = new Vector2(actual.x, actual.z).magnitude;
            if (IsSliding && HorizontalSpeed < _slideSpeed * 0.5f) EndSlide(); // ran into something
        }

        void Jump()
        {
            _velocity.y = Mathf.Sqrt(2f * jumpHeight * -gravity);
            _jumpBuffer = 0f;
            _coyote = 0f;
            _landVel = -jumpDip;
            _jumpKick = 1f;
        }

        // smooth random value in -1..1, different stream per offset, different every run
        float Noise(float offset, float t) => Mathf.PerlinNoise(_seed + offset, t) * 2f - 1f;

        void UpdateCameraMotion()
        {
            if (viewCamera == null) return;
            float dt = Time.deltaTime;

            float t = Mathf.InverseLerp(standHeight, crouchHeight, _currentHeight);
            float eye = Mathf.Lerp(standEyeHeight, crouchEyeHeight, t);
            Vector3 pos = new Vector3(0f, eye, 0f);
            float rollTarget = 0f;
            float fovTarget = 0f;
            float bobPitch = 0f;
            float swayYaw = 0f;

            if (cameraMotion)
            {
                // head bob: fades in and out with walking, stronger when sprinting, small when crouched
                bool moving = IsGrounded && !IsSliding && HorizontalSpeed > 0.5f;
                float amp = IsCrouching ? bobCrouch : (IsSprinting ? bobSprint : bobWalk);
                _bobAmp = Mathf.SmoothDamp(_bobAmp, moving ? amp : 0f, ref _bobAmpVel, 0.12f);
                if (moving)
                {
                    float jitter = 1f + Noise(0f, Time.time * 0.7f) * cadenceJitter;
                    _bobPhase += HorizontalSpeed * bobCyclesPerMetre * Mathf.PI * 2f * dt * jitter;
                    int step = Mathf.FloorToInt(_bobPhase / Mathf.PI);
                    if (step != _lastStep) // every footfall gets its own depth
                    {
                        _lastStep = step;
                        _stepTarget = 1f + Random.Range(-1f, 1f) * bobRandomness;
                    }
                }
                _stepAmp = Mathf.SmoothDamp(_stepAmp, _stepTarget, ref _stepAmpVel, 0.05f);

                // one footstep = half a cycle: sharp drop at the footfall, soft rise to the top of the stride
                float s = Mathf.Sin(_bobPhase);
                float shape = (Mathf.Sqrt(s * s + 0.0225f) - 0.68f) * 2f;
                float ampRatio = _bobAmp / Mathf.Max(bobWalk, 0.001f);
                pos.x += Mathf.Cos(_bobPhase) * _bobAmp * bobSway;
                pos.y += shape * _bobAmp * _stepAmp;
                bobPitch = -shape * bobPitchDeg * ampRatio * _stepAmp; // head nods down on each footfall
                rollTarget += s * bobRollDeg * ampRatio;

                // standing: breathing and a slow drift, so the view is never frozen
                bool still = IsGrounded && !IsSliding && HorizontalSpeed < 0.3f;
                _idleBlend = Mathf.SmoothDamp(_idleBlend, still ? 1f : 0f, ref _idleBlendVel, 0.3f);
                float breath = Mathf.Sin(Time.time * Mathf.PI * 2f * idleBreathHz);
                float swayWeight = Mathf.Lerp(0.4f, 1f, _idleBlend); // a lighter drift stays on while moving
                pos.y += breath * idleBreath * _idleBlend;
                pos.x += Noise(31.7f, Time.time * 0.3f) * idleDrift * _idleBlend;
                bobPitch += (Noise(0f, Time.time * 0.35f + 50f) * idleSwayDeg + breath * 0.08f * _idleBlend) * swayWeight;
                swayYaw = Noise(17.3f, Time.time * 0.3f) * idleSwayDeg * swayWeight;
                rollTarget += Noise(5.1f, Time.time * 0.25f) * idleSwayDeg * 0.5f * swayWeight;

                // lean into strafing
                rollTarget += -MoveInput.x * strafeRollDeg * (IsGrounded ? 1f : 0.4f);

                // slide: camera drops further, FOV opens
                _slideBlend = Mathf.SmoothDamp(_slideBlend, IsSliding ? 1f : 0f, ref _slideBlendVel, 0.1f);
                pos.y -= slideEyeDrop * _slideBlend;

                // landing dip: a damped spring kicked by the fall speed
                _landVel += (-160f * _landPos - 16f * _landVel) * dt;
                _landPos = Mathf.Clamp(_landPos + _landVel * dt, -0.3f, 0.1f);
                pos.y += _landPos;
                bobPitch += -_landPos * 8f; // the view tips down with every dip: landing, slide entry, push-off

                // in the air the view follows the jump: tips up while rising, down while falling
                float airTarget = IsGrounded ? 0f : -Mathf.Clamp(_velocity.y, -14f, 8f) * airPitchPerMps;
                _airPitch = Mathf.SmoothDamp(_airPitch, airTarget, ref _airPitchVel, 0.12f);
                bobPitch += _airPitch;

                _jumpKick = Mathf.MoveTowards(_jumpKick, 0f, dt / 0.5f);
                fovTarget = (IsSprinting ? sprintFovKick : 0f) + (IsSliding ? slideFovKick : 0f) + jumpFovKick * _jumpKick;
            }

            _roll = Mathf.SmoothDamp(_roll, rollTarget, ref _rollVel, 0.1f);
            _fovAdd = Mathf.SmoothDamp(_fovAdd, fovTarget, ref _fovAddVel, 0.15f);

            viewCamera.transform.localPosition = pos;
            viewCamera.transform.localRotation = Quaternion.Euler(_pitch + bobPitch, swayYaw, _roll);
            viewCamera.fieldOfView = (IsWideFov ? fovBo2_80 : fovBo2_65) + _fovAdd;
        }
    }
}
