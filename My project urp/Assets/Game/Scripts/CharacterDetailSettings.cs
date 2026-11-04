using UnityEngine;
using UnityEngine.Rendering;

namespace Game.ThirdPerson
{
    public sealed class CharacterDetailSettings : MonoBehaviour
    {
        void Awake()
        {
            foreach(var skin in GetComponentsInChildren<SkinnedMeshRenderer>(true)) {
                skin.quality=SkinQuality.Bone4;
                skin.skinnedMotionVectors=true;
                skin.shadowCastingMode=ShadowCastingMode.TwoSided;
                skin.receiveShadows=true;
            }
            foreach(var lod in GetComponentsInChildren<LODGroup>(true))lod.ForceLOD(0);
        }
    }
}
