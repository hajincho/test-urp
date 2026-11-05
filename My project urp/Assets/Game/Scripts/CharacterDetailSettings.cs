using UnityEngine;
using UnityEngine.Rendering;

namespace Game.ThirdPerson
{
    // Character fidelity is independent of the environment's quality tier.
    [DefaultExecutionOrder(-300)]
    public sealed class CharacterDetailSettings : MonoBehaviour
    {
        void OnEnable() => ApplyCharacterQuality();

        public void ApplyCharacterQuality()
        {
            foreach (var skin in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                skin.quality = SkinQuality.Bone4;
                skin.skinnedMotionVectors = true;
                skin.shadowCastingMode = ShadowCastingMode.TwoSided;
                skin.receiveShadows = true;
                skin.updateWhenOffscreen = true;
                skin.allowOcclusionWhenDynamic = false;
            }
            foreach (var animator in GetComponentsInChildren<Animator>(true))
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            foreach (var lod in GetComponentsInChildren<LODGroup>(true))
                lod.ForceLOD(0);
        }
    }
}
