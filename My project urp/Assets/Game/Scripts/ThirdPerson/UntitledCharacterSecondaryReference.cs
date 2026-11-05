using UnityEngine;

namespace Game.ThirdPerson
{
    public sealed partial class UntitledCharacterMotion
    {
        // A damped, bounded response to the body's motion. The reference's free ends
        // lag through starts/stops; the scalp and belt remain comparatively quiet.
        struct ReferenceSpring
        {
            public float value, velocity;
            public void Step(float target, float hz, float damping, float dt)
            {
                float omega = Mathf.Max(.1f, hz) * 2f * Mathf.PI;
                float error = value - target;
                if (damping >= .999f)
                {
                    float decay = Mathf.Exp(-omega * dt), c = velocity + omega * error;
                    value = target + (error + c * dt) * decay;
                    velocity = (velocity - omega * c * dt) * decay;
                    return;
                }
                damping = Mathf.Clamp(damping, .15f, .998f);
                float frequency = omega * Mathf.Sqrt(1f - damping * damping);
                float cosine = Mathf.Cos(frequency * dt), sine = Mathf.Sin(frequency * dt);
                float envelope = Mathf.Exp(-damping * omega * dt);
                float b = (velocity + damping * omega * error) / frequency;
                value = target + envelope * (error * cosine + b * sine);
                velocity = envelope * (velocity * cosine - (damping * omega * b + frequency * error) * sine);
            }
        }

        ReferenceSpring[] referenceHairPitch, referenceHairRoll, referenceClothPitch, referenceClothRoll;
        ReferenceSpring[] referenceTipPitch, referenceTipRoll, referenceHangingPitch, referenceHangingRoll;
        Vector3 referencePreviousPosition, referenceVelocity, referenceAcceleration;
        float referencePreviousYaw, referenceYawRate, referenceRun, referenceSwim, referenceClimb;
        ReferenceSpring referenceWaterFloat;
        bool referenceSecondaryReady;

        void CacheSecondaryReference()
        {
            referenceHairPitch = new ReferenceSpring[hairBones == null ? 0 : hairBones.Length];
            referenceHairRoll = new ReferenceSpring[referenceHairPitch.Length];
            referenceClothPitch = new ReferenceSpring[clothBones == null ? 0 : clothBones.Length];
            referenceClothRoll = new ReferenceSpring[referenceClothPitch.Length];
            referenceTipPitch = new ReferenceSpring[hairTips == null ? 0 : hairTips.Length];
            referenceTipRoll = new ReferenceSpring[referenceTipPitch.Length];
            referenceHangingPitch = new ReferenceSpring[hanging == null ? 0 : hanging.Length];
            referenceHangingRoll = new ReferenceSpring[referenceHangingPitch.Length];
            ResetSecondaryReference();
        }

        void ResetSecondaryReference()
        {
            referenceSecondaryReady = false;
            referenceVelocity = referenceAcceleration = Vector3.zero;
            referenceRun = referenceSwim = referenceClimb = referenceYawRate = 0f;
            referenceWaterFloat = default;
            if (referenceHairPitch != null) System.Array.Clear(referenceHairPitch, 0, referenceHairPitch.Length);
            if (referenceHairRoll != null) System.Array.Clear(referenceHairRoll, 0, referenceHairRoll.Length);
            if (referenceClothPitch != null) System.Array.Clear(referenceClothPitch, 0, referenceClothPitch.Length);
            if (referenceClothRoll != null) System.Array.Clear(referenceClothRoll, 0, referenceClothRoll.Length);
            if (referenceTipPitch != null) System.Array.Clear(referenceTipPitch, 0, referenceTipPitch.Length);
            if (referenceTipRoll != null) System.Array.Clear(referenceTipRoll, 0, referenceTipRoll.Length);
            if (referenceHangingPitch != null) System.Array.Clear(referenceHangingPitch, 0, referenceHangingPitch.Length);
            if (referenceHangingRoll != null) System.Array.Clear(referenceHangingRoll, 0, referenceHangingRoll.Length);
        }

