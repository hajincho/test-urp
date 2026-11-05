using UnityEngine;

namespace Game.ThirdPerson
{
    public sealed partial class UntitledCharacterMotion
    {
        bool trackingJump, leftLeads;
        float travelAtTakeoff, timeOnGround, jumpFollow;

        static float JumpEase(float start, float end, float value)
        {
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(start, end, value));
        }

        // Flat-ground reference: slight knee lift when stationary, split legs in travel,
        // open forearms on descent, then a short compression after actual ground contact.
        void ApplyReferenceJump(float dt)
        {
            if (!motor || joints == null || joints.Length < 14) return;
            if (motor.IsClimbing || motor.IsSwimming || motor.IsMantling)
            { trackingJump = false; jumpFollow = 0f; return; }
            float phase, compression = 0f, weight;
            if (motor.IsJumping)
            {
                if (!trackingJump || timeOnGround >= 0f)
                {
                    travelAtTakeoff = Mathf.Clamp01(currentSpeed / Mathf.Max(.1f, motor.walkSpeed));
                    leftLeads = travelAtTakeoff < .15f || Mathf.Sin(motionPhase) <= 0f;
                }
                trackingJump = true;
                timeOnGround = -1f;
                phase = motor.JumpProgress;
                weight = JumpEase(0f, .09f, phase);
            }
            else if (trackingJump && motor.Grounded)
            {
                timeOnGround = Mathf.Max(0f, timeOnGround) + dt;
                float recovery = Mathf.Lerp(.34f, .23f, travelAtTakeoff);
                compression = JumpEase(0f, .075f, timeOnGround);
                phase = 1f;
                weight = 1f - JumpEase(.095f, recovery, timeOnGround);
                trackingJump = timeOnGround < recovery;
            }
            else
            {
                trackingJump = false;
                jumpFollow = Mathf.MoveTowards(jumpFollow, 0f, dt * 5f);
                return;
            }

            float lift = JumpEase(.03f, .43f, phase) * (1f - JumpEase(.58f, .96f, phase));
            float opening = JumpEase(.22f, .86f, phase);
            float desiredFlare = (opening + .18f * compression) * weight;
            jumpFollow = Mathf.Lerp(jumpFollow, desiredFlare, 1f - Mathf.Exp(-14f * dt));

            float crouch = Mathf.Lerp(.13f, .10f, travelAtTakeoff) * compression;
            transform.localPosition = Vector3.Lerp(transform.localPosition, restPosition - Vector3.up * crouch, weight);
            transform.localRotation = Quaternion.Slerp(transform.localRotation, restRotation, weight);
            float lean = Mathf.Lerp(3f, 17f, travelAtTakeoff) + 14f * compression;
            if (waist)
                waist.localRotation = Quaternion.Slerp(waist.localRotation,
                    waistRest * Quaternion.AngleAxis(lean, waistPitchAxis), weight);
            BlendJumpPitch(0, 2f + 4f * compression, weight);
            BlendJumpPitch(1, -.65f * lean, weight);

            float leadThigh = -Mathf.Lerp(10f, 22f, travelAtTakeoff) - lift * Mathf.Lerp(24f, 42f, travelAtTakeoff);
            float leadKnee = Mathf.Lerp(16f, 29f, travelAtTakeoff) + lift * Mathf.Lerp(25f, 47f, travelAtTakeoff);
            float otherThigh = Mathf.Lerp(-4f, 10f, travelAtTakeoff) + lift * Mathf.Lerp(-4f, 8f, travelAtTakeoff);
            float otherKnee = 15f + lift * Mathf.Lerp(8f, 62f, travelAtTakeoff);
            leadThigh = Mathf.Lerp(leadThigh, -39f, compression);
            otherThigh = Mathf.Lerp(otherThigh, -31f, compression);
            leadKnee = Mathf.Lerp(leadKnee, 75f, compression);
            otherKnee = Mathf.Lerp(otherKnee, 67f, compression);
            for (int hip = 6; hip <= 10; hip += 4)
            {
                BlendJumpPitch(hip, leftLeads ? leadThigh : otherThigh, weight);
                BlendJumpPitch(hip + 1, leftLeads ? otherThigh : leadThigh, weight);
                BlendJumpPitch(hip + 2, leftLeads ? leadKnee : otherKnee, weight);
                BlendJumpPitch(hip + 3, leftLeads ? otherKnee : leadKnee, weight);
            }

            GuideJumpArm(2, 4, leftHand, leftLeads, lift, opening, compression, weight);
            GuideJumpArm(3, 5, rightHand, !leftLeads, lift, opening, compression, weight);
        }

        void BlendJumpPitch(int i, float degrees, float weight)
        {
            var j = joints[i];
            if (j.bone)
                j.bone.localRotation = Quaternion.Slerp(j.bone.localRotation,
                    j.rest * Quaternion.AngleAxis(degrees, j.pitchAxis), weight);
        }

        void GuideJumpArm(int shoulder, int elbow, Transform hand, bool sameSideAsRaisedKnee,
                          float lift, float opening, float compression, float weight)
        {
            var upper = joints[shoulder];
            var lower = joints[elbow].bone;
            if (!upper.bone || !lower || !hand) return;
            float travel = travelAtTakeoff;
            float swing = (sameSideAsRaisedKnee ? -25f : 24f) * travel;
            float upperPitch = Mathf.Lerp(3f + swing * (.45f + .55f * lift), 10f, compression) * Mathf.Deg2Rad;
            float foreLift = Mathf.Lerp(66f, 24f, travel) * opening;
            float forePitch = Mathf.Lerp(30f + foreLift + swing * (1f + lift), 18f, compression) * Mathf.Deg2Rad;
            float spread = (.18f + .25f * opening) * (1f - .62f * travel) * (1f - .65f * compression);
            AimSegment(upper.bone, lower,
                new Vector3(upper.side * spread, -Mathf.Cos(upperPitch), Mathf.Sin(upperPitch)), weight);
            AimSegment(lower, hand,
                new Vector3(upper.side * (spread + .55f * (1f - travel) * opening), -Mathf.Cos(forePitch), Mathf.Sin(forePitch)), weight);
        }
    }
}
