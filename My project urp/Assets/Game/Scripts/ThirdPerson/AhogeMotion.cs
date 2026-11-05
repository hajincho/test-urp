using UnityEngine;

namespace Game.ThirdPerson
{
    // Evaluated after the body/hair retarget, without moving the head or hat.
    [DisallowMultipleComponent, DefaultExecutionOrder(198)]
    public sealed class AhogeMotion : MonoBehaviour
    {
        public Transform head;
        public Transform[] joints = new Transform[6];
        [Range(0f, 2f)] public float movementResponse = 1f;
        [Range(0f, 2f)] public float breezeStrength = .75f;
        [Range(1f, 12f)] public float maximumSwayDegrees = 7f;
        [Range(.5f, 6f)] public float maximumCurlDegreesPerJoint = 3.25f;
        public WindZone wind;
        public Vector3 ambientWindDirection = new Vector3(.8f, 0f, .35f);
        [SerializeField] Quaternion[] restLocalRotations;

        ThirdPersonMotor motor;
        UntitledCharacterMotion motion;
        Transform frame;
        WindZone sceneWind;
        readonly Vector2[] delayedSway = new Vector2[6];
        Vector2 sway, swayVelocity;
        float curl, curlVelocity, movementBlend;
        Vector3 previousPosition, previousVelocity;
        Quaternion previousRotation;
        bool initialized;

        public bool IsConfigured => head && joints != null && joints.Length == 6 && restLocalRotations != null && restLocalRotations.Length == 6
            && joints[0] && joints[1] && joints[2] && joints[3] && joints[4] && joints[5];
        public Vector2 SwayAngles => sway;
        public float CurlAmount => curl;
        public float MaximumJointOffset { get; private set; }

        public bool ConfigureRig()
        {
            var all = GetComponentsInChildren<Transform>(true);
            head = System.Array.Find(all, t => t.name == "Head");
            joints = new Transform[6];
            for (int i = 0; i < joints.Length; i++) joints[i] = System.Array.Find(all, t => t.name == "DM_" + i + "_1");
            if (!head || System.Array.Exists(joints, t => !t)) return false;
            restLocalRotations = new Quaternion[6];
            for (int i = 0; i < joints.Length; i++) restLocalRotations[i] = joints[i].localRotation;
            return true;
        }

