using UnityEngine;

namespace Game.ThirdPerson
{
    public sealed partial class UntitledCharacterMotion
    {
        int lastTransitionSequence = -1;
        bool transitionLeftLead;

        // Crossfade from the actual previous output pose, not a freshly evaluated
        // walk pose, when the run input is released between animation frames.
        Quaternion[] lastBrakePose, brakeFromPose;
        Vector3 lastBrakeRootPosition, brakeFromRootPosition;
        Quaternion lastBrakeRootRotation, brakeFromRootRotation;
        Quaternion lastBrakeWaist, brakeFromWaist;
        bool brakePoseReady;
        float brakeBlendRemaining;
        int brakeSequence = -1;
        const float BrakeBlendDuration = .20f;

        void SmoothBrakingPose(float dt)
        {
            if (joints == null) return;
            if (lastBrakePose == null || lastBrakePose.Length != joints.Length) {
                lastBrakePose = new Quaternion[joints.Length];
                brakeFromPose = new Quaternion[joints.Length];
                brakePoseReady = false;
            }
            if (motor && brakeSequence != motor.TransitionSequence) {
                brakeSequence = motor.TransitionSequence;
                bool braking = motor.CurrentTransition == ThirdPersonMotor.Transition.SprintStop ||
                               motor.CurrentTransition == ThirdPersonMotor.Transition.WalkStop ||
                               motor.CurrentTransition == ThirdPersonMotor.Transition.AirStop;
                brakeBlendRemaining = braking && brakePoseReady ? BrakeBlendDuration : 0f;
                if (brakeBlendRemaining > 0f) {
                    System.Array.Copy(lastBrakePose, brakeFromPose, joints.Length);
                    brakeFromRootPosition = lastBrakeRootPosition;
                    brakeFromRootRotation = lastBrakeRootRotation;
                    brakeFromWaist = lastBrakeWaist;
                }
            }
            if (!motor || motor.IsJumping || motor.CurrentTransition == ThirdPersonMotor.Transition.None ||
                motor.CurrentTransition == ThirdPersonMotor.Transition.SprintStart)
                brakeBlendRemaining = 0f;
            if (brakeBlendRemaining > 0f) {
                float progress = 1f - brakeBlendRemaining / BrakeBlendDuration;
                float blend = Mathf.SmoothStep(0f, 1f, progress);
                transform.localPosition = Vector3.Lerp(brakeFromRootPosition, transform.localPosition, blend);
                transform.localRotation = Quaternion.Slerp(brakeFromRootRotation, transform.localRotation, blend);
                if (waist) waist.localRotation = Quaternion.Slerp(brakeFromWaist, waist.localRotation, blend);
                for (int i=0; i<joints.Length; i++)
                    if (joints[i].bone) joints[i].bone.localRotation = Quaternion.Slerp(brakeFromPose[i], joints[i].bone.localRotation, blend);
                brakeBlendRemaining = Mathf.Max(0f, brakeBlendRemaining - dt);
            }
            lastBrakeRootPosition = transform.localPosition;
            lastBrakeRootRotation = transform.localRotation;
            if (waist) lastBrakeWaist = waist.localRotation;
            for (int i=0; i<joints.Length; i++) if (joints[i].bone) lastBrakePose[i] = joints[i].bone.localRotation;
            brakePoseReady = true;
        }

