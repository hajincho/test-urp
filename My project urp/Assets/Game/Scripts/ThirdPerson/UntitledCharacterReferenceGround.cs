using UnityEngine;

namespace Game.ThirdPerson
{
    public sealed partial class UntitledCharacterMotion
    {
        // Calibrated from the regular run, burst and stop sections of the supplied clip.
        // The pose is procedural; these are reference timings rather than captured 3D joints.
        public float referenceRunLean = 25f;
        public float referenceDashLean = 39f;
        float referenceLean, referenceLeanVelocity;
        Vector3 referencePreviousRoot;
        bool referenceGroundReady;
        static bool referenceCyclesReady;

        void ConfigureReferenceGroundCycle()
        {
            if (referenceCyclesReady) return;
            referenceCyclesReady = true;
            sprintThigh = MakeCycle(-18, 14, 34, 16, -28, -61, -58, -37);
            sprintKnee = MakeCycle(15, 23, 53, 88, 101, 90, 49, 21);
            sprintUpperArm = MakeCycle(-5, 11, 18, 0, -33, -61, -56, -27);
            sprintForearm = MakeCycle(68, 91, 101, 70, 8, -42, -37, 18);
        }

        void ResetReferenceGroundMotion()
        {
            referenceLean = referenceLeanVelocity = 0f;
            referenceGroundReady = false;
        }

        void ApplyReferenceGroundPose(float dt, float fastRun = 0f)
        {
            if (!motor || joints == null || joints.Length < 14) return;
            if (motor.IsClimbing || motor.IsSwimming || motor.IsMantling)
            { ResetReferenceGroundMotion(); return; }
            dt = Mathf.Clamp(dt, .0001f, .05f);
            var position = movementRoot ? movementRoot.position : transform.position;
            if (!referenceGroundReady || Vector3.Distance(position, referencePreviousRoot) > 2f)
            {
                referenceLean = referenceLeanVelocity = 0f;
                referenceGroundReady = true;
            }
            referencePreviousRoot = position;

            float ground = Mathf.Clamp01(1f - airBlend);
            float weight = movement * ground;
            float running = runBlend * weight;
            float start = motor.CurrentTransition == ThirdPersonMotor.Transition.SprintStart
                ? 1f - JumpEase(.15f, .9f, motor.TransitionProgress) : 0f;
            // Ordinary run starts stay near the regular running posture. The low,
            // forward burst belongs only to an explicitly requested dash.
            float burst = Mathf.Clamp01(fastRun);
            float slopeLean = Mathf.Lerp(14f, 7f, runBlend) * uphillBlend * weight;
            float target = Mathf.Lerp(referenceRunLean, referenceDashLean, burst) * running + slopeLean;
            referenceLean = Mathf.SmoothDamp(referenceLean, target, ref referenceLeanVelocity,
                burst > .05f ? .09f : .13f, Mathf.Infinity, dt);

            bool braking = motor.CurrentTransition == ThirdPersonMotor.Transition.SprintStop
                || motor.CurrentTransition == ThirdPersonMotor.Transition.WalkStop
                || motor.CurrentTransition == ThirdPersonMotor.Transition.AirStop;
            // The explicit step-and-compression poses own braking and landing. Keep their
            // continuation intact so the final pose can be cached by SmoothBrakingPose.
            if (motor.Grounded && !motor.IsJumping && !braking && !trackingJump)
            {
                float settle = 1f - JumpEase(0f, .2f, start);
                if (waist)
                {
                    var targetRotation = waistRest * Quaternion.AngleAxis(referenceLean, waistPitchAxis);
                    waist.localRotation = Quaternion.Slerp(waist.localRotation, targetRotation, settle);
                }
                // Lean at the waist; the entire character and planted shins remain upright.
                float phase = motionPhase;
                if (waist && joints[2].bone && joints[3].bone && motor.visualRoot)
                {
                    Vector3 shoulders = (joints[2].bone.position + joints[3].bone.position) * .5f;
                    Vector3 trunk = shoulders - waist.position;
                    Vector3 forward = motor.visualRoot.forward;
                    float actualPitch = Mathf.Atan2(Vector3.Dot(trunk, forward), trunk.y) * Mathf.Rad2Deg;
                    float runPitch = Mathf.Lerp(referenceRunLean + 8f, referenceDashLean + 3f, burst) + 1.6f * Mathf.Sin(2f * phase);
                    float correction = Mathf.Clamp(runPitch - actualPitch, -18f, 18f) * running * settle;
                    waist.rotation = Quaternion.AngleAxis(correction, motor.visualRoot.right) * waist.rotation;
                }
                float yaw = Mathf.Sin(phase + .18f) * Mathf.Lerp(.3f, 2.6f, runBlend) * weight;
                float roll = turnLean * Mathf.Lerp(.08f, .28f, runBlend)
                    + Mathf.Sin(phase - .12f) * Mathf.Lerp(.10f, .55f, runBlend) * weight;
                transform.localRotation = restRotation * Quaternion.Euler(0f, yaw, roll);
                var head = joints[1];
                if (head.bone)
                {
                    float extraLean = referenceLean - referenceRunLean * running - slopeLean;
                    head.bone.localRotation *= Quaternion.AngleAxis(-.82f * extraLean, head.pitchAxis);
                }
            }
        }
    }
}