        void Awake()
        {
            if (!IsConfigured && !ConfigureRig()) { enabled = false; return; }
            motor = GetComponentInParent<ThirdPersonMotor>();
            frame = motor && motor.visualRoot ? motor.visualRoot : head;
            motion = motor ? motor.GetComponentInChildren<UntitledCharacterMotion>(true) : null;
            sceneWind = wind ? wind : FindAnyObjectByType<WindZone>();
        }
        void OnEnable() { initialized = false; }
        void OnDisable()
        {
            if (IsConfigured) for (int i = 0; i < joints.Length; i++) joints[i].localRotation = restLocalRotations[i];
            initialized = false;
        }
        void ResetDynamics(Vector3 position)
        {
            previousPosition = position; previousVelocity = Vector3.zero; previousRotation = frame.rotation;
            sway = swayVelocity = Vector2.zero; curl = curlVelocity = movementBlend = 0f;
            for (int i = 0; i < delayedSway.Length; i++) delayedSway[i] = Vector2.zero;
            initialized = true;
        }
        Vector3 Breeze(float time)
        {
            Vector3 direction = ambientWindDirection.sqrMagnitude > .001f ? ambientWindDirection.normalized : Vector3.right;
            float strength = breezeStrength;
            if (sceneWind && sceneWind.gameObject.activeInHierarchy)
            {
                direction = sceneWind.mode == WindZoneMode.Directional ? sceneWind.transform.forward : head.position - sceneWind.transform.position;
                if (direction.sqrMagnitude > .001f) direction.Normalize();
                float pulse = .15f * sceneWind.windPulseMagnitude * Mathf.Sin(time * Mathf.Max(.1f, sceneWind.windPulseFrequency));
                strength *= Mathf.Clamp(sceneWind.windMain + pulse + .12f * sceneWind.windTurbulence, 0f, 2f);
                if (sceneWind.mode == WindZoneMode.Spherical)
                    strength *= 1f - Mathf.Clamp01(Vector3.Distance(head.position, sceneWind.transform.position) / Mathf.Max(.01f, sceneWind.radius));
            }
            float gust = .7f + .18f * Mathf.Sin(time * .67f) + .12f * Mathf.Sin(time * 1.13f + .8f);
            return (direction * gust + Vector3.Cross(Vector3.up, direction) * (.12f * Mathf.Sin(time * .83f))) * strength;
        }
        void LateUpdate()
        {
            if (!IsConfigured || !frame || Time.deltaTime <= 0f) return;
            float dt = Mathf.Min(Time.deltaTime, .1f);
            Vector3 position = motor ? motor.transform.position : head.position;
            if (!initialized || Vector3.Distance(position, previousPosition) > 1.5f || Time.deltaTime > .25f) ResetDynamics(position);
            Vector3 velocity = (position - previousPosition) / dt;
            Vector3 acceleration = Vector3.ClampMagnitude((velocity - previousVelocity) / dt, 14f);
            float yawRate = Mathf.Clamp(Mathf.DeltaAngle(previousRotation.eulerAngles.y, frame.eulerAngles.y) / dt, -240f, 240f);
            previousPosition = position; previousVelocity = velocity; previousRotation = frame.rotation;
            Vector3 localVelocity = frame.InverseTransformDirection(velocity);
            Vector3 localAcceleration = frame.InverseTransformDirection(acceleration);
            Vector3 localWind = frame.InverseTransformDirection(Breeze(Time.time));
            float speed = motor ? motor.PlanarSpeed : new Vector2(velocity.x, velocity.z).magnitude;
            float activity = Mathf.Clamp01(speed / (motor ? Mathf.Max(.1f, motor.sprintSpeed) : 4f));
            movementBlend = Mathf.Lerp(movementBlend, activity, 1f - Mathf.Exp(-4f * dt));
            float phase = motion ? motion.GaitPhase : Time.time * 7f;
            Vector2 target = new Vector2(
                -localVelocity.z * .7f - localAcceleration.z * .10f + localWind.z * 1.6f,
                localVelocity.x * .55f + localAcceleration.x * .10f - yawRate * .006f + localWind.x * 1.8f);
            target = Vector2.ClampMagnitude(target * movementResponse, maximumSwayDegrees);
            float targetCurl = Mathf.Clamp(movementBlend * (.045f + .018f * Mathf.Sin(phase))
                + breezeStrength * (.007f * Mathf.Sin(Time.time * .91f) + .004f * Mathf.Sin(Time.time * 1.61f))
                + localAcceleration.y * .001f, -.02f, .085f);
            // Short stable steps prevent low editor frame rates from exciting the spring.
            int steps = Mathf.Max(1, Mathf.CeilToInt(dt * 120f)); float h = dt / steps;
            const float frequency = 17f, damping = .72f, curlFrequency = 20f;
            for (int i = 0; i < steps; i++)
            {
                swayVelocity += (frequency * frequency * (target - sway) - 2f * damping * frequency * swayVelocity) * h;
                sway += swayVelocity * h;
                curlVelocity += (curlFrequency * curlFrequency * (targetCurl - curl) - 2f * .76f * curlFrequency * curlVelocity) * h;
                curl += curlVelocity * h;
            }
            sway = Vector2.ClampMagnitude(sway, maximumSwayDegrees); curl = Mathf.Clamp(curl, -.025f, .095f);
            for (int i = 0; i < joints.Length; i++) joints[i].localRotation = restLocalRotations[i];
            Vector3 curlAxis = frame.right;
            MaximumJointOffset = 0f;
            for (int i = 0; i < joints.Length; i++)
            {
                float weight = i == 0 ? .42f : i == 1 ? .24f : i == 2 ? .15f : i == 3 ? .10f : i == 4 ? .06f : .03f;
                delayedSway[i] = Vector2.Lerp(delayedSway[i], sway, 1f - Mathf.Exp(-Mathf.Lerp(28f, 14f, i / 5f) * dt));
                var joint = joints[i];
                var parent = joint.parent;
                joint.localRotation = Quaternion.AngleAxis(delayedSway[i].x * weight, parent.InverseTransformDirection(frame.right))
                    * Quaternion.AngleAxis(delayedSway[i].y * weight, parent.InverseTransformDirection(frame.forward)) * restLocalRotations[i];
                if (i >= 2 && i < joints.Length - 1)
                {
                    Vector3 incoming = joint.position - joints[i - 1].position;
                    Vector3 outgoing = joints[i + 1].position - joint.position;
                    Vector3 axis = Vector3.Cross(outgoing, incoming);
                    float angle = Vector3.Angle(outgoing, incoming);
                    if (axis.sqrMagnitude > .00000001f && angle > .01f)
                    {
                        curlAxis = axis.normalized;
                        float amount = Mathf.Clamp(angle * curl * Mathf.Lerp(.5f, 1f, (i - 2) / 2f), -maximumCurlDegreesPerJoint, maximumCurlDegreesPerJoint);
                        joint.rotation = Quaternion.AngleAxis(amount, curlAxis) * joint.rotation;
                    }
                }
                else if (i == joints.Length - 1) joint.rotation = Quaternion.AngleAxis(Mathf.Clamp(curl * 25f, -2f, 2f), curlAxis) * joint.rotation;
                MaximumJointOffset = Mathf.Max(MaximumJointOffset, Quaternion.Angle(restLocalRotations[i], joint.localRotation));
            }
        }
    }
}