        void ApplyMovementTransition()
        {
            if (!motor || !motor.Grounded || motor.IsJumping || motor.IsClimbing || motor.IsSwimming || motor.IsMantling || joints == null || joints.Length < 14) return;
            var state = motor.CurrentTransition;
            if (state == ThirdPersonMotor.Transition.None) return;
            if (lastTransitionSequence != motor.TransitionSequence)
            {
                lastTransitionSequence = motor.TransitionSequence;
                // Continue with the leg already moving forward.
                transitionLeftLead = Mathf.Sin(motionPhase) <= 0f;
            }

            float t = motor.TransitionProgress;
            float weight = JumpEase(0f, .12f, t) * (1f - JumpEase(.32f, 1f, t));
            if (weight <= .0001f) return;

            bool start = state == ThirdPersonMotor.Transition.SprintStart;
            bool walk = state == ThirdPersonMotor.Transition.WalkStop;
            bool air = state == ThirdPersonMotor.Transition.AirStop;
            // The reference's low launch is the dash, while an ordinary run starts
            // with a smaller push-off and remains appreciably more upright.
            float dashStart = start && motor.IsDashing
                ? Mathf.Lerp(.3f, 1f, motor.DashBlend) : 0f;
            float strength = walk ? .45f : air ? .55f : 1f;
            weight *= strength;
            float crouch = start ? Mathf.Lerp(.025f, .075f, dashStart) : walk ? .018f : air ? .065f : .065f;
            transform.localPosition = Vector3.Lerp(transform.localPosition, restPosition - Vector3.up * crouch, weight);
            transform.localRotation = Quaternion.Slerp(transform.localRotation, restRotation, weight);

            float lean = start ? Mathf.Lerp(20f, 42f, dashStart) : walk ? 2.5f : air ? 7f : Mathf.Lerp(9f, 2f, JumpEase(.20f, 1f, t));
            if (waist)
                waist.localRotation = Quaternion.Slerp(waist.localRotation,
                    waistRest * Quaternion.AngleAxis(lean, waistPitchAxis), weight);
            BlendTransitionPitch(0, start ? Mathf.Lerp(4f, 8f, dashStart) : walk ? 1f : 4f, weight);
            BlendTransitionPitch(1, start ? Mathf.Lerp(-22f, -41f, dashStart) : walk ? -2f : -5f, weight);

            float leadThigh = start ? Mathf.Lerp(-30f, -46f, dashStart) : walk ? -11f : air ? -20f : -32f;
            float leadKnee = start ? Mathf.Lerp(44f, 70f, dashStart) : walk ? 14f : air ? 34f : 28f;
            float rearThigh = start ? Mathf.Lerp(16f, 23f, dashStart) : walk ? 4f : air ? -8f : 15f;
            float rearKnee = start ? Mathf.Lerp(32f, 49f, dashStart) : walk ? 9f : air ? 34f : 45f;
            for (int hip = 6; hip <= 10; hip += 4)
            {
                BlendTransitionPitch(hip, transitionLeftLead ? leadThigh : rearThigh, weight);
                BlendTransitionPitch(hip + 1, transitionLeftLead ? rearThigh : leadThigh, weight);
                BlendTransitionPitch(hip + 2, transitionLeftLead ? leadKnee : rearKnee, weight);
                BlendTransitionPitch(hip + 3, transitionLeftLead ? rearKnee : leadKnee, weight);
            }
            float armWeight = weight * (start ? Mathf.Lerp(.55f, 1f, dashStart) : 1f);
            TransitionArm(2, 4, leftHand, transitionLeftLead, start, armWeight);
            TransitionArm(3, 5, rightHand, !transitionLeftLead, start, armWeight);
        }

        void BlendTransitionPitch(int index, float degrees, float weight)
        {
            var joint = joints[index];
            if (joint.bone)
                joint.bone.localRotation = Quaternion.Slerp(joint.bone.localRotation,
                    joint.rest * Quaternion.AngleAxis(degrees, joint.pitchAxis), weight);
        }

        void TransitionArm(int shoulderIndex, int elbowIndex, Transform hand, bool sameAsLead, bool starting, float weight)
        {
            var shoulder = joints[shoulderIndex];
            var elbow = joints[elbowIndex].bone;
            if (!shoulder.bone || !elbow || !hand) return;
            float upper = (starting ? (sameAsLead ? -43f : 18f) : (sameAsLead ? 18f : -18f)) * Mathf.Deg2Rad;
            float fore = (starting ? (sameAsLead ? -18f : 95f) : (sameAsLead ? 85f : 55f)) * Mathf.Deg2Rad;
            AimSegment(shoulder.bone, elbow,
                new Vector3(shoulder.side * .16f, -Mathf.Cos(upper), Mathf.Sin(upper)), weight);
            AimSegment(elbow, hand,
                new Vector3(shoulder.side * .08f, -Mathf.Cos(fore), Mathf.Sin(fore)), weight);
        }
    }
}
