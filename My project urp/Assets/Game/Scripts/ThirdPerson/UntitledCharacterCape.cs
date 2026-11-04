using System;
using UnityEngine;

namespace Game.ThirdPerson
{
    public sealed partial class UntitledCharacterMotion
    {
        struct CapePanel
        {
            public Vector3 outward, restPosition;
            public float angle;
            public float front;
            public Transform hem;
        }
        CapePanel[] panels;
        Transform leftToe, rightToe, capeBelt;
        CharacterGroundAndLook groundPose;

        void BindCapeWaist()
        {
            // The top row of both fabric layers is skinned directly to the pelvis. Give just
            // those renderers a matching movable anchor so a high thigh can lift the seam.
            if (!capeBelt && clothBones[0])
            {
                var pelvis = clothBones[0].parent;
                capeBelt = pelvis.Find("CapeWaist");
                if (!capeBelt)
                {
                    capeBelt = new GameObject("CapeWaist").transform;
                    capeBelt.SetParent(pelvis, false);
                }
                foreach (var part in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (part.name != "CharacterMesh_09" && part.name != "CharacterMesh_10") continue;
                    var bones = part.bones;
                    int index = Array.IndexOf(bones, pelvis);
                    if (index < 0) continue;
                    bones[index] = capeBelt;
                    part.bones = bones;
                }
            }
        }

        internal void OpenCapeForStride()
        {
            if (!leftToe || !rightToe || joints == null || joints.Length < 14) return;
            var leftKnee = joints[8].bone;
            var rightKnee = joints[9].bone;
            if (!leftKnee || !rightKnee) return;
            var toModel = transform.worldToLocalMatrix;
            var kneeL = toModel.MultiplyPoint3x4(leftKnee.position);
            var kneeR = toModel.MultiplyPoint3x4(rightKnee.position);
            var ankleL = toModel.MultiplyPoint3x4(leftToe.parent.position);
            var ankleR = toModel.MultiplyPoint3x4(rightToe.parent.position);
            var toeL = toModel.MultiplyPoint3x4(leftToe.position);
            var toeR = toModel.MultiplyPoint3x4(rightToe.position);
            float running = runBlend * GroundStrideWeight;
            // Leave a little ease at the waist while settling into a planted pose.
            float clearance = Mathf.Max(.38f * (1f - airBlend), Mathf.Clamp01(GroundStrideWeight * 3f));
            if (capeBelt)
            {
                capeBelt.position = capeBelt.parent.position + transform.TransformVector(new Vector3(0f, .045f, .012f) * clearance);
                capeBelt.localScale = Vector3.one * (1f + .10f * clearance);
            }
            if (clearance < .01f) return;

            for (int i = 0; i < clothBones.Length; i += 2)
            {
                var belt = clothBones[i];
                var next = clothBones[i + 1];
                if (!belt || !next) continue;
                var outward = panels[i / 2].outward;
                float front = panels[i / 2].front * runBlend * (1f - airBlend);
                var pivot = toModel.MultiplyPoint3x4(belt.position);
                float knees = Mathf.Max(ClearanceSlope(kneeL - pivot, outward, .09f), ClearanceSlope(kneeR - pivot, outward, .09f));
                float ankles = Mathf.Max(ClearanceSlope(ankleL - pivot, outward, .075f), ClearanceSlope(ankleR - pivot, outward, .075f));
                float toes = Mathf.Max(ClearanceSlope(toeL - pivot, outward, .065f), ClearanceSlope(toeR - pivot, outward, .065f));
                float required = Mathf.Atan(Mathf.Max(knees, Mathf.Max(ankles, toes))) * Mathf.Rad2Deg;
                // Make room immediately, then let the fabric fall as the leg passes.
                ref CapePanel panel = ref panels[i / 2];
                float rear = 1f - panel.front;
                float response = 1f - Mathf.Exp(-Mathf.Lerp(16f, 7f, rear) * Time.deltaTime);
                required = Mathf.Lerp(panel.angle, required, response);
                panel.angle = required;
                var drape = toModel.MultiplyPoint3x4(next.position) - pivot;
                float current = Mathf.Atan2(Vector3.Dot(drape, outward), -drape.y) * Mathf.Rad2Deg;
                // The lifted front seam already provides some of the clearance.
                float open = Mathf.Clamp(required * Mathf.Lerp(1f, .83f, front) + 7f * (1f - front) - current, 0f, 90f) * clearance;
                var hinge = transform.TransformDirection(Vector3.Cross(Vector3.down, outward));
                float lift = (.025f + .05f * Mathf.InverseLerp(0f, 65f, required)) * clearance;
                // Give the leading knee room in front of the belt instead of lifting the whole panel.
                var beltOffset = outward + 1.7f * front * Vector3.forward + (1f - .35f * front) * Vector3.up;
                belt.position += transform.TransformVector(lift * beltOffset);
                belt.rotation = Quaternion.AngleAxis(open, hinge) * belt.rotation;
                // Keep the bend over the thigh. Below it the fabric hangs back down and the tip can sway.
                float bend = open * front;
                next.rotation = Quaternion.AngleAxis(-.45f * bend, hinge) * next.rotation;
                if (panel.hem)
                {
                    float tipBend = .55f * bend + .24f * open * running * (1f - panel.front);
                    panel.hem.rotation = Quaternion.AngleAxis(-tipBend, hinge) * panel.hem.rotation;
                }
            }
        }

        // Radial reach per unit drop, including the thickness of the knee or boot.
        static float ClearanceSlope(Vector3 offset, Vector3 outward, float radius)
        {
            return (Vector3.Dot(offset, outward) + radius) / Mathf.Max(.01f, -offset.y - radius);
        }
    }
}
