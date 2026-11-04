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
        public void Tick(Vector2 input, bool sprint, bool jump, float dt)
        {
            if (!controller) controller = GetComponent<CharacterController>();
            if (dt <= 0f) return;
            input = Vector2.ClampMagnitude(input, 1f);
            // Camera yaw remains well-defined even when looking straight up.
            float yaw = cameraTransform ? cameraTransform.eulerAngles.y : 0f;
            Vector3 direction = Quaternion.Euler(0f, yaw, 0f) * new Vector3(input.x, 0f, input.y);
            bool moving = input.sqrMagnitude > .001f;
            Grounded = controller.isGrounded;
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
            if (Grounded)
            {
                if (wasJumping && airStopped && !moving) BeginTransition(Transition.AirStop, .38f);
                IsJumping = false;
                airStopped = false;
            }
            Vector3 delta = transform.position - before;
            float planarDistance = new Vector2(delta.x, delta.z).magnitude;
            PlanarSpeed = planarDistance / dt;
            if ((flags & CollisionFlags.Sides) != 0)
                planarVelocity = new Vector3(delta.x, 0f, delta.z) / dt;
            UphillAngle = Grounded && input.sqrMagnitude > .001f && PlanarSpeed > .08f
                ? Mathf.Atan2(Mathf.Max(0f, delta.y), planarDistance) * Mathf.Rad2Deg : 0f;
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