        void ApplySecondaryReference(float deltaTime)
        {
            if (deltaTime <= 0f || !movementRoot) return;
            if (referenceHairPitch == null || referenceHairPitch.Length != (hairBones == null ? 0 : hairBones.Length)) CacheSecondaryReference();
            float dt = Mathf.Min(deltaTime, .05f);
            float scale = Mathf.Max(.01f, Mathf.Abs(movementRoot.lossyScale.y));
            Vector3 position = movementRoot.position;
            float yaw = motor && motor.visualRoot ? motor.visualRoot.eulerAngles.y : transform.eulerAngles.y;
            bool teleported = referenceSecondaryReady && Vector3.Distance(position, referencePreviousPosition) > 1.5f * scale;
            if (!referenceSecondaryReady || teleported || deltaTime > .25f)
            {
                ResetSecondaryReference(); referencePreviousPosition = position; referencePreviousYaw = yaw;
                referenceSecondaryReady = true;
            }
            var velocity = Vector3.ClampMagnitude((position - referencePreviousPosition) / Mathf.Max(.0001f, deltaTime), 15f * scale);
            var acceleration = Vector3.ClampMagnitude((velocity - referenceVelocity) / Mathf.Max(.0001f, deltaTime), 35f * scale);
            referenceAcceleration = Vector3.Lerp(referenceAcceleration, acceleration, 1f - Mathf.Exp(-9f * dt));
            referenceVelocity = Vector3.Lerp(referenceVelocity, velocity, 1f - Mathf.Exp(-14f * dt));
            referencePreviousPosition = position;
            float yawRate = Mathf.Clamp(Mathf.DeltaAngle(referencePreviousYaw, yaw) / Mathf.Max(.0001f, deltaTime), -240f, 240f);
            referencePreviousYaw = yaw;
            referenceYawRate = Mathf.Lerp(referenceYawRate, yawRate, 1f - Mathf.Exp(-8f * dt));
            bool swimming = motor && motor.IsSwimming, climbing = motor && motor.IsClimbing;
            referenceSwim = Mathf.Lerp(referenceSwim, swimming ? 1f : 0f, 1f - Mathf.Exp(-3.5f * dt));
            referenceClimb = Mathf.Lerp(referenceClimb, climbing ? 1f : 0f, 1f - Mathf.Exp(-5f * dt));
            // Free ends keep floating when the swimmer stops and the torso becomes
            // upright. Release more slowly than the body pose when leaving the water.
            referenceWaterFloat.Step(swimming ? 1f : 0f, swimming ? 1.1f : .55f, .96f, dt);
            float desiredRun = motor ? Mathf.InverseLerp(motor.walkSpeed, motor.sprintSpeed, motor.PlanarSpeed) : runBlend;
            if (swimming || climbing) desiredRun = 0f;
            referenceRun = Mathf.Lerp(referenceRun, desiredRun, 1f - Mathf.Exp(-(desiredRun < referenceRun ? 3.3f : 4.8f) * dt));
            float walking = motor ? Mathf.Clamp01(motor.PlanarSpeed / Mathf.Max(.1f, motor.walkSpeed)) * (1f - referenceRun) : movement * (1f - referenceRun);
            Vector3 localAcceleration = transform.InverseTransformDirection(referenceAcceleration) / scale;
            float drag = Mathf.Clamp(localAcceleration.z * .52f, -7f, 8f);
            float lateral = Mathf.Clamp(-localAcceleration.x * .24f - referenceYawRate * .016f, -5f, 5f);
            float vertical = Mathf.Clamp(-referenceAcceleration.y / scale * .11f, -3f, 3f);
            float airborne = motor && !motor.Grounded && !swimming && !climbing ? 1f : 0f;

            for (int i = 0; i < referenceHairPitch.Length; i++)
            {
                if (!hairBones[i]) continue;
                int chain = i / 3, segment = i % 3;
                float free = segment / 2f, phase = chain * .79f + segment * .57f;
                float breeze = Mathf.Sin(clock * 1.13f - phase) + .24f * Mathf.Sin(clock * 1.91f - phase * 1.37f);
                float gait = Mathf.Sin(2f * motionPhase - phase) * (.10f * walking + .70f * referenceRun) * (1f - airborne);
                float rootTrail = chain < 3 ? (segment == 0 ? 18f : 4.2f) : 2.2f;
                float targetPitch = referenceRun * rootTrail + .50f * walking + breeze * Mathf.Lerp(.12f, .46f, free)
                    + drag * Mathf.Lerp(.23f, .44f, free) + vertical * free + gait;
                float climbTrail = chain < 3 ? (segment == 0 ? 4.5f : 2.2f) : 1.8f;
                targetPitch = Mathf.Lerp(targetPitch, climbTrail + .55f * breeze + drag * .16f + vertical * free * .32f, referenceClimb);
                targetPitch = Mathf.Lerp(targetPitch, (segment == 0 ? 8f : 4f) + 1.2f * Mathf.Sin(clock * .93f - phase) + drag * .13f, referenceSwim);
                float targetRoll = lateral * Mathf.Lerp(.24f, .60f, free) + breeze * Mathf.Lerp(.10f, .48f, free);
                targetRoll += referenceSwim * Mathf.Sin(clock * 1.04f - phase) * (1f + free);
                referenceHairPitch[i].Step(Mathf.Clamp(targetPitch, segment == 0 ? -1f : -8f, segment == 0 ? 26f : 14f), Mathf.Lerp(5.0f, 2.5f, free) * Mathf.Lerp(1f, .7f, referenceSwim), .84f, dt);
                referenceHairRoll[i].Step(Mathf.Clamp(targetRoll, -6f, 6f), Mathf.Lerp(4.6f, 2.3f, free), .88f, dt);
                hairBones[i].localRotation = hairRest[i] * Quaternion.AngleAxis(referenceHairPitch[i].value, hairAxes[i]) * Quaternion.AngleAxis(referenceHairRoll[i].value, hairRollAxes[i]);
            }
            for (int i = 0; i < referenceClothPitch.Length; i++)
            {
                if (!clothBones[i]) continue;
                bool belt = i % 2 == 0;float rear = panels == null ? 1f : 1f - panels[i / 2].front;
                float softness = belt ? .25f : 1f, phase = i / 2f * .62f + (belt ? 0f : .60f);
                float breeze = Mathf.Sin(clock * .83f - phase) + .21f * Mathf.Sin(clock * 1.39f - phase * 1.23f);
                float trail = (belt ? Mathf.Lerp(10f, 8.5f, rear) : 3.7f) * referenceRun;
                float stepSway = Mathf.Sin(2f * motionPhase - .55f - (belt ? 0f : .5f) - rear * .32f) * (walking * 1.7f + referenceRun * 3.8f);
                float groundGait = motor && motor.Grounded ? stepSway * softness : 0f;
                float pitch = trail + groundGait + (breeze * (.20f + referenceRun * .48f) + drag * .38f + vertical * .24f) * softness;
                pitch += jumpFollow * (belt ? 7f : 2f) * (1f - referenceSwim);
                pitch = Mathf.Lerp(pitch, (belt ? Mathf.Lerp(1.8f, 4f, rear) : 2.4f) + .30f * breeze + vertical * softness * .32f, referenceClimb);
                pitch = Mathf.Lerp(pitch, (belt ? 3f : 5.5f) + softness * 1.3f * Mathf.Sin(clock * .96f - phase), referenceSwim);
                float roll = (lateral * .30f + breeze * .22f) * softness * Mathf.Lerp(1f, .50f, rear);
                referenceClothPitch[i].Step(pitch, belt ? 4.5f : 2.2f, belt ? 1f : .85f, dt);
                referenceClothRoll[i].Step(roll, belt ? 4f : 2f, .9f, dt);
                clothBones[i].localRotation = clothRest[i] * Quaternion.AngleAxis(referenceClothPitch[i].value, clothAxes[i]);
                if (capeFlareAxes[i].sqrMagnitude > .1f) clothBones[i].localRotation *= Quaternion.AngleAxis(referenceClothRoll[i].value, capeFlareAxes[i]);
            }
            for (int i = 0; i < referenceTipPitch.Length; i++)
            {
                if (!hairTips[i]) continue;
                float phase = i * .83f;
                float pitch = .45f * Mathf.Sin(clock * 1.09f - phase) + referenceRun * (1.2f + .65f * Mathf.Sin(motionPhase - phase)) + drag * .40f;
                pitch = Mathf.Lerp(pitch, 2f + 1.1f * Mathf.Sin(clock * .90f - phase), referenceSwim);
                referenceTipPitch[i].Step(Mathf.Clamp(pitch, -5f, 7f), 2.1f, .78f, dt);
                hairTips[i].localRotation = hairTipRest[i] * Quaternion.AngleAxis(referenceTipPitch[i].value, hairTipAxes[i]);
            }
            for (int i = 0; i < referenceHangingPitch.Length; i++)
            {
                var joint = hanging[i];if (!joint.bone) continue;
                float free = joint.hem ? .60f : 1f, phase = i * .73f + joint.phaseLag * 9f;
                float idle = Mathf.Sin(clock * 1.24f - phase) + .22f * Mathf.Sin(clock * 1.87f - phase * 1.31f);
                float heavy = joint.hem && joint.panel >= 0 ? Mathf.Lerp(1f, .35f, 1f - panels[joint.panel].front) : 1f;
                float pitch = joint.amplitude * heavy * (idle * .28f + drag * free * .75f + vertical * .40f + referenceRun * 1.1f * Mathf.Sin(motionPhase - phase));
                float roll = joint.amplitude * heavy * (lateral * .65f + idle * .35f);
                pitch = Mathf.Lerp(pitch, joint.amplitude * Mathf.Sin(clock * .86f - phase) * 1.5f, referenceSwim);
                referenceHangingPitch[i].Step(Mathf.Clamp(pitch, -10f, 10f), joint.hem ? 2.6f : 3.2f, .77f, dt);
                referenceHangingRoll[i].Step(Mathf.Clamp(roll, -8f, 8f), 2.5f, .82f, dt);
                joint.bone.localRotation = joint.rest * Quaternion.AngleAxis(referenceHangingPitch[i].value, joint.pitch) * Quaternion.AngleAxis(referenceHangingRoll[i].value, joint.roll);
            }
            ApplyReferenceWaterDrape();
            KeepReferenceHairBehindTorso();
        }

