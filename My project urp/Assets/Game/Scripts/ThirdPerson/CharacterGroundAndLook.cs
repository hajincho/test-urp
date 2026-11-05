using UnityEngine;

namespace Game.ThirdPerson
{
    // Complements UntitledCharacterMotion after its base pose is evaluated.
    // No Animator avatar or humanoid mapping is required.
    [DefaultExecutionOrder(180)]
    public sealed class CharacterGroundAndLook : MonoBehaviour
    {
        public Transform playerRoot;
        public Transform visualRoot;
        public LayerMask groundLayers = ~0;
        [Range(0f, 1f)] public float footWeight = 1f;
        public float maxFootAdjustment = .3f;

        private ThirdPersonMotor motor;
        private UntitledCharacterMotion motion;
        private Transform head, pelvis;
        private Vector3 pelvisRest;
        private float stanceWeight, pelvisDrop;
        private bool supported;
        private Leg left, right, leftAux, rightAux;
        private float groundedBlend;
        private Quaternion stableHeadRotation;
        private bool hasStableHeadRotation;

        private void OnEnable() { hasStableHeadRotation = false; }
        private readonly RaycastHit[] hits = new RaycastHit[16];
        private readonly RaycastHit[] footprintHits = new RaycastHit[5];
        private readonly float[] footprintHeights = new float[5];
        private static readonly Vector2[] footprint = { Vector2.zero, Vector2.right, Vector2.left, Vector2.up, Vector2.down };

        private sealed class Leg
        {
            public Transform thigh, shin, foot;
            public float restFootHeight;
            public Vector3 restAnkle, supportTarget, supportNormal;
            public Quaternion restFootInVisual;
            public float targetHeight, weight, heightVelocity;
            public Quaternion restFootRotation;
            public Vector3 normal = Vector3.up;
            public bool planted;
            public Vector3 contactPosition;
        }

        private Transform FindBone(string name)
        {
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        private Leg MakeLeg(string upper, string lower, string footName)
        {
            var leg = new Leg { thigh = FindBone(upper), shin = FindBone(lower), foot = FindBone(footName) };
            if (!leg.thigh || !leg.shin || !leg.foot) return null;
            leg.restFootHeight = leg.foot.position.y - playerRoot.position.y;
            leg.restAnkle = visualRoot.InverseTransformPoint(leg.foot.position);
            leg.restFootInVisual = Quaternion.Inverse(visualRoot.rotation) * leg.foot.rotation;
            leg.restFootRotation = leg.foot.localRotation;
            leg.targetHeight = leg.foot.position.y;
            return leg;
        }

        private void Awake()
        {
            if (!playerRoot)
            {
                var found = GetComponentInParent<ThirdPersonMotor>();
                if (found) playerRoot = found.transform;
            }
            if (!playerRoot) { enabled = false; return; }
            motor = playerRoot.GetComponent<ThirdPersonMotor>();
            motion = GetComponent<UntitledCharacterMotion>();
            if (!visualRoot && motor) visualRoot = motor.visualRoot;
            if (!visualRoot) visualRoot = playerRoot;
            head = FindBone("Head");
            pelvis = FindBone("Bone_006");
            if (pelvis) pelvisRest = pelvis.localPosition;
            left = MakeLeg("Thigh_L", "Shin_L", "Foot_L");
            right = MakeLeg("Thigh_R", "Shin_R", "Foot_R");
            leftAux = MakeLeg("ThighAux_L", "ShinAux_L", "Bone_015");
            rightAux = MakeLeg("ThighAux_R", "ShinAux_R", "Bone_025");
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f || !playerRoot) return;
            if (motor && (motor.IsClimbing || motor.IsSwimming))
            {
                groundedBlend = stanceWeight = pelvisDrop = 0f;
                hasStableHeadRotation = false;
                if (pelvis) pelvis.localPosition = pelvisRest;
                return;
            }
            float k = 1f - Mathf.Exp(-12f * dt);
            groundedBlend = Mathf.Lerp(groundedBlend, !motor || motor.Grounded ? 1f : 0f, k);
            PrepareSlopeStance(dt);
            AdjustLeg(left, dt);
            AdjustLeg(right, dt);
            AdjustLeg(leftAux, dt);
            AdjustLeg(rightAux, dt);
            if (motion && motion.isActiveAndEnabled) motion.OpenCapeForStride();
            StabilizeHead(dt);
        }

