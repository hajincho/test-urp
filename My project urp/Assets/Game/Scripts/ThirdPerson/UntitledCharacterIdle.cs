using System;
using UnityEngine;

namespace Game.ThirdPerson
{
    public sealed partial class UntitledCharacterMotion
    {
        struct BlinkPart
        {
            public SkinnedMeshRenderer renderer;
            public int channel;
        }
        BlinkPart[] blinkParts;
        public float BlinkWeight { get; private set; }
        System.Random blinkTiming;
        float blinkWait, blinkTime;
        const float CloseTime = .045f, HoldTime = .065f, OpenTime = .085f;

        void CacheBlink()
        {
            var list = new System.Collections.Generic.List<BlinkPart>();
            foreach (var part in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                int channel = part.sharedMesh ? part.sharedMesh.GetBlendShapeIndex("IdleBlink") : -1;
                if (channel >= 0) list.Add(new BlinkPart { renderer = part, channel = channel });
            }
            blinkParts = list.ToArray();
            blinkTiming = new System.Random();
            blinkTime = -1f;
            blinkWait = 2.6f + (float)blinkTiming.NextDouble();
        }

        void UpdateIdleBlink(float dt)
        {
            if (blinkTime < 0f)
            {
                if (MotionState != "Idle" || trackingJump) return;
                blinkWait -= dt;
                if (blinkWait > 0f) return;
                blinkTime = 0f;
            }
            blinkTime += dt;
            float weight = blinkTime < CloseTime ? Mathf.SmoothStep(0f, 100f, blinkTime / CloseTime)
                : Mathf.SmoothStep(100f, 0f, (blinkTime - CloseTime - HoldTime) / OpenTime);
            SetBlinkWeight(weight);
            if (blinkTime >= CloseTime + HoldTime + OpenTime)
            {
                blinkTime = -1f;
                blinkWait = 4.0f + (float)blinkTiming.NextDouble();
            }
        }

        void SetBlinkWeight(float weight)
        {
            BlinkWeight = weight;
            if (blinkParts == null) return;
            foreach (var part in blinkParts)
                if (part.renderer) part.renderer.SetBlendShapeWeight(part.channel, weight);
        }
    }
}

