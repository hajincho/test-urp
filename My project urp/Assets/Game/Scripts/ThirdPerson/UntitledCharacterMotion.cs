using System;
using UnityEngine;

namespace Game.ThirdPerson
{
    [DefaultExecutionOrder(100)]
    public sealed partial class UntitledCharacterMotion : MonoBehaviour
    {
        [Serializable] public class Joint
        {
            public Transform bone;
            public Quaternion rest;
            public Vector3 pitchAxis;
            public Vector3 rollAxis;
            public float side;
        }
        public Transform movementRoot;
        public ThirdPersonMotor motor;
        public Joint[] joints;
        public float walkingAmplitude = 32f;
        public float breathingAmplitude = .008f;
        [SerializeField] float currentSpeed;
        [SerializeField] float motionPhase;
        sealed class FingerJoint
        {
            public Transform bone;
            public Quaternion rest;
            public Vector3 curlAxis;
            public float relaxedCurl, sprintCurl;
        }
        FingerJoint[] fingers;
        Transform[] clothBones;
        Quaternion[] clothRest;
        Vector3[] clothAxes, capeFlareAxes;
        float[] capeTrailAngles;
        float windBlend;
        float capeFollow, capeFollowVelocity;
        Transform[] hairBones;
        Quaternion[] hairRest;
        Transform[] hairTips;
        Quaternion[] hairTipRest;
        Vector3[] hairTipAxes;
        Vector3[] hairAxes, hairRollAxes;
        Transform leftHand, rightHand, waist;
        Quaternion waistRest;
        Vector3 waistPitchAxis;
        // One full stride, sampled from support through toe-off, knee recovery and extension.
        // Positive thigh angles move the leg back; arm angles are measured forward from vertical.
        static AnimationCurve sprintThigh, sprintKnee, sprintUpperArm, sprintForearm;

        static void CacheSprintCycle()
        {
            if (sprintThigh != null) return;
            sprintThigh = MakeCycle(-14, 19, 41, 18, -31, -68, -66, -44);
            sprintKnee = MakeCycle(24, 33, 67, 104, 123, 107, 68, 35);
            sprintUpperArm = MakeCycle(-3, 12, 16, -1, -36, -67, -60, -32);
            sprintForearm = MakeCycle(72, 96, 110, 74, 8, -55, -42, 19);
        }

        static AnimationCurve MakeCycle(params float[] angles)
        {
            int count = angles.Length;
            var keys = new Keyframe[count + 1];
            for (int i = 0; i <= count; i++)
            {
                float tangent = .5f * count * (angles[(i + 1) % count] - angles[(i + count - 1) % count]);
                keys[i] = new Keyframe((float)i / count, angles[i % count], tangent, tangent);
            }
            return new AnimationCurve(keys);
        }

        public float GaitPhase => motionPhase;
        public float GroundStrideWeight => movement * (1f - airBlend);
        public float ContactHalfCycle => Mathf.Lerp(.25f, .16f, runBlend);
        Vector3 restPosition;
        Quaternion restRotation;
        Vector3 previousPosition;
        float movement;
        float clock;