        private void AdjustLeg(Leg leg, float dt)
        {
            if (leg == null || !leg.foot || !leg.thigh || !leg.shin) return;
            leg.foot.localRotation = leg.restFootRotation;
            if (motor && motor.IsJumping)
            {
                leg.planted = false;
                leg.weight = 0f;
                leg.heightVelocity = 0f;
                leg.targetHeight = leg.foot.position.y;
                return;
            }
            Vector3 foot = leg.foot.position;
            ProbeGround(foot, out RaycastHit best);

            float desiredWeight = 0f;
            float desiredHeight = foot.y;
            Vector3 normal = Vector3.up;
            if (best.collider)
            {
                float onGround = best.point.y + leg.restFootHeight;
                float delta = onGround - foot.y;
                // Let a swinging foot lift; correct only the planted part of the stride.
                float contact = 1f - Mathf.InverseLerp(.04f * playerRoot.lossyScale.y, .22f * playerRoot.lossyScale.y, Mathf.Max(0f, -delta));
                desiredWeight = footWeight * groundedBlend * contact;
                desiredHeight = foot.y + Mathf.Clamp(delta, -maxFootAdjustment, maxFootAdjustment);
                normal = best.normal;
            }

            // Opposite legs contact half a cycle apart. Hold the ankle in world space
            // through support, and release before the knee starts its recovery.
            float lockWeight = 0f;
            if (motion && motion.enabled && motion.GroundStrideWeight > .1f && motor && motor.Grounded && best.collider)
            {
                bool rightSide = leg == right || leg == rightAux;
                float halfContact = motion.ContactHalfCycle;
                float cycle = Mathf.Repeat(motion.GaitPhase / (2f * Mathf.PI) + (rightSide ? .5f : 0f) + halfContact, 1f);
                if (cycle < 2f * halfContact)
                {
                    if (!leg.planted) leg.contactPosition = foot;
                    leg.planted = true;
                    float edge = Mathf.Clamp01(Mathf.Min(cycle, 2f * halfContact - cycle) / .085f);
                    lockWeight = Mathf.SmoothStep(0f, 1f, edge) * motion.GroundStrideWeight * footWeight;
                    desiredHeight = Mathf.Lerp(desiredHeight, best.point.y + leg.restFootHeight, lockWeight);
                    desiredWeight = Mathf.Max(desiredWeight, lockWeight);
                }
                else leg.planted = false;
            }
            else leg.planted = false;

            if (leg.weight > .1f && Mathf.Abs(desiredHeight - leg.targetHeight) < .012f * Mathf.Abs(playerRoot.lossyScale.y))
                desiredHeight = leg.targetHeight;
            float blend = 1f - Mathf.Exp(-14f * dt);
            leg.weight = Mathf.Lerp(leg.weight, desiredWeight, blend);
            leg.targetHeight = Mathf.SmoothDamp(leg.targetHeight, desiredHeight, ref leg.heightVelocity, .065f, Mathf.Infinity, dt);
            leg.normal = Vector3.Slerp(leg.normal, normal, blend).normalized;
            float stance = supported ? stanceWeight : 0f;
            if (leg.weight < .005f && stance < .005f) return;

            Vector3 target = Vector3.Lerp(foot, leg.contactPosition, lockWeight);
            target.y = Mathf.Lerp(foot.y, leg.targetHeight, leg.weight);
            var supportLeg = leg == right || leg == rightAux ? right : left;
            if (stance > 0f) target = Vector3.Lerp(target, supportLeg.supportTarget, stance);
            SolveTwoBone(leg, target, Mathf.Max(Mathf.Max(lockWeight, leg.weight), stance), stance);

            // Rotate only modestly; the imported rig may already have a stylized foot angle.
            Quaternion align = Quaternion.FromToRotation(visualRoot.up, leg.normal);
            leg.foot.rotation = Quaternion.Slerp(leg.foot.rotation, align * leg.foot.rotation, .55f * leg.weight);
            if (stance > 0f)
            {
                // Align the planted sole from its neutral orientation, independent of the bent shin.
                var tilt = Quaternion.FromToRotation(Vector3.up, supportLeg.supportNormal);
                var planted = tilt * visualRoot.rotation * leg.restFootInVisual;
                leg.foot.rotation = Quaternion.Slerp(leg.foot.rotation, planted, stance);
            }
        }

