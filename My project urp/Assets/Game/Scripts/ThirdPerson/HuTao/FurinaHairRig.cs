using System.Collections.Generic;
using UnityEngine;

namespace Game.ThirdPerson
{
    /// <summary>
    /// Gives Furina's hair the same multi-joint chain physics as Hu Tao (<see cref="HuTaoChainPhysics"/> in Furina naming).
    /// Before the solver starts, the short hair chains get extra skinned hinges, using the same weight-splitting
    /// method as BlenderCharacterRetarget.SetupCurlTips: the terminal bone's weighted extent along its axis is
    /// divided into equal segments and only that bone's influence is redistributed (all other weights unchanged).
    ///   CF_2_L / CF_2_R (side locks, 2 joints each)  -> +4 hinges each (CFExt_*)
    ///   DM_5_1 (crown ahoge, 6 joints, never animated before) -> +3 hinges (DMExt_*)
    /// The braids keep their 18 joints plus the 5 existing curl hinges each. Nothing else on Furina changes.
    /// Removing this component restores the original mesh and bone list.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FurinaHairRig : MonoBehaviour
    {
        struct Extension { public string tip, prefix; public int segments; }
        static readonly Extension[] Extensions =
        {
            new Extension { tip = "CF_2_L", prefix = "CFExt_L_", segments = 4 },
            new Extension { tip = "CF_2_R", prefix = "CFExt_R_", segments = 4 },
            new Extension { tip = "DM_5_1", prefix = "DMExt_", segments = 3 },
        };

        SkinnedMeshRenderer skin;
        Mesh previousMesh, createdMesh;
        Transform[] previousBones;
        readonly List<Transform> addedRoots = new List<Transform>();

        public int AddedJoints { get; private set; }
        public HuTaoChainPhysics Physics { get; private set; }

        /// <summary>Adds the hinges and the hair solver to the Furina model (idempotent).</summary>
        public static FurinaHairRig Install(GameObject furina)
        {
            var rig = furina.GetComponent<FurinaHairRig>();
            if (rig) return rig;
            rig = furina.AddComponent<FurinaHairRig>();
            rig.ExtendChains();
            rig.Physics = furina.GetComponent<HuTaoChainPhysics>();
            if (!rig.Physics)
            {
                rig.Physics = furina.AddComponent<HuTaoChainPhysics>();
                rig.Physics.naming = RigNaming.Furina;
                rig.Physics.idleBreeze = .45f;
                rig.Physics.runFlutter = 0f; // hair only: no garment flap
            }
            Debug.Log($"[FurinaHairRig] added hinges={rig.AddedJoints} hair bones simulated={rig.Physics.ActiveBoneCount}");
            return rig;
        }

        void ExtendChains()
        {
            var names = new Dictionary<string, Transform>();
            foreach (var t in GetComponentsInChildren<Transform>(true)) names[t.name] = t;
            foreach (var candidate in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (names.TryGetValue("CF_2_L", out var probe) && System.Array.IndexOf(candidate.bones, probe) >= 0) { skin = candidate; break; }
            if (!skin || !skin.sharedMesh || !skin.sharedMesh.isReadable)
            {
                Debug.LogWarning("[FurinaHairRig] Hair mesh missing or not readable; hinges not added (existing joints are still simulated).");
                return;
            }
            var mesh = skin.sharedMesh;
            previousMesh = mesh;
            previousBones = skin.bones;
            var bones = new List<Transform>(previousBones);
            var bind = new List<Matrix4x4>(mesh.bindposes);
            var vertices = mesh.vertices;
            var weights = mesh.boneWeights;
            foreach (var extension in Extensions)
                if (names.TryGetValue(extension.tip, out var tip) && !names.ContainsKey(extension.prefix + "0"))
                    AddedJoints += Extend(tip, extension.prefix, extension.segments, bones, bind, vertices, weights);
            if (AddedJoints == 0) return;
            createdMesh = Instantiate(mesh);
            createdMesh.name = mesh.name + " (hair hinges)";
            createdMesh.bindposes = bind.ToArray();
            createdMesh.boneWeights = weights;
            skin.bones = bones.ToArray();
            skin.sharedMesh = createdMesh;
        }

        int Extend(Transform tip, string prefix, int segments, List<Transform> bones, List<Matrix4x4> bind,
            Vector3[] vertices, BoneWeight[] weights)
        {
            int tipIndex = bones.IndexOf(tip);
            if (tipIndex < 0 || tipIndex >= bind.Count) return 0;
            var tipBind = bind[tipIndex];
            float low = 0f, high = 0f;
            for (int i = 0; i < weights.Length; i++)
            {
                if (Weight(weights[i], tipIndex) <= .0001f) continue;
                float y = tipBind.MultiplyPoint3x4(vertices[i]).y;
                low = Mathf.Min(low, y); high = Mathf.Max(high, y);
            }
            // Use the bone axis direction along which the weighted strand actually extends.
            float direction = high >= -low ? 1f : -1f;
            float extent = direction > 0f ? high : -low;
            if (extent <= .00001f) return 0;
            float step = extent * .92f / segments;

            int first = bones.Count;
            Transform parent = tip;
            for (int j = 0; j < segments; j++)
            {
                var added = new GameObject(prefix + j).transform;
                added.SetParent(parent, false);
                added.localPosition = Vector3.up * (direction * step);
                added.localRotation = Quaternion.identity;
                added.localScale = Vector3.one;
                if (j == 0) addedRoots.Add(added);
                bones.Add(added);
                bind.Add(Matrix4x4.Translate(Vector3.down * ((j + 1) * direction * step)) * tipBind);
                parent = added;
            }

            var list = new List<KeyValuePair<int, float>>(6);
            for (int i = 0; i < weights.Length; i++)
            {
                var w = weights[i];
                if (Weight(w, tipIndex) <= 0f) continue;
                list.Clear();
                int[] indices = { w.boneIndex0, w.boneIndex1, w.boneIndex2, w.boneIndex3 };
                float[] values = { w.weight0, w.weight1, w.weight2, w.weight3 };
                for (int k = 0; k < 4; k++)
                {
                    if (values[k] <= 0f) continue;
                    if (indices[k] != tipIndex) { list.Add(new KeyValuePair<int, float>(indices[k], values[k])); continue; }
                    float y = tipBind.MultiplyPoint3x4(vertices[i]).y * direction;
                    float sample = Mathf.Clamp(y / step, 0f, segments);
                    int lower = Mathf.FloorToInt(sample), upper = Mathf.Min(segments, lower + 1);
                    float mix = sample - lower;
                    int a = lower == 0 ? tipIndex : first + lower - 1;
                    int b = upper == 0 ? tipIndex : first + upper - 1;
                    list.Add(new KeyValuePair<int, float>(a, values[k] * (1f - mix)));
                    if (mix > 0f) list.Add(new KeyValuePair<int, float>(b, values[k] * mix));
                }
                for (int a = 0; a < list.Count; a++)
                    for (int b = list.Count - 1; b > a; b--)
                        if (list[a].Key == list[b].Key)
                        {
                            list[a] = new KeyValuePair<int, float>(list[a].Key, list[a].Value + list[b].Value);
                            list.RemoveAt(b);
                        }
                list.Sort((x, y) => y.Value.CompareTo(x.Value));
                while (list.Count < 4) list.Add(new KeyValuePair<int, float>(0, 0f));
                float sum = list[0].Value + list[1].Value + list[2].Value + list[3].Value;
                if (sum <= .000001f) continue;
                weights[i] = new BoneWeight
                {
                    boneIndex0 = list[0].Key, weight0 = list[0].Value / sum,
                    boneIndex1 = list[1].Key, weight1 = list[1].Value / sum,
                    boneIndex2 = list[2].Key, weight2 = list[2].Value / sum,
                    boneIndex3 = list[3].Key, weight3 = list[3].Value / sum
                };
            }
            return segments;
        }

        static float Weight(BoneWeight w, int index) =>
            (w.boneIndex0 == index ? w.weight0 : 0f) + (w.boneIndex1 == index ? w.weight1 : 0f)
            + (w.boneIndex2 == index ? w.weight2 : 0f) + (w.boneIndex3 == index ? w.weight3 : 0f);

        void OnDestroy()
        {
            if (Physics) Destroy(Physics);
            if (skin && createdMesh && skin.sharedMesh == createdMesh)
            {
                skin.sharedMesh = previousMesh;
                skin.bones = previousBones;
            }
            if (createdMesh) Destroy(createdMesh);
            foreach (var added in addedRoots) if (added) Destroy(added.gameObject);
        }
    }
}
