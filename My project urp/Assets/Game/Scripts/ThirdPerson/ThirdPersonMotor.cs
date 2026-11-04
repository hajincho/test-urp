using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.ThirdPerson
{
    [RequireComponent(typeof(CharacterController))]
    public class ThirdPersonMotor : MonoBehaviour
    {
        public Transform cameraTransform;
        public Transform visualRoot;
        public float walkSpeed = 1.2f;
        public bool flightMode = false;
        public float flySpeed = 20f;
        public float fastFlySpeed = 80f;
        public float sprintSpeed = 5.5f;
        public float jumpHeight = .96f;
        public float gravity = -9.81f;
        // The observed flat-ground jump takes about 0.6 s. Apply the quicker arc only to an accepted jump.
        [Min(.1f)] public float jumpGravityScale = 1.9f;
        public bool IsJumping { get; private set; }
        public float JumpProgress => IsJumping && launchSpeed > 0f
            ? Mathf.Clamp01(.5f * (1f - verticalSpeed / launchSpeed)) : 0f;
        float launchSpeed;
        public float turnSpeed = 12f;
        public bool Grounded { get; private set; }
        public float VerticalSpeed => verticalSpeed;
        public float PlanarSpeed { get; private set; }
        public enum Transition { None, SprintStart, SprintStop, WalkStop, AirStop }
        public Transition CurrentTransition { get; private set; }
        public int TransitionSequence { get; private set; }
        public float TransitionProgress => transitionDuration > 0f ? Mathf.Clamp01(transitionTime / transitionDuration) : 1f;
        float transitionTime, transitionDuration;
        Vector3 planarVelocity;
        bool previousMove, previousSprint, airStopped;
        [Min(.01f)] public float groundSnapDistance = .24f;
        public LayerMask groundMask = ~0;
        readonly RaycastHit[] supportHits = new RaycastHit[24];
        Vector3 stableGroundNormal = Vector3.up;

        bool ProbeSupport(out RaycastHit support, out float gap)
        {
            float scale = Mathf.Abs(transform.lossyScale.y);
            float radius = Mathf.Max(.04f, controller.radius * scale * .82f);
            var foot = transform.TransformPoint(controller.center - Vector3.up * controller.height * .5f);
            var origin = foot + Vector3.up * (radius + .18f * scale);
            int count = Physics.SphereCastNonAlloc(origin, radius, Vector3.down, supportHits,
                (.18f + groundSnapDistance) * scale, groundMask, QueryTriggerInteraction.Ignore);
            support = default; gap = float.PositiveInfinity; float nearest = float.PositiveInfinity;
            float minimumY = Mathf.Cos(controller.slopeLimit * Mathf.Deg2Rad);
            for (int i = 0; i < count; i++) {
                var hit = supportHits[i];
                if (!hit.collider || hit.transform.IsChildOf(transform) || hit.normal.y < minimumY || hit.distance >= nearest) continue;
                float difference = foot.y - hit.point.y;
                if (difference < -.08f * scale || difference > groundSnapDistance * scale) continue;
                nearest = hit.distance; support = hit; gap = difference;
            }
            return support.collider;
        }

        void BeginTransition(Transition kind, float duration)
        {
            CurrentTransition = kind;
            TransitionSequence++;
            transitionTime = 0f;
            transitionDuration = duration;
        }

        public float UphillAngle { get; private set; }
        public bool Sprinting { get; private set; }
        CharacterController controller;
        float verticalSpeed;
        Vector3 spawn;
        float lastGrounded = -10f, lastJumpPressed = -10f;
        public bool SimulationInput { get; set; }
        public Vector2 SimulatedMove { get; set; }
        public bool SimulatedSprint { get; set; }
        public bool SimulatedJump { get; set; }

        void Awake() { controller = GetComponent<CharacterController>(); spawn = transform.position; }
        void Update()
        {
            var keyboard = Keyboard.current;
            var pad = Gamepad.current;
            Vector2 input = pad != null ? pad.leftStick.ReadValue() : Vector2.zero;
            if (keyboard != null)
                input += new Vector2((keyboard.dKey.isPressed ? 1 : 0) - (keyboard.aKey.isPressed ? 1 : 0),
                    (keyboard.wKey.isPressed ? 1 : 0) - (keyboard.sKey.isPressed ? 1 : 0));
            bool sprint = (keyboard != null && keyboard.leftShiftKey.isPressed) || (pad != null && pad.leftStickButton.isPressed);
            bool jump = (keyboard != null && keyboard.spaceKey.wasPressedThisFrame) || (pad != null && pad.buttonSouth.wasPressedThisFrame);
            if (SimulationInput) { input = SimulatedMove; sprint = SimulatedSprint; jump = SimulatedJump; SimulatedJump = false; }
            Tick(input, sprint, jump, Time.deltaTime);
        }
        public void TeleportTo(Vector3 destination)
        {
            if (!controller) controller = GetComponent<CharacterController>();
            controller.enabled = false;
            transform.position = destination;
            spawn = destination;
            verticalSpeed = 0f; planarVelocity = Vector3.zero; PlanarSpeed = 0f;
            IsJumping = false; Grounded = false; Sprinting = false;
            CurrentTransition = Transition.None; previousMove = previousSprint = airStopped = false;
            stableGroundNormal = Vector3.up; UphillAngle = 0f;
            lastGrounded = lastJumpPressed = -10f;
            controller.enabled = true;
        }
        public void Tick(Vector2 input, bool sprint, bool jump, float dt)
        {
            if (!controller) controller = GetComponent<CharacterController>();
            if (dt <= 0f) return;
            input = Vector2.ClampMagnitude(input, 1f);
            // Camera yaw remains well-defined even when looking straight up.
            float yaw = cameraTransform ? cameraTransform.eulerAngles.y : 0f;
            Vector3 direction = Quaternion.Euler(0f, yaw, 0f) * new Vector3(input.x, 0f, input.y);
            bool moving = input.sqrMagnitude > .001f;
            bool wasSupported = Grounded;
            Grounded = controller.isGrounded || (!IsJumping && verticalSpeed <= 0f &&
                ProbeSupport(out var earlySupport, out float earlyGap) && earlyGap < .08f * Mathf.Abs(transform.lossyScale.y));
            bool wasJumping = IsJumping;
            transitionTime += dt;
            if (CurrentTransition != Transition.None && transitionTime >= transitionDuration)
                CurrentTransition = Transition.None;
            if (moving)
            {
                if (Grounded && sprint && (!previousMove || !previousSprint))
                    BeginTransition(Transition.SprintStart, .24f);
                else if (Grounded && !sprint && previousSprint && previousMove)
                    BeginTransition(Transition.SprintStop, .42f);
                else if (!previousMove && CurrentTransition != Transition.SprintStart)
                    CurrentTransition = Transition.None;
                airStopped = false;
            }
            else if (previousMove)
            {
                if (!Grounded || IsJumping) airStopped = true;
                else if (PlanarSpeed > walkSpeed * 1.35f) BeginTransition(Transition.SprintStop, .48f);
                else if (PlanarSpeed > .08f) BeginTransition(Transition.WalkStop, .28f);
            }
            if (moving && (CurrentTransition == Transition.WalkStop || CurrentTransition == Transition.AirStop))
                CurrentTransition = Transition.None;
            previousMove = moving;
            previousSprint = sprint;
            if (Grounded) lastGrounded = Time.time;
            if (jump) lastJumpPressed = Time.time;
            if (Grounded && verticalSpeed < 0f) verticalSpeed = -2f;
            if (Time.time - lastJumpPressed < .12f && Time.time - lastGrounded < .1f)
            {
                launchSpeed = Mathf.Sqrt(2f * Mathf.Abs(gravity) * Mathf.Max(.1f, jumpGravityScale) * jumpHeight);
                verticalSpeed = launchSpeed;
                IsJumping = true;
                CurrentTransition = Transition.None;
                airStopped = false;
                lastJumpPressed = -10f; lastGrounded = -10f; Grounded = false;
            }
            float previousVertical = verticalSpeed;
            float acceleration = IsJumping ? gravity * Mathf.Max(.1f, jumpGravityScale) : gravity;
            verticalSpeed = Mathf.Max(verticalSpeed + acceleration * dt, -50f);
            float verticalDisplacement = (previousVertical + verticalSpeed) * .5f * dt;
            Vector3 before = transform.position;
            float targetSpeed = sprint ? sprintSpeed : walkSpeed;
            // The reference has a brief push-off before settling into the running cycle.
            if (CurrentTransition == Transition.SprintStart && moving && Grounded)
                targetSpeed *= 1f + .25f * (1f - Mathf.SmoothStep(0f, 1f, TransitionProgress));
            Vector3 desiredVelocity = direction * targetSpeed;
            float changeTime = moving ? (sprint ? .075f : .10f)
                : (!Grounded ? .16f : planarVelocity.magnitude > walkSpeed * 1.35f ? .20f : .11f);
            float referenceSpeed = moving ? targetSpeed
                : CurrentTransition == Transition.WalkStop ? walkSpeed : sprintSpeed;
            planarVelocity = Vector3.MoveTowards(planarVelocity, desiredVelocity,
                Mathf.Max(.1f, referenceSpeed) * dt / changeTime);
            var flags = controller.Move(planarVelocity * dt + Vector3.up * verticalDisplacement);
            if ((flags & CollisionFlags.Above) != 0 && verticalSpeed > 0f) verticalSpeed = 0f;
            Grounded = (flags & CollisionFlags.Below) != 0;
            RaycastHit support = default;
            if (!IsJumping && verticalSpeed <= 0f && ProbeSupport(out support, out float supportGap)) {
                if ((wasSupported || Grounded) && supportGap > .005f) {
                    flags |= controller.Move(Vector3.down * (supportGap + .012f));
                    Grounded = (flags & CollisionFlags.Below) != 0 || controller.isGrounded;
                }
                if ((wasSupported || Grounded) && supportGap <= .08f * Mathf.Abs(transform.lossyScale.y)) Grounded = true;
            }
            if (Grounded)
            {
                if (wasJumping && airStopped && !moving) BeginTransition(Transition.AirStop, .38f);
                IsJumping = false;
                airStopped = false;
            }
            Vector3 delta = transform.position - before;
            float planarDistance = new Vector2(delta.x, delta.z).magnitude;
            float measuredSpeed = planarDistance / dt;
            PlanarSpeed = Mathf.Lerp(PlanarSpeed, measuredSpeed, 1f - Mathf.Exp(-24f * dt));
            if (measuredSpeed < .015f && PlanarSpeed < .03f) PlanarSpeed = 0f;
            if ((flags & CollisionFlags.Sides) != 0 && measuredSpeed < planarVelocity.magnitude * .6f)
                planarVelocity = new Vector3(delta.x, 0f, delta.z) / dt;
            // A stair/stone edge is not an uphill slope. Use the supporting surface,
            // rather than differentiating the capsule's tiny vertical steps.
            var targetNormal = Grounded && support.collider ? support.normal : Vector3.up;
            stableGroundNormal = Vector3.Slerp(stableGroundNormal, targetNormal, 1f - Mathf.Exp(-8f * dt)).normalized;
            float gradient = -Vector3.Dot(stableGroundNormal, direction) / Mathf.Max(.1f, stableGroundNormal.y);
            float targetSlope = Grounded && moving ? Mathf.Atan(Mathf.Max(0f, gradient)) * Mathf.Rad2Deg : 0f;
            if (targetSlope < 3f) targetSlope = 0f;
            UphillAngle = Mathf.MoveTowards(UphillAngle, targetSlope, 45f * dt);
            Sprinting = sprint && PlanarSpeed > .1f;
            if (direction.sqrMagnitude > .001f && visualRoot)
                visualRoot.rotation = Quaternion.Slerp(visualRoot.rotation, Quaternion.LookRotation(direction), 1f - Mathf.Exp(-turnSpeed * dt));
            if (transform.position.y < spawn.y - 100f)
            {
                controller.enabled = false; transform.position = spawn;
                verticalSpeed = 0f; IsJumping = false; planarVelocity = Vector3.zero;
                CurrentTransition = Transition.None; airStopped = false; controller.enabled = true;
            }
        }
    }
}