        private bool ProbeGround(Vector3 foot, out RaycastHit best)
        {
            float scale = Mathf.Abs(playerRoot.lossyScale.y);
            int found = 0; best = default;
            foreach (var offset in footprint) {
                Vector3 origin = foot + Vector3.up * (.45f * scale)
                    + (visualRoot.right * offset.x + visualRoot.forward * offset.y) * (.065f * scale);
                int count = Physics.RaycastNonAlloc(origin, Vector3.down, hits, 1.2f * scale, groundLayers, QueryTriggerInteraction.Ignore);
                float closest = float.PositiveInfinity; RaycastHit candidate = default;
                for (int i = 0; i < count; i++) {
                    var hit = hits[i];
                    if (!hit.collider || hit.collider.transform.IsChildOf(playerRoot) || hit.normal.y < .45f || hit.distance >= closest) continue;
                    closest = hit.distance; candidate = hit;
                }
                if (!candidate.collider) continue;
                footprintHits[found] = candidate; footprintHeights[found] = candidate.point.y; found++;
            }
            if (found == 0) return false;
            System.Array.Sort(footprintHeights, 0, found);
            float median = footprintHeights[found / 2];
            float nearest = float.PositiveInfinity; Vector3 normal = Vector3.zero;
            for (int i = 0; i < found; i++) {
                float delta = Mathf.Abs(footprintHits[i].point.y - median);
                if (delta < nearest) { best = footprintHits[i]; nearest = delta; }
                if (delta < .045f * scale) normal += footprintHits[i].normal;
            }
            normal = normal.sqrMagnitude > .001f ? normal.normalized : Vector3.up;
            if (Vector3.Angle(normal, Vector3.up) < 3f) normal = Vector3.up;
            best.point = new Vector3(foot.x, median, foot.z); best.normal = normal;
            return true;
        }

        private bool SampleSupport(Leg leg, float side)
        {
            // Widen and slightly stagger the stance so uphill and downhill contacts have room.
            Vector3 rest = visualRoot.TransformPoint(leg.restAnkle);
            Vector3 spread = (visualRoot.right * (.075f * side) - visualRoot.forward * (.08f * side)) * playerRoot.lossyScale.y;
            Vector3 point = rest + spread * stanceWeight;
            if (!ProbeGround(point, out RaycastHit hit) || hit.normal.y < .68f) return false;
            float y = hit.point.y + leg.restFootHeight / hit.normal.y;
            if (Mathf.Abs(y - rest.y) > maxFootAdjustment) return false;
            leg.supportTarget = new Vector3(point.x, y, point.z);
            leg.supportNormal = hit.normal;
            return true;
        }

        private static float DropForReach(Leg leg)
        {
            // Leave the knee some flex even on the downhill side. Lower the hips only as
            // much as the more extended leg needs to reach its ankle.
            float reach = .975f * (Vector3.Distance(leg.thigh.position, leg.shin.position)
                                 + Vector3.Distance(leg.shin.position, leg.foot.position));
            Vector3 d = leg.thigh.position - leg.supportTarget;
            float verticalReach = Mathf.Sqrt(Mathf.Max(0f, reach * reach - d.x * d.x - d.z * d.z));
            return Mathf.Max(0f, d.y - verticalReach);
        }