        void ApplyReferenceWaterDrape()
        {
            float afloat = Mathf.Clamp01(referenceWaterFloat.value);
            if (afloat < .0001f) return;
            // Aim only flexible lower segments in a world-space trailing direction.
            // Scalp, belt and accessories remain attached to the posed body. Applying
            // this after local springs prevents an upright tread pose dropping all
            // long pieces vertically in a single transition.
            Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < .5f) forward = Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            for (int i = 0; i < referenceHairPitch.Length; i++)
            {
                int segment = i % 3, chain = i / 3;
                var bone = hairBones[i];
                if (!bone || segment == 0 || bone.childCount == 0) continue;
                float side = Mathf.Clamp(transform.InverseTransformPoint(bone.position).x * 3f, -.7f, .7f);
                float wave = Mathf.Sin(clock * .83f - chain * .91f - segment * .30f);
                Vector3 direction = -forward + right * (side * .30f + wave * .035f) - Vector3.up * (.18f + .055f * wave);
                AimReferenceFreeEnd(bone, direction, (segment == 1 ? .68f : .96f) * afloat, chain < 3 ? 35f : 22f);
            }
            for (int i = 1; i < referenceClothPitch.Length; i += 2)
            {
                var bone = clothBones[i];
                if (!bone || bone.childCount == 0) continue;
                float rear = panels == null ? 1f : 1f - panels[i / 2].front;
                Vector3 outward = panels == null ? -forward : transform.TransformDirection(panels[i / 2].outward);
                outward = Vector3.ProjectOnPlane(outward, Vector3.up).normalized;
                Vector3 direction = Vector3.Lerp(outward, -forward, rear * .45f)
                    - Vector3.up * (.26f + .045f * Mathf.Sin(clock * .76f - i * .37f));
                AimReferenceFreeEnd(bone, direction, Mathf.Lerp(.45f, .86f, rear) * afloat, 48f);
            }
        }