        public void Configure(Transform root, ThirdPersonMotor movementMotor)
        {
            movementRoot = root;
            motor = movementMotor;
            restPosition = transform.localPosition;
            restRotation = transform.localRotation;
            string[] names = { "Chest", "Head", "UpperArm_L", "UpperArm_R", "Forearm_L", "Forearm_R",
                "Thigh_L", "Thigh_R", "Shin_L", "Shin_R", "ThighAux_L", "ThighAux_R", "ShinAux_L", "ShinAux_R" };
            var all = GetComponentsInChildren<Transform>(true);
            joints = new Joint[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                var bone = Array.Find(all, t => t.name == names[i]);
                joints[i] = new Joint { bone = bone };
                if (!bone) continue;
                joints[i].rest = bone.localRotation;
                joints[i].side = Mathf.Sign(transform.InverseTransformPoint(bone.position).x);
                joints[i].pitchAxis = bone.InverseTransformDirection(transform.right).normalized;
                joints[i].rollAxis = bone.InverseTransformDirection(transform.forward).normalized;
            }
        }
        void OnEnable()
        {
            restPosition = transform.localPosition;
            restRotation = transform.localRotation;
            previousPosition = movementRoot ? movementRoot.position : transform.position;
            lastYaw = transform.parent ? transform.parent.eulerAngles.y : transform.eulerAngles.y;
            turnLean = 0f;
            CacheSprintCycle();
            CacheUpperBody();
            groundPose = GetComponent<CharacterGroundAndLook>();
            CacheCloth();
            CacheWalkDetails();
            CacheBlink();
        }
        void CacheUpperBody()
        {
            int[] starts = { 246, 250, 254, 258, 262, 284, 288, 292, 296, 300 };
            var all = GetComponentsInChildren<Transform>(true);
            waist = Array.Find(all, t => t.name == "Bone_163");
            if (waist)
            {
                waistRest = waist.localRotation;
                waistPitchAxis = waist.InverseTransformDirection(transform.right).normalized;
            }
            leftHand = Array.Find(all, t => t.name == "Bone_244");
            rightHand = Array.Find(all, t => t.name == "Bone_282");
            var found = new System.Collections.Generic.List<FingerJoint>();
            for (int chain = 0; chain < starts.Length; chain++)
            {
                int digit = chain % 5;
                int indexBase = starts[chain - digit + 1];
                var index = Array.Find(all, t => t.name == "Bone_" + indexBase);
                var little = Array.Find(all, t => t.name == "Bone_" + (indexBase + 12));
                var hand = chain < 5 ? leftHand : rightHand;
                if (!index || !little || !hand) continue;
                Vector3 across = (little.position - index.position).normalized;
                Vector3 palm = Vector3.Cross(index.position - hand.position, across).normalized;
                if (Vector3.Dot(palm, -transform.up) < 0f) palm = -palm;
                Vector3 inward = digit == 0 ? (across + palm * .45f).normalized : palm;
                for (int segment = 0; segment < 3; segment++)
                {
                    string boneName = "Bone_" + (starts[chain] + segment);
                    Transform bone = Array.Find(all, t => t.name == boneName);
                    if (!bone || bone.childCount == 0) continue;
                    Vector3 direction = (bone.GetChild(0).position - bone.position).normalized;
                    Vector3 axis = Vector3.Cross(direction, inward);
                    if (axis.sqrMagnitude < .0001f) axis = transform.forward;
                    found.Add(new FingerJoint {
                        bone = bone, rest = bone.localRotation,
                        curlAxis = bone.InverseTransformDirection(axis.normalized),
                        relaxedCurl = digit == 0 ? 10f : (segment == 2 ? 9f : 15f) + 3f * digit,
                        sprintCurl = digit == 0 ? 14f : (segment == 1 ? 25f : 16f) + 3f * digit
                    });
                }
            }
            fingers = found.ToArray();
        }
        void CacheCloth()
        {
            // Waist-attached skirt chains in the imported character rig.
            int[] ids = { 35, 36, 42, 43, 53, 54, 64, 65, 75, 76, 86, 87,
                          97, 98, 108, 109, 119, 120, 130, 131, 141, 142, 152, 153 };
            var all = GetComponentsInChildren<Transform>(true);
            clothBones = new Transform[ids.Length];
            clothRest = new Quaternion[ids.Length];
            clothAxes = new Vector3[ids.Length];
            capeFlareAxes = new Vector3[ids.Length];
            capeTrailAngles = new float[ids.Length];
            panels = new CapePanel[ids.Length / 2];
            leftToe = Array.Find(all, t => t.name == "Bone_012");
            rightToe = Array.Find(all, t => t.name == "Bone_022");
            for (int i = 0; i < ids.Length; i++)
            {
                Transform bone = Array.Find(all, t => t.name == "Bone_" + ids[i].ToString("D3"));
                clothBones[i] = bone;
                if (!bone) continue;
                clothRest[i] = bone.localRotation;
                // Let the rear panels tilt from the belt as a unit. Keep the front panels clear of the knees.
                float rootZ = transform.InverseTransformPoint((i % 2 == 0 ? bone : bone.parent).position).z;
                float rear = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-.02f, .11f, rootZ));
                // Each belt panel opens around its own hinge as the character decelerates on landing.
                Vector3 radial = bone.position - bone.parent.position;
                Vector3 axis = Vector3.Cross(-transform.up, Vector3.ProjectOnPlane(radial, transform.up));
                capeFlareAxes[i] = bone.InverseTransformDirection(axis.normalized);
                if (i % 2 == 0)
                {
                    panels[i / 2].restPosition = bone.localPosition;
                    panels[i / 2].outward = Vector3.ProjectOnPlane(transform.InverseTransformDirection(radial), Vector3.up).normalized;
                    panels[i / 2].front = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.05f, .65f, panels[i / 2].outward.z));
                }
                capeTrailAngles[i] = rear * (i % 2 == 0 ? 27f : 2f);
                clothAxes[i] = bone.InverseTransformDirection(transform.right).normalized;
            }
            BindCapeWaist();
            // Long back hair chains; facial and head bones remain untouched.
            int[] hairIds = { 175, 176, 177, 181, 182, 183, 187, 188, 189,
                              217, 218, 219, 226, 227, 228 };
            hairBones = new Transform[hairIds.Length];
            hairRest = new Quaternion[hairIds.Length];
            hairAxes = new Vector3[hairIds.Length];
            hairRollAxes = new Vector3[hairIds.Length];
            for (int i = 0; i < hairIds.Length; i++)
            {
                var bone = Array.Find(all, t => t.name == "Bone_" + hairIds[i].ToString("D3"));
                hairBones[i] = bone;
                if (!bone) continue;
                hairRest[i] = bone.localRotation;
                hairAxes[i] = bone.InverseTransformDirection(transform.right).normalized;
                hairRollAxes[i] = bone.InverseTransformDirection(transform.forward).normalized;
            }
            windBlend = 0f;
            int[] tipIds = { 178, 184, 190, 220, 229 };
            hairTips = new Transform[tipIds.Length];
            hairTipRest = new Quaternion[tipIds.Length];
            hairTipAxes = new Vector3[tipIds.Length];
            for (int i=0; i<tipIds.Length; i++) {
                var tip = Array.Find(all, t => t.name == "Bone_" + tipIds[i].ToString("D3"));
                hairTips[i] = tip;
                if (!tip) continue;
                hairTipRest[i] = tip.localRotation;
                hairTipAxes[i] = tip.InverseTransformDirection(transform.right).normalized;
            }
            capeFollow = capeFollowVelocity = 0f;
        }
        void ApplyClothWind(float dt)
        {
            if (clothBones == null) return;
            float speed = motor ? motor.PlanarSpeed : 0f;
            float sprint = motor ? Mathf.InverseLerp(motor.walkSpeed, motor.sprintSpeed, speed) : 0f;
            windBlend = Mathf.Lerp(windBlend, sprint, 1f - Mathf.Exp(-3f * dt));
            capeFollow = Mathf.SmoothDamp(capeFollow, sprint, ref capeFollowVelocity, .32f,
                                          Mathf.Infinity, dt);
            for (int i = 0; i < clothBones.Length; i++)
            {
                if (!clothBones[i]) continue;
                if (i % 2 == 0) clothBones[i].localPosition = panels[i / 2].restPosition;
                float chain = i / 2;
                float wave = Mathf.Sin(clock * (1.3f + .5f * windBlend) + chain * .55f - (i % 2) * .55f);
                float gust = Mathf.Sin(clock * .43f + chain * .3f);
                float downwardLook = motor && motor.cameraTransform ? Mathf.Clamp01(-motor.cameraTransform.forward.y) : 0f;
                // Keep the back panels weighted, following speed with a delayed response.
                float back = 1f - panels[i / 2].front;
                float lift = Mathf.Lerp(4f + capeTrailAngles[i], 3f + .28f * capeTrailAngles[i], back) * capeFollow
                           + 1.2f * downwardLook * (1f - .75f * back);
                float angle = (wave * (.18f + .35f * windBlend) + gust * .12f) * (1f - .75f * back) + lift;
                angle *= 1f - panels[i / 2].front * runBlend * (1f - airBlend);
                clothBones[i].localRotation = clothRest[i] * Quaternion.AngleAxis(angle, clothAxes[i]);
                if (i % 2 == 0 && jumpFollow > 0f && capeFlareAxes[i].sqrMagnitude > .5f)
                    clothBones[i].localRotation *= Quaternion.AngleAxis(19f * jumpFollow, capeFlareAxes[i]);
            }
            if (hairBones == null) return;
            for (int i = 0; i < hairBones.Length; i++)
            {
                if (!hairBones[i]) continue;
                float chain = i / 3;
                float segment = i % 3;
                float wave = Mathf.Sin(clock * (1.25f + .8f * windBlend) + chain * .8f - segment * .65f);
                float gust = Mathf.Sin(clock * .55f + chain * .4f);
                float angle = wave * (.9f + 3.2f * windBlend) + gust * .7f + 2.5f * windBlend;
                float lag = segment * .55f + chain * .12f;
                float bounce = Mathf.Sin(2f * motionPhase - lag) * movement * (1f - airBlend);
                float trailAngle = chain < 3 ? (segment == 0 ? 23f : 6f) : 3f;
                float trailing = angle + jumpFollow * (segment == 0 ? 12f : 4f) + windBlend * trailAngle + bounce * (.55f + segment * .65f);
                float lateral = (Mathf.Sin(motionPhase - lag) * movement * 2.2f + turnLean * .55f)
                              * (.5f + segment * .4f);
                hairBones[i].localRotation = hairRest[i] * Quaternion.AngleAxis(trailing, hairAxes[i])
                                           * Quaternion.AngleAxis(lateral, hairRollAxes[i]);
            }
        }
        public string MotionState { get; private set; } = "Idle";
        float runBlend, airBlend, landing, uphillBlend;
        float lastYaw, turnLean;
        bool wasGrounded = true;
        void LateUpdate()
        {
            if (!movementRoot || joints == null || joints.Length == 0) return;
            float dt = Mathf.Max(Time.deltaTime, .0001f);
            currentSpeed = motor ? motor.PlanarSpeed : new Vector2(
                movementRoot.position.x-previousPosition.x,movementRoot.position.z-previousPosition.z).magnitude/dt;
            previousPosition=movementRoot.position;
            float target = Mathf.Clamp01(currentSpeed / Mathf.Max(motor ? motor.walkSpeed : 1.2f,.1f));
            float run = Mathf.InverseLerp(motor ? motor.walkSpeed : 1.2f,motor ? motor.sprintSpeed : 4f,currentSpeed);
            bool grounded = !motor || motor.Grounded;
            if (grounded && !wasGrounded && !trackingJump) landing=.9f;
            wasGrounded=grounded;
            landing=Mathf.MoveTowards(landing,0f,dt*4f);
            movement=Mathf.Lerp(movement,target,1f-Mathf.Exp(-7f*dt));
            runBlend=Mathf.Lerp(runBlend,run,1f-Mathf.Exp(-8f*dt));
            airBlend=Mathf.Lerp(airBlend,grounded ? 0f : 1f,1f-Mathf.Exp(-12f*dt));
            float climb = motor ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(3f, 28f, motor.UphillAngle)) : 0f;
            uphillBlend = Mathf.Lerp(uphillBlend, climb, 1f - Mathf.Exp(-9f * dt));
            clock+=dt;
            // Match cadence to the locomotion speed and ease into a turn instead of snapping.
            float yaw = transform.parent ? transform.parent.eulerAngles.y : transform.eulerAngles.y;
            float yawRate = Mathf.DeltaAngle(lastYaw, yaw) / dt;
            lastYaw = yaw;
            float targetLean = Mathf.Clamp(-yawRate * .025f, -7f, 7f) * movement * (1f-airBlend);
            turnLean = Mathf.Lerp(turnLean, targetLean, 1f-Mathf.Exp(-8f*dt));
            // At full walking speed one left-to-left cycle takes 80 frames of the 60 Hz reference.
            float walkDistance = (motor ? motor.walkSpeed : 1.2f) * (80f / 60f);
            float characterScale = motor ? Mathf.Abs(motor.transform.lossyScale.y) : 1f;
            float cycleDistance = Mathf.Lerp(Mathf.Max(.1f, walkDistance), 2.57f * characterScale, runBlend);
            motionPhase = Mathf.Repeat(motionPhase + currentSpeed * dt * (2f * Mathf.PI / cycleDistance), 2f * Mathf.PI);
            MotionState=!grounded ? (motor.VerticalSpeed>0f ? "Jump" : "Fall") :
                currentSpeed<.08f ? "Idle" : run>.65f ? "Run" : run>.15f ? "Jog" : "Walk";
            ApplyLocomotion(clock,motionPhase,movement,runBlend,airBlend,motor ? motor.VerticalSpeed : 0f,landing);
            ApplyReferenceJump(dt);
            ApplyMovementTransition();
            SmoothBrakingPose(dt);
            ApplyClothWind(dt);
            ApplyWalkDetails(dt);
            AnimateHairTips();
            if (!groundPose || !groundPose.isActiveAndEnabled) OpenCapeForStride();
            UpdateIdleBlink(dt);
        }
        void AnimateHairTips()
        {
            if (hairTips == null) return;
            for (int i=0; i<hairTips.Length; i++) {
                var tip = hairTips[i]; if (!tip) continue;
                float slow = Mathf.Sin(clock * 1.15f - i * .83f) * .7f;
                float gait = Mathf.Sin(motionPhase - i * .71f) * movement * Mathf.Lerp(.5f, 2.2f, runBlend);
                tip.localRotation = hairTipRest[i] * Quaternion.AngleAxis(slow + gait, hairTipAxes[i]);
            }
        }
        // Kept for the existing pose-preview and skin-validation editor tools.
        public void ApplyPose(float time,float phase,float amount,bool flying)
        { ApplyLocomotion(time,phase,amount,0f,flying ? 1f : 0f,0f,0f); }

        public void ApplyLocomotion(float time,float phase,float amount,float run,float air,float vertical,float land)
        {
            if(joints==null || joints.Length<14) return;
            CacheSprintCycle();
            CacheWalkCycle();
            float wave=Mathf.Sin(phase);
            float breath=Mathf.Sin(time*1.8f);
            float sway=Mathf.Sin(time*.75f);
            float grounded=1f-air;
            float leftCycle = Mathf.Repeat(phase / (2f * Mathf.PI), 1f);
            float rightCycle = Mathf.Repeat(leftCycle + .5f, 1f);
            float weight = amount * grounded;
            float arms=Mathf.Lerp(39f,48f,run)*amount*grounded;
            // Hold the planted leg longer, while the other foot recovers briskly.
            float recoveryL=Mathf.Pow(Mathf.Max(0f,-wave), 1.15f);
            float recoveryR=Mathf.Pow(Mathf.Max(0f,wave), 1.15f);
            // Keep the walking torso stable; retain the stronger counter-rotation for running.
            float walkStability = Mathf.Lerp(.12f, 1f, run);
            float counterTwist=Mathf.Sin(phase+.22f)*amount*grounded*walkStability;
            float bodyBounce=(1f-Mathf.Cos(2f*phase))*.5f;
            float torsoSway=Mathf.Sin(phase-.15f)*amount*grounded*walkStability;
            float idle=1f-amount;
            transform.localPosition=restPosition+Vector3.up*(
                breath*breathingAmplitude*.55f*idle+Mathf.Lerp(-bodyBounce*.002f,bodyBounce*.018f,run)*amount*grounded-.095f*run*amount*grounded-.065f*land);
            // The stylized run leans forward, recovers upright in air, and banks gently into turns.
            transform.localRotation=restRotation*Quaternion.Euler(4f*air+bodyBounce*1.1f*amount*grounded*walkStability, counterTwist*3.2f, sway*.3f*idle+turnLean*Mathf.Lerp(.15f,.6f,run)+torsoSway*1.4f);
            foreach(var j in joints) if(j.bone) j.bone.localRotation=j.rest;
            float sprintPose = run * amount * grounded;
            float climbPose = uphillBlend * weight;
            float climbLean = Mathf.Lerp(14f, 7f, run) * climbPose;
            if (waist) waist.localRotation = waistRest * Quaternion.AngleAxis(45.5f * sprintPose + climbLean, waistPitchAxis);
            Rotate(0,breath*.7f+18f*sprintPose+5f*land+bodyBounce*1.4f*amount*grounded*walkStability,counterTwist*2.7f+sway*.3f*idle);
            Rotate(1,-breath*.12f-51.5f*sprintPose-.65f*climbLean-bodyBounce*1.6f*amount*grounded*walkStability,-sway*.1f*idle-counterTwist*2.7f-torsoSway*1.4f);
            Rotate(2,-wave*arms-15f*air,-joints[2].side*(Mathf.Lerp(28f, 35f, amount)-breath*.8f-7f*air));
            Rotate(3,wave*arms-15f*air,-joints[3].side*(Mathf.Lerp(28f, 35f, amount)-breath*.8f-7f*air));
            Rotate(4,-8f-38f*run*amount-20f*air-recoveryR*amount*12f+wave*4f*amount*grounded,0f);
            Rotate(5,-8f-38f*run*amount-20f*air-recoveryL*amount*12f-wave*4f*amount*grounded,0f);
            float armBlend = sprintPose;
            GuideStrideArm(2, 4, leftHand, leftCycle, run, weight);
            GuideStrideArm(3, 5, rightHand, rightCycle, run, weight);
            if (fingers != null)
                foreach (var finger in fingers)
                    if (finger.bone) finger.bone.localRotation = finger.rest *
                        Quaternion.AngleAxis(Mathf.Lerp(finger.relaxedCurl, finger.sprintCurl, armBlend), finger.curlAxis);
            float tuck=air*(vertical>0f ? 23f : 10f);
            float thighL = Mathf.Lerp(walkThigh.Evaluate(leftCycle) * (walkingAmplitude / 32f), sprintThigh.Evaluate(leftCycle), run) * weight;
            float thighR = Mathf.Lerp(walkThigh.Evaluate(rightCycle) * (walkingAmplitude / 32f), sprintThigh.Evaluate(rightCycle), run) * weight;
            float kneeL = Mathf.Lerp(walkKnee.Evaluate(leftCycle), sprintKnee.Evaluate(leftCycle), run) * weight;
            float kneeR = Mathf.Lerp(walkKnee.Evaluate(rightCycle), sprintKnee.Evaluate(rightCycle), run) * weight;
            // Lift each advancing knee and tuck its heel while the opposite foot supports the climb.
            float stepL = climbPose * recoveryL;
            float stepR = climbPose * recoveryR;
            float kneeLift = Mathf.Lerp(23f, 9f, run);
            float heelTuck = Mathf.Lerp(29f, 14f, run);
            thighL -= kneeLift * stepL;
            thighR -= kneeLift * stepR;
            kneeL += heelTuck * stepL;
            kneeR += heelTuck * stepR;
            for(int i=6;i<=10;i+=4)
            {
                Rotate(i,thighL-tuck,joints[i].side*8f*idle*grounded);
                Rotate(i+1,thighR-tuck*.65f,joints[i+1].side*8f*idle*grounded);
                Rotate(i+2,kneeL+tuck*1.5f+22f*land,0f);
                Rotate(i+3,kneeR+tuck+22f*land,0f);
            }
        }
        // Set the swing plane from the elbow and wrist, independent of the imported arm axes.
        void GuideStrideArm(int upperIndex, int lowerIndex, Transform hand, float cycle, float run, float blend)
        {
            var upper = joints[upperIndex];
            var elbow = joints[lowerIndex].bone;
            if (blend <= 0f || !upper.bone || !elbow || !hand) return;
            float angle = Mathf.Lerp(walkUpperArm.Evaluate(cycle), sprintUpperArm.Evaluate(cycle), run) * Mathf.Deg2Rad;
            AimSegment(upper.bone, elbow, new Vector3(upper.side * .16f, -Mathf.Cos(angle), Mathf.Sin(angle)), blend);
            float forearm = Mathf.Lerp(walkForearm.Evaluate(cycle), sprintForearm.Evaluate(cycle), run) * Mathf.Deg2Rad;
            AimSegment(elbow, hand, new Vector3(upper.side * Mathf.Lerp(.06f, -.06f, run), -Mathf.Cos(forearm), Mathf.Sin(forearm)), blend);
        }

        void AimSegment(Transform bone, Transform end, Vector3 localDirection, float blend)
        {
            var turn = Quaternion.FromToRotation(end.position - bone.position, transform.TransformDirection(localDirection));
            bone.rotation = Quaternion.Slerp(Quaternion.identity, turn, blend) * bone.rotation;
        }

        void Rotate(int index, float pitch, float roll)
        {
            if (index >= joints.Length) return;
            var j = joints[index];
            if (j.bone) j.bone.localRotation = j.rest *
                Quaternion.AngleAxis(roll, j.rollAxis) * Quaternion.AngleAxis(pitch, j.pitchAxis);
        }
        void OnDisable()
        {
            SetBlinkWeight(0f);
            RestoreWalkDetails();
            trackingJump = false;
            brakePoseReady = false;
            brakeBlendRemaining = 0f;
            uphillBlend = 0f;
            jumpFollow = 0f;
            if (waist) waist.localRotation = waistRest;
            if (capeBelt) { capeBelt.localPosition = Vector3.zero; capeBelt.localScale = Vector3.one; }
            if (joints != null) foreach (var j in joints)
                if (j.bone) j.bone.localRotation = j.rest;
            if (fingers != null) foreach (var finger in fingers)
                if (finger.bone) finger.bone.localRotation = finger.rest;
            if (clothBones != null) for (int i = 0; i < clothBones.Length; i++)
                if (clothBones[i])
                {
                    clothBones[i].localRotation = clothRest[i];
                    if (i % 2 == 0) clothBones[i].localPosition = panels[i / 2].restPosition;
                }
            if (hairTips != null) for (int i = 0; i < hairTips.Length; i++)
                if (hairTips[i]) hairTips[i].localRotation = hairTipRest[i];
            if (hairBones != null) for (int i = 0; i < hairBones.Length; i++)
                if (hairBones[i]) hairBones[i].localRotation = hairRest[i];
            transform.localPosition = restPosition;
            transform.localRotation = restRotation;
        }
    }
}

