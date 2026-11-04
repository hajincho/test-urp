using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.ThirdPerson
{
    public sealed partial class UntitledCharacterMotion
    {
        static AnimationCurve walkThigh, walkKnee, walkUpperArm, walkForearm;
        static AnimationCurve hairTrace, hemTrace, pendantTrace;
        Vector2 follow, followVelocity;

        struct HangingJoint
        {
            public Transform bone;
            public Quaternion rest;
            public Vector3 pitch, roll;
            public float amplitude, phaseLag, side;
            public int panel;
            public bool hem;
        }
        HangingJoint[] hanging;

        static void CacheWalkCycle()
        {
            if (walkThigh != null) return;
            // Left mid-stance is phase zero. The trailing foot lifts, passes the support
            // leg with a soft knee, then extends for the next heel strike.
            walkThigh = MakeCycle(-4, 11, 20, 6, -16, -31, -29, -18);
            walkKnee = MakeCycle(7, 9, 24, 49, 43, 24, 7, 12);
            walkUpperArm = MakeCycle(4, 16, 22, 11, -5, -19, -24, -12);
            walkForearm = MakeCycle(17, 28, 31, 20, 7, -10, -16, 1);
            // Free-end offsets digitized against the nearby anchor over two profile cycles.
            // Rotated two bins from the sampled image sequence to left mid-stance.
            hairTrace = MakeCycle(-.5f, -2.9f, .45f, 1.5f, 1.45f, -1.65f, .05f, 1.5f);
            hemTrace = MakeCycle(-2.8f, 1.2f, 2.45f, 2.2f, -2.7f, -2.2f, 1.95f, -.05f);
            pendantTrace = MakeCycle(1.65f, 5.55f, 4.2f, -.55f, -4.3f, -3.15f, -2.3f, -1.05f);
        }

        void CacheWalkDetails()
        {
            CacheWalkCycle();
            var bones = GetComponentsInChildren<Transform>(true);
            var found = new List<HangingJoint>();
            // The belt plate and its pendant have different pivots. The head ornaments
            // and neck ribbon have shorter chains than the hanging strip at the waist.
            AddChain(bones, found, new[] { 28, 29 }, .40f);
            AddChain(bones, found, new[] { 31, 32, 33 }, 1.05f);
            AddChain(bones, found, new[] { 201, 202 }, .43f);
            AddChain(bones, found, new[] { 205, 206 }, .43f);
            AddChain(bones, found, new[] { 235, 236 }, .52f);
            // One lower hinge per panel lets the hem lag behind the stiffer cloth at the belt.
            int[] hemIds = { 38, 48, 59, 70, 81, 92, 103, 114, 125, 136, 147, 158 };
            for (int i = 0; i < hemIds.Length; i++)
                AddChain(bones, found, new[] { hemIds[i] }, 1f, i);
            hanging = found.ToArray();
            follow = followVelocity = Vector2.zero;
        }

        void AddChain(Transform[] bones, List<HangingJoint> found, int[] ids, float scale, int panel = -1)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                string name = "Bone_" + ids[i].ToString("D3");
                var bone = Array.Find(bones, b => b.name == name);
                if (!bone) continue;
                bool hem = panel >= 0;
                if (hem) panels[panel].hem = bone;
                found.Add(new HangingJoint {
                    bone = bone, rest = bone.localRotation, hem = hem, panel = panel,
                    pitch = bone.InverseTransformDirection(transform.right).normalized,
                    roll = bone.InverseTransformDirection(transform.forward).normalized,
                    amplitude = scale * (i == 0 ? 1f : .65f),
                    side = Mathf.Clamp(transform.InverseTransformPoint(bone.position).x * 8f, -1f, 1f),
                    phaseLag = i * .035f + (hem ? .04f : 0f)
                });
            }
        }

        void ApplyWalkDetails(float dt)
        {
            if (hanging == null) return;
            // A damped spring retains a little momentum through a stop or reversal.
            // Analytic stepping keeps the response stable across frame rates.
            float speed = Mathf.Clamp(currentSpeed / Mathf.Max(motor ? motor.walkSpeed : 1.2f, .1f), 0f, 1.6f);
            Vector2 target = new Vector2(speed, turnLean);
            Vector2 offset = follow - target;
            const float decay = 6f, frequency = 10f;
            Vector2 b = (followVelocity + decay * offset) / frequency;
            float c = Mathf.Cos(frequency * dt), s = Mathf.Sin(frequency * dt), envelope = Mathf.Exp(-decay * dt);
            follow = target + envelope * (offset * c + b * s);
            followVelocity = envelope * (followVelocity * c - (decay * b + frequency * offset) * s);

            float groundedMotion = Mathf.Clamp01(follow.x) * (1f - airBlend);
            float gait = groundedMotion * (1f - runBlend);
            float idle = (1f - movement) * (1f - airBlend);
            float drag = follow.x - speed;
            float phase = motionPhase / (2f * Mathf.PI);
            for (int i = 0; i < hairBones.Length; i++)
            {
                if (!hairBones[i]) continue;
                int segment = i % 3;
                float sample = Mathf.Repeat(phase - segment * .026f, 1f);
                float gain = segment == 0 ? .65f : .45f;
                float pitch = gait * (4f + hairTrace.Evaluate(sample) * gain) - drag * 2.8f;
                float falloff = (.35f + segment * .30f) * idle;
                float delay = i / 3 * .18f + segment * .48f;
                pitch += falloff * Mathf.Sin(clock * 1.42f - delay);
                float roll = follow.y * (.20f + .10f * segment)
                           + falloff * (1.5f * Mathf.Sin(clock * 1.12f - delay) + .48f * Mathf.Sin(clock * 2.05f - delay));
                hairBones[i].localRotation *= Quaternion.AngleAxis(pitch, hairAxes[i])
                                            * Quaternion.AngleAxis(roll, hairRollAxes[i]);
            }
            for (int i = 0; i < clothBones.Length; i++)
            {
                if (!clothBones[i]) continue;
                float back = 1f - panels[i / 2].front;
                float gain = (i % 2 == 0 ? .45f : .14f) * (1f - .75f * back);
                float pitch = gait * (capeTrailAngles[i] * Mathf.Lerp(.17f, .05f, back) + gain * hemTrace.Evaluate(phase)) - drag * gain;
                clothBones[i].localRotation *= Quaternion.AngleAxis(pitch, clothAxes[i]);
            }
            foreach (var j in hanging)
            {
                if (!j.bone) continue;
                float t = Mathf.Repeat(phase - j.phaseLag - j.side * .02f, 1f);
                float trace = j.hem ? hemTrace.Evaluate(t) : pendantTrace.Evaluate(t);
                float sway = Mathf.Sin(clock * 1.6f - j.phaseLag * 8f);
                float detailWeight = j.hem ? gait : groundedMotion;
                float backStiffness = j.hem && j.panel >= 0 ? 1f - .72f * (1f - panels[j.panel].front) : 1f;
                float pitch = backStiffness * j.amplitude * (detailWeight * trace + .55f * sway - drag * 4f + 2f * jumpFollow);
                // The weighted belt stays put. Let the hem and hanging tips breathe a little at rest.
                float lag = 9f * j.phaseLag + j.side * .36f;
                float idleTip = Mathf.Sin(clock * (j.hem ? 1.05f : 1.56f) - lag)
                              + .28f * Mathf.Sin(clock * 2.38f - lag * 1.4f);
                float roll = j.amplitude * (.56f * follow.y + detailWeight * 1.7f * Mathf.Sin(2f * Mathf.PI * t)
                           + idle * idleTip * (j.hem ? 1.05f : 2.25f));
                pitch += idle * j.amplitude * .65f * Mathf.Sin(clock * 1.31f - lag);
                j.bone.localRotation = j.rest * Quaternion.AngleAxis(pitch, j.pitch) * Quaternion.AngleAxis(roll * backStiffness, j.roll);
            }
        }

        void RestoreWalkDetails()
        {
            if (hanging == null) return;
            foreach (var j in hanging) if (j.bone) j.bone.localRotation = j.rest;
        }
    }
}