        static void AimReferenceFreeEnd(Transform bone, Vector3 desiredDirection, float weight, float maxAngle)
        {
            Vector3 current = bone.GetChild(0).position - bone.position;
            if (current.sqrMagnitude < .000001f || desiredDirection.sqrMagnitude < .000001f) return;
            Quaternion aim = Quaternion.FromToRotation(current, desiredDirection);
            aim = Quaternion.RotateTowards(Quaternion.identity, aim, maxAngle);
            bone.rotation = Quaternion.Slerp(Quaternion.identity, aim, Mathf.Clamp01(weight)) * bone.rotation;
        }

        void KeepReferenceHairBehindTorso()
        {
            if (joints == null || joints.Length < 4 || !waist || !joints[0].bone || !joints[2].bone || !joints[3].bone) return;
            Vector3 bottom = waist.position, top = joints[0].bone.position;
            float radius = Mathf.Max(.04f, Vector3.Distance(joints[2].bone.position, joints[3].bone.position) * .34f);
            Vector3 axis = top - bottom;float lengthSquared = axis.sqrMagnitude;if (lengthSquared < .0001f) return;
            // Only the long back locks, never bangs or the face ornaments.
            for (int i = 0; i < Mathf.Min(9, hairBones.Length); i++)
            {
                var bone = hairBones[i];if (!bone || bone.childCount == 0) continue;
                var next = bone.GetChild(0);Vector3 current = next.position;
                float t = Mathf.Clamp01(Vector3.Dot(current - bottom, axis) / lengthSquared);
                var center = bottom + axis * t;var offset = current - center;
                if (offset.sqrMagnitude >= radius * radius || t <= .01f || t >= .99f) continue;
                Vector3 outward = Vector3.ProjectOnPlane(offset, axis);
                if (outward.sqrMagnitude < .000001f) outward = -transform.forward;
                if (Vector3.Dot(outward, transform.forward) > 0f) outward = Vector3.ProjectOnPlane(-transform.forward, axis);
                var goal = center + outward.normalized * (radius + .003f);
                if ((goal - bone.position).sqrMagnitude > .000001f) bone.rotation = Quaternion.FromToRotation(current - bone.position, goal - bone.position) * bone.rotation;
            }
        }
    }
}