        private void PrepareSlopeStance(float dt)
        {
            supported = false;
            if (!pelvis || !pelvis.parent || left == null || right == null || !motor) return;
            pelvis.localPosition = pelvisRest;
            float targetWeight = 0f;
            if (motor.Grounded && !motor.IsJumping && (motor.PlanarSpeed < .65f * playerRoot.lossyScale.y || stanceWeight > .005f)
                && SampleSupport(left, -1f) && SampleSupport(right, 1f))
            {
                supported = true;
                float angle = Vector3.Angle(Vector3.up, (left.supportNormal + right.supportNormal).normalized);
                targetWeight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(3f, 14f, angle))
                             * (1f - Mathf.InverseLerp(.06f * playerRoot.lossyScale.y, .65f * playerRoot.lossyScale.y, motor.PlanarSpeed)) * footWeight;
            }
            float blend = 1f - Mathf.Exp(-13f * dt);
            stanceWeight = Mathf.Lerp(stanceWeight, targetWeight, blend);
            float drop = supported ? Mathf.Clamp(Mathf.Max(DropForReach(left), DropForReach(right)), 0f, .22f * playerRoot.lossyScale.y) : 0f;
            pelvisDrop = motor.Grounded ? Mathf.Lerp(pelvisDrop, drop * stanceWeight, blend) : 0f;
            pelvis.localPosition += pelvis.parent.InverseTransformVector(Vector3.down * pelvisDrop);
        }

        private void OnDisable()
        {
            if (pelvis) pelvis.localPosition = pelvisRest;
            stanceWeight = pelvisDrop = 0f;
        }

        private void SolveTwoBone(Leg leg, Vector3 target, float weight, float stance)
        {
            Vector3 hip = leg.thigh.position;
            Vector3 knee = leg.shin.position;
            Vector3 ankle = leg.foot.position;
            float upper = Vector3.Distance(hip, knee);
            float lower = Vector3.Distance(knee, ankle);
            Vector3 toTarget = target - hip;
            float distance = toTarget.magnitude;
            if (upper < .001f || lower < .001f || distance < .001f) return;

            Vector3 direction = toTarget / distance;
            float clamped = Mathf.Clamp(distance, Mathf.Abs(upper - lower) + .001f, upper + lower - .001f);
            float along = (upper * upper - lower * lower + clamped * clamped) / (2f * clamped);
            float height = Mathf.Sqrt(Mathf.Max(0f, upper * upper - along * along));
            Vector3 bend = Vector3.ProjectOnPlane(knee - hip, direction);
            if (bend.sqrMagnitude < .00001f)
                bend = Vector3.ProjectOnPlane(visualRoot.forward, direction);
            if (bend.sqrMagnitude < .00001f) return;
            bend.Normalize();
            if (stance > 0f)
            {
                Vector3 forward = Vector3.ProjectOnPlane(visualRoot.forward, direction).normalized;
                if (forward.sqrMagnitude > .5f) bend = Vector3.Slerp(bend, forward, stance).normalized;
            }
            Vector3 desiredKnee = hip + direction * along + bend * height;

            var upperRotation = Quaternion.FromToRotation(knee - hip, desiredKnee - hip);
            leg.thigh.rotation = Quaternion.Slerp(leg.thigh.rotation, upperRotation * leg.thigh.rotation, weight);
            knee = leg.shin.position;
            ankle = leg.foot.position;
            var lowerRotation = Quaternion.FromToRotation(ankle - knee, target - knee);
            leg.shin.rotation = Quaternion.Slerp(leg.shin.rotation, lowerRotation * leg.shin.rotation, weight);
        }

        private void StabilizeHead(float dt)
        {
            if (!head || !visualRoot) return;
            // Smooth only the animated pose relative to the character.
            Quaternion desired = Quaternion.Inverse(visualRoot.rotation) * head.rotation;
            if (!hasStableHeadRotation)
            {
                stableHeadRotation = desired;
                hasStableHeadRotation = true;
            }
            float gait = motor ? Mathf.Clamp01(motor.PlanarSpeed / Mathf.Max(motor.walkSpeed, .1f)) : 0f;
            stableHeadRotation = Quaternion.Slerp(stableHeadRotation, desired, 1f - Mathf.Exp(-Mathf.Lerp(5f, 2.5f, gait) * dt));
            head.rotation = visualRoot.rotation * stableHeadRotation;
        }
    }
}
