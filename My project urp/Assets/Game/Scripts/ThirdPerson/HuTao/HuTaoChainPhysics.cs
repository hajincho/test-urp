using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Game.ThirdPerson
{
    public enum ChainKind { None, HairBack, HairSide, HairFront, Tassel, HatStrap, Belt, Hem, Ahoge }
    public enum RigNaming { Auto, HuTao, Furina }

    /// <summary>
    /// Fixed-step position-based chain dynamics for every secondary bone chain of the Hu Tao rig
    /// (hair, hat tassel / straps, belt ribbons and rear coat hem). The solver follows the same
    /// design as <see cref="SilkClothDynamics"/> (60 Hz sub-steps, spring toward the animated pose,
    /// rotation-limited PBD length constraints, curvature retention, capsule body collision,
    /// neighbouring-chain separation, twist) but discovers chains by structure/name
    /// instead of the "HB" prefix and uses a per-kind parameter profile.
    /// </summary>
    [DefaultExecutionOrder(198)]
    [DisallowMultipleComponent]
    public sealed class HuTaoChainPhysics : MonoBehaviour
    {
        [Serializable]
        public sealed class BoneSettings
        {
            public Transform bone;
            public string chain;
            public ChainKind kind;
            public int depth;
            public int chainLength;
            public int weightedVertices;
            public Vector3 restLocalPosition;
            public Quaternion restLocalRotation = Quaternion.identity;
            public Vector3 tipLocal;
            [Range(.1f, 24f)] public float stiffness = 5f;
            [Range(.1f, 16f)] public float damping = 3.5f;
            [Range(.005f, .2f)] public float mass = .025f;
            [Range(0f, 2f)] public float gravityInfluence = .3f;
            [Range(2f, 85f)] public float rotationLimit = 45f;
            [Range(0f, 1f)] public float parentMotionInfluence = .3f;
            [Range(0f, 2f)] public float velocityResponse = .18f;
            [Range(0f, 2f)] public float accelerationResponse = .4f;
            [Range(0f, 2f)] public float angularVelocityResponse = .35f;
            [Range(.001f, .08f)] public float collisionRadius = .012f;
            [Range(0f, 1f)] public float breeze = 0f;
            [Tooltip("Running flap: travelling-wave + lift acceleration per (m/s)^2 of ground speed.")]
            [Range(0f, 2f)] public float flutter = 0f;
            [Tooltip("Stiffness multiplier at full run (lower = looser while running).")]
            [Range(.2f, 1f)] public float runSoftening = 1f;
            [Tooltip("Rotation-limit multiplier at full run.")]
            [Range(1f, 2f)] public float runLimitBoost = 1f;
            [Tooltip("Cross-section radius / segment length measured by HairChainSmoother (0 = not measured).")]
            public float thickness;
            [Tooltip("Max bend between this segment and the next (deg); 0 = automatic. Thick hair gets a tighter limit.")]
            [Range(0f, 60f)] public float bendLimit;
            [Tooltip("Bend-limit multiplier at full run (thick hair stays visually straight while running).")]
            [Range(.2f, 1f)] public float runBendScale = 1f;
            [NonSerialized] public float peakDeflection;
        }

        struct Profile
        {
            public float stiffRoot, stiffTip, dampRoot, dampTip, massRoot, massTip;
            public float gravRoot, gravTip, limitRoot, limitTip, parentRoot, parentTip;
            public float velRoot, velTip, accRoot, accTip, angRoot, angTip, radRoot, radTip, breeze;
            public float flutterRoot, flutterTip, runSoftening, runLimitBoost;
        }

        struct Particle
        {
            public Vector3 position, previousPosition, renderPrevious;
            public int parent, settings, chain;
            public float length, inverseMass;
        }

        struct LayerPair
        {
            public int a, b;
            public float separation;
            public Vector3 fallback;
        }

        struct BodyCapsule
        {
            public Transform a, b;
            public float radius;
            public Vector3 start, end;
            public Vector3 previousStart, previousEnd, targetStart, targetEnd;
        }

        [Tooltip("Every secondary chain bone, including the zero-weight chain terminals.")]
        public List<BoneSettings> bones = new List<BoneSettings>();
        [Range(2, 16)] public int constraintIterations = 8;
        [Range(1, 12)] public int maximumSubsteps = 6;
        [Range(0f, 2f)] public float aerodynamicScale = 1f;
        [Range(0f, 2f)] public float bodyCollisionScale = 1f;
        [Range(0f, 2f)] public float layerCollisionScale = 1f;
        [Range(0f, 1f)] public float curvatureRetention = .65f;
        [Range(.1f, 5f)] public float teleportDistance = 1.5f;
        [Tooltip("Metres/second^2 of slow, bounded gust noise applied to loose strands only (0 disables).")]
        [Range(0f, 3f)] public float idleBreeze = .6f;
        [Tooltip("Global scale of the running flap on the hem and belt ribbons.")]
        [Range(0f, 3f)] public float runFlutter = 1f;
        [Tooltip("Ground speeds (m/s) between which the run-only behaviour blends in.")]
        public Vector2 runSpeedRange = new Vector2(1.6f, 5f);
        public RigNaming naming = RigNaming.Auto;
        public bool trackUtilization = true;
        public bool measureErrors = false;
        [Tooltip("Physical seconds, independent of rendering frame rate.")]
        public const float FixedStep = 1f / 60f;

        Particle[] particles = Array.Empty<Particle>();
        Vector3[] currentTargets = Array.Empty<Vector3>();
        Vector3[] previousTargets = Array.Empty<Vector3>();
        Vector3[] stepTargets = Array.Empty<Vector3>();
        Vector3[] lastStepTargets = Array.Empty<Vector3>();
        Vector3[] rendered = Array.Empty<Vector3>();
        int[] tails = Array.Empty<int>();
        int[] parents = Array.Empty<int>();
        float[] twist = Array.Empty<float>();
        float[] twistVelocity = Array.Empty<float>();
        float[] chainPhase = Array.Empty<float>();
        LayerPair[] layerPairs = Array.Empty<LayerPair>();
        BodyCapsule[] bodyCapsules = Array.Empty<BodyCapsule>();
        bool[] attachmentContacts = Array.Empty<bool>();
        ThirdPersonMotor motor;
        Transform actor, hips;
        Vector3 previousActorPosition, actorVelocity, actorAcceleration, angularVelocity;
        Vector3 previousDriverPosition, angularAcceleration;
        Quaternion previousActorRotation;
        float accumulator, scale = 1f, runBlend;
        // Not serialized: after a script hot-reload in Play Mode the solver rebuilds its runtime arrays.
        [NonSerialized] bool initialized;
        [NonSerialized] bool needsReset = true;

        public bool OwnsChains => isActiveAndEnabled && initialized;
        public int ActiveBoneCount => initialized ? bones.Count : 0;
        public int ParticleCount => particles.Length;
        public int LayerConstraintCount => layerPairs.Length;
        public int BodyCapsuleCount => bodyCapsules.Length;
        public int ResetCount { get; private set; }
        public long SimulatedSteps { get; private set; }
        public float DiscardedTime { get; private set; }
        public float MaximumLengthError { get; private set; }
        public float MaximumBodyPenetration { get; private set; }
        public double LastStepMilliseconds { get; private set; }
        public float RunBlend => runBlend;

        void Awake() => InitializeRig();
        void OnEnable() => needsReset = true;
        void OnDisable()
        {
            RestoreRestPose();
            needsReset = true;
        }

        void LateUpdate() => StepSimulation(Time.deltaTime);

        // ------------------------------------------------------------------ chain discovery

        /// <summary>Resolved naming scheme: Hu Tao (MMD names) or Furina (Blender names). Auto detects it.</summary>
        public RigNaming Naming
        {
            get
            {
                if (naming != RigNaming.Auto) return naming;
                foreach (var t in GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == "腰") return naming = RigNaming.HuTao;
                    if (t.name == "Hips") return naming = RigNaming.Furina;
                }
                return RigNaming.HuTao;
            }
        }

        ChainKind Classify(string name) => Naming == RigNaming.Furina ? ClassifyFurina(name) : ClassifyHuTao(name);

        /// <summary>Furina hair: 18-joint braids F_n_1_L/R (+ the retarget's curl hinges and their extensions),
        /// side locks CF_1/CF_2 (+ CFExt hinges) and the crown ahoge DM_0..5 (+ DMExt hinges).
        /// Garment HB bones belong to SilkClothDynamics and are never claimed here.</summary>
        public static ChainKind ClassifyFurina(string name)
        {
            if (string.IsNullOrEmpty(name) || name.StartsWith("HB", StringComparison.Ordinal)) return ChainKind.None;
            if (name.StartsWith("DM_", StringComparison.Ordinal) || name.StartsWith("DMExt_", StringComparison.Ordinal)) return ChainKind.Ahoge;
            if (name.StartsWith("CF_", StringComparison.Ordinal) || name.StartsWith("CFExt_", StringComparison.Ordinal)) return ChainKind.HairSide;
            if (name.StartsWith("ReferenceCurlTip_", StringComparison.Ordinal)) return ChainKind.HairBack;
            if (name.StartsWith("F_", StringComparison.Ordinal) && (name.EndsWith("_1_L", StringComparison.Ordinal)
                || name.EndsWith("_1_R", StringComparison.Ordinal))) return ChainKind.HairBack;
            return ChainKind.None;
        }

        /// <summary>Classifies a bone by the naming scheme of the MMD-derived Hu Tao rig.</summary>
        public static ChainKind ClassifyHuTao(string name)
        {
            if (string.IsNullOrEmpty(name)) return ChainKind.None;
            if (name.Contains("后摆")) return ChainKind.Hem;
            if (name.Contains("帽流苏")) return ChainKind.Tassel;
            if (name.Contains("帽带")) return ChainKind.HatStrap;
            if (name.Contains("后发")) return ChainKind.HairBack;
            if (name.Contains("侧发")) return ChainKind.HairSide;
            if (name.Contains("发1_")) return ChainKind.HairFront;
            if (name.Contains("带_")) return ChainKind.Belt;
            return ChainKind.None;
        }

        static Profile ProfileFor(ChainKind kind)
        {
            switch (kind)
            {
                case ChainKind.HairBack:
                    return new Profile { stiffRoot = 7f, stiffTip = 2.4f, dampRoot = 5.8f, dampTip = 3.4f, massRoot = .03f, massTip = .02f,
                        gravRoot = .12f, gravTip = .42f, limitRoot = 22f, limitTip = 68f, parentRoot = .82f, parentTip = .10f,
                        velRoot = .06f, velTip = .26f, accRoot = .20f, accTip = .80f, angRoot = .22f, angTip = .72f,
                        radRoot = .010f, radTip = .016f, breeze = .45f, runSoftening = 1f, runLimitBoost = 1f };
                case ChainKind.HairSide:
                    return new Profile { stiffRoot = 10f, stiffTip = 4f, dampRoot = 6f, dampTip = 4f, massRoot = .028f, massTip = .02f,
                        gravRoot = .10f, gravTip = .34f, limitRoot = 18f, limitTip = 50f, parentRoot = .85f, parentTip = .20f,
                        velRoot = .05f, velTip = .20f, accRoot = .15f, accTip = .55f, angRoot = .18f, angTip = .55f,
                        radRoot = .008f, radTip = .012f, breeze = .35f, runSoftening = 1f, runLimitBoost = 1f };
                case ChainKind.HairFront:
                    return new Profile { stiffRoot = 12f, stiffTip = 5f, dampRoot = 6.2f, dampTip = 4.4f, massRoot = .026f, massTip = .02f,
                        gravRoot = .08f, gravTip = .26f, limitRoot = 14f, limitTip = 40f, parentRoot = .88f, parentTip = .30f,
                        velRoot = .04f, velTip = .16f, accRoot = .12f, accTip = .45f, angRoot = .15f, angTip = .50f,
                        radRoot = .007f, radTip = .011f, breeze = .30f, runSoftening = 1f, runLimitBoost = 1f };
                case ChainKind.Tassel:
                    return new Profile { stiffRoot = 5f, stiffTip = 1.8f, dampRoot = 4.8f, dampTip = 3f, massRoot = .03f, massTip = .02f,
                        gravRoot = .22f, gravTip = .60f, limitRoot = 30f, limitTip = 78f, parentRoot = .60f, parentTip = .08f,
                        velRoot = .10f, velTip = .32f, accRoot = .30f, accTip = .95f, angRoot = .30f, angTip = .90f,
                        radRoot = .010f, radTip = .015f, breeze = .50f, runSoftening = 1f, runLimitBoost = 1f };
                case ChainKind.Ahoge: // springy crown strand: stands up against gravity and bounces back
                    return new Profile { stiffRoot = 16f, stiffTip = 7f, dampRoot = 3.2f, dampTip = 2.1f, massRoot = .02f, massTip = .015f,
                        gravRoot = .02f, gravTip = .07f, limitRoot = 12f, limitTip = 42f, parentRoot = .90f, parentTip = .30f,
                        velRoot = .03f, velTip = .12f, accRoot = .30f, accTip = 1.0f, angRoot = .30f, angTip = .90f,
                        radRoot = .005f, radTip = .007f, breeze = .25f, runSoftening = 1f, runLimitBoost = 1f };
                case ChainKind.HatStrap:
                    return new Profile { stiffRoot = 6f, stiffTip = 2.2f, dampRoot = 5f, dampTip = 3.2f, massRoot = .028f, massTip = .02f,
                        gravRoot = .20f, gravTip = .50f, limitRoot = 28f, limitTip = 72f, parentRoot = .65f, parentTip = .10f,
                        velRoot = .09f, velTip = .30f, accRoot = .28f, accTip = .90f, angRoot = .28f, angTip = .85f,
                        radRoot = .009f, radTip = .014f, breeze = .40f, runSoftening = 1f, runLimitBoost = 1f };
                case ChainKind.Belt:
                    return new Profile { stiffRoot = 8f, stiffTip = 3f, dampRoot = 5f, dampTip = 3.6f, massRoot = .03f, massTip = .02f,
                        gravRoot = .16f, gravTip = .36f, limitRoot = 26f, limitTip = 64f, parentRoot = .70f, parentTip = .10f,
                        velRoot = .08f, velTip = .28f, accRoot = .25f, accTip = .80f, angRoot = .25f, angTip = .75f,
                        radRoot = .009f, radTip = .013f, breeze = .15f,
                        flutterRoot = .02f, flutterTip = .30f, runSoftening = .70f, runLimitBoost = 1.2f };
                default: // Hem: heavier cloth, no idle gusts; loosens, lifts and flaps while running
                    return new Profile { stiffRoot = 11f, stiffTip = 3.6f, dampRoot = 5.6f, dampTip = 3.8f, massRoot = .04f, massTip = .022f,
                        gravRoot = .14f, gravTip = .32f, limitRoot = 22f, limitTip = 64f, parentRoot = .72f, parentTip = .10f,
                        velRoot = .08f, velTip = .34f, accRoot = .22f, accTip = .85f, angRoot = .22f, angTip = .70f,
                        radRoot = .010f, radTip = .014f, breeze = 0f,
                        flutterRoot = .05f, flutterTip = .60f, runSoftening = .50f, runLimitBoost = 1.40f };
            }
        }

        // Deterministic per-chain variation so mirrored strands never move identically.
        static float Jitter(string chain, int salt)
        {
            int h = 17;
            for (int i = 0; i < chain.Length; i++) h = unchecked(h * 31 + chain[i]);
            var random = new System.Random(unchecked(h ^ (salt * 7919)));
            return .88f + .24f * (float)random.NextDouble();
        }

        /// <summary>Authoring only. Existing named settings survive rebuilding this list.</summary>
        [ContextMenu("Configure All Secondary Chains")]
        public void ConfigureRig()
        {
            var existing = new Dictionary<Transform, BoneSettings>();
            foreach (var setting in bones)
                if (setting != null && setting.bone) existing[setting.bone] = setting;

            var chainBones = new List<Transform>();
            foreach (var bone in GetComponentsInChildren<Transform>(true))
                if (Classify(bone.name) != ChainKind.None) chainBones.Add(bone);
            chainBones.Sort((a, b) => HierarchyDepth(a).CompareTo(HierarchyDepth(b)));

            var configured = new List<BoneSettings>(chainBones.Count);
            foreach (var bone in chainBones)
            {
                if (existing.TryGetValue(bone, out var saved))
                {
                    configured.Add(saved);
                    continue;
                }
                var kind = Classify(bone.name);
                Transform root = bone;
                int depth = 0;
                while (root.parent && Classify(root.parent.name) != ChainKind.None)
                {
                    root = root.parent;
                    depth++;
                }
                int chainLength = LongestDepth(root);
                float free = chainLength > 0 ? Mathf.Clamp01((float)depth / chainLength) : 1f;
                var profile = ProfileFor(kind);
                float jitterStiff = Jitter(root.name, 1);
                float jitterGrav = Jitter(root.name, 2);
                Transform child = FirstChainChild(bone);
                float fallbackLength = bone.parent ? bone.localPosition.magnitude : .035f;
                // Terminal bones continue the incoming segment direction (parent -> bone) in their own frame.
                Vector3 incoming = Quaternion.Inverse(bone.localRotation) * bone.localPosition;
                Vector3 tip = child ? child.localPosition
                    : incoming.sqrMagnitude > .0000001f ? incoming * .8f : Vector3.up * Mathf.Max(.008f, fallbackLength * .8f);
                configured.Add(new BoneSettings
                {
                    bone = bone, chain = root.name, kind = kind, depth = depth, chainLength = chainLength,
                    restLocalPosition = bone.localPosition, restLocalRotation = bone.localRotation, tipLocal = tip,
                    stiffness = Mathf.Lerp(profile.stiffRoot, profile.stiffTip, free) * jitterStiff,
                    damping = Mathf.Lerp(profile.dampRoot, profile.dampTip, free),
                    mass = Mathf.Lerp(profile.massRoot, profile.massTip, free),
                    gravityInfluence = Mathf.Lerp(profile.gravRoot, profile.gravTip, free) * jitterGrav,
                    rotationLimit = Mathf.Lerp(profile.limitRoot, profile.limitTip, free),
                    parentMotionInfluence = Mathf.Lerp(profile.parentRoot, profile.parentTip, free),
                    velocityResponse = Mathf.Lerp(profile.velRoot, profile.velTip, free),
                    accelerationResponse = Mathf.Lerp(profile.accRoot, profile.accTip, free),
                    angularVelocityResponse = Mathf.Lerp(profile.angRoot, profile.angTip, free),
                    collisionRadius = Mathf.Lerp(profile.radRoot, profile.radTip, free),
                    breeze = profile.breeze * Mathf.SmoothStep(0f, 1f, free),
                    flutter = Mathf.Lerp(profile.flutterRoot, profile.flutterTip, free * free),
                    runSoftening = Mathf.Lerp(1f, profile.runSoftening, free),
                    runLimitBoost = Mathf.Lerp(1f, profile.runLimitBoost, free)
                });
            }
            bones = configured;
            CountWeightedVertices();
            ApplyThickness();
            initialized = false;
            needsReset = true;
        }

        // Thick hair sections bend less per joint (the bend is shared along the strand) and stay straighter
        // while running, so the bulge never folds like plastic.
        void ApplyThickness()
        {
            var smoother = GetComponent<HairChainSmoother>();
            if (!smoother) return;
            foreach (var setting in bones)
            {
                if (setting.kind != ChainKind.HairBack || !smoother.TryGetThickness(setting.bone, out var ratio)) continue;
                setting.thickness = ratio;
                float thick = HairChainSmoother.Thickness01(ratio);
                if (thick <= 0f) continue;
                setting.bendLimit = Mathf.Lerp(18f + setting.depth * 1.1f, 9f, thick);
                setting.runBendScale = Mathf.Lerp(1f, .45f, thick);
                setting.stiffness *= 1f + .6f * thick;
            }
        }

        static int HierarchyDepth(Transform bone)
        {
            int depth = 0;
            for (var parent = bone.parent; parent; parent = parent.parent) depth++;
            return depth;
        }

        int LongestDepth(Transform bone)
        {
            int result = 0;
            for (int i = 0; i < bone.childCount; i++)
            {
                var child = bone.GetChild(i);
                if (Classify(child.name) != ChainKind.None) result = Mathf.Max(result, 1 + LongestDepth(child));
            }
            return result;
        }

        Transform FirstChainChild(Transform bone)
        {
            for (int i = 0; i < bone.childCount; i++)
            {
                var child = bone.GetChild(i);
                if (Classify(child.name) != ChainKind.None) return child;
            }
            return null;
        }

        void CountWeightedVertices()
        {
            var lookup = new Dictionary<Transform, BoneSettings>();
            foreach (var setting in bones)
            {
                setting.weightedVertices = 0;
                lookup[setting.bone] = setting;
            }
            foreach (var skin in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!skin.sharedMesh || !skin.sharedMesh.isReadable) continue;
                var skinBones = skin.bones;
                foreach (var weight in skin.sharedMesh.boneWeights)
                {
                    Count(weight.boneIndex0, weight.weight0);
                    Count(weight.boneIndex1, weight.weight1);
                    Count(weight.boneIndex2, weight.weight2);
                    Count(weight.boneIndex3, weight.weight3);
                }
                void Count(int index, float weight)
                {
                    if (weight > 0f && index >= 0 && index < skinBones.Length
                        && skinBones[index] && lookup.TryGetValue(skinBones[index], out var setting))
                        setting.weightedVertices++;
                }
            }
        }

        // ------------------------------------------------------------------ initialisation

        public void InitializeRig()
        {
            if (bones.Count == 0) ConfigureRig();
            if (bones.Count == 0) return;
            foreach (var setting in bones)
                if (setting == null || !setting.bone) return;

            motor = GetComponentInParent<ThirdPersonMotor>();
            actor = motor ? motor.transform : transform;
            var lookup = new Dictionary<Transform, int>();
            for (int i = 0; i < bones.Count; i++) lookup[bones[i].bone] = i;
            parents = new int[bones.Count];
            tails = new int[bones.Count];
            for (int i = 0; i < bones.Count; i++)
            {
                parents[i] = bones[i].bone.parent && lookup.TryGetValue(bones[i].bone.parent, out var parent) ? parent : -1;
                var child = FirstChainChild(bones[i].bone);
                tails[i] = child && lookup.TryGetValue(child, out var tail) ? tail : -1;
            }
            // Skin-weighted terminal bones get a virtual tail particle so they bend too (as in SilkClothDynamics).
            int terminals = 0;
            for (int i = 0; i < bones.Count; i++)
                if (tails[i] < 0 && bones[i].weightedVertices > 0) tails[i] = bones.Count + terminals++;
            particles = new Particle[bones.Count + terminals];
            currentTargets = new Vector3[particles.Length];
            previousTargets = new Vector3[particles.Length];
            stepTargets = new Vector3[particles.Length];
            lastStepTargets = new Vector3[particles.Length];
            rendered = new Vector3[particles.Length];
            twist = new float[bones.Count];
            twistVelocity = new float[bones.Count];
            chainPhase = new float[particles.Length];
            var chains = new Dictionary<string, int>();
            for (int i = 0; i < bones.Count; i++)
            {
                var setting = bones[i];
                if (!chains.ContainsKey(setting.chain)) chains[setting.chain] = chains.Count;
                particles[i] = new Particle
                {
                    parent = parents[i], settings = parents[i] >= 0 ? parents[i] : i, chain = chains[setting.chain],
                    inverseMass = parents[i] >= 0 ? 1f / Mathf.Max(.005f, bones[parents[i]].mass) : 0f
                };
                chainPhase[i] = Jitter(setting.chain, 3) * 6.2831853f;
                if (tails[i] >= bones.Count)
                {
                    particles[tails[i]] = new Particle { parent = i, settings = i, chain = chains[setting.chain],
                        inverseMass = 1f / Mathf.Max(.005f, setting.mass) };
                    chainPhase[tails[i]] = chainPhase[i];
                }
            }
            CacheBodyCapsules();
            RestoreRestPose();
            CaptureRestTargets();
            CacheAttachmentContacts();
            for (int i = 0; i < particles.Length; i++)
                if (particles[i].parent >= 0)
                    particles[i].length = Vector3.Distance(currentTargets[i], currentTargets[particles[i].parent]);
            CacheLayerPairs();
            initialized = true;
            ResetSimulation();
        }

        // Coarse body proxy using the MMD bone names of this rig. Radii are metres at the fitted size.
        void CacheBodyCapsules()
        {
            var lookup = new Dictionary<string, Transform>();
            foreach (var bone in GetComponentsInChildren<Transform>(true)) lookup[bone.name] = bone;
            var capsules = new List<BodyCapsule>();
            if (Naming == RigNaming.Furina)
            {
                lookup.TryGetValue("Hips", out hips);
                Add("Hips", "Chest", .115f);
                Add("Chest", "Neck", .095f);
                Add("Neck", "Head", .05f);
                Add("Head", "DM_0_1", .08f);
                foreach (var side in new[] { "Left ", "Right " })
                {
                    Add(side + "shoulder", side + "arm", .05f);
                    Add(side + "arm", side + "elbow", .045f);
                    Add(side + "elbow", side + "wrist", .038f);
                    Add(side + "leg", side + "knee", .082f);
                    Add(side + "knee", side + "ankle", .055f);
                }
                bodyCapsules = capsules.ToArray();
                return;
            }
            lookup.TryGetValue("腰", out hips);
            Add("下半身", "上半身", .12f);
            Add("上半身", "上半身2", .115f);
            Add("上半身2", "首", .105f);
            Add("首", "頭", .05f);
            Add("頭", "両目", .105f);
            foreach (var side in new[] { "左", "右" })
            {
                Add(side + "足", side + "ひざ", .082f);
                Add(side + "ひざ", side + "足首", .055f);
                Add(side + "足首", side + "つま先", .045f);
                Add(side + "腕", side + "ひじ", .05f);
                Add(side + "ひじ", side + "手首", .042f);
            }
            bodyCapsules = capsules.ToArray();
            void Add(string a, string b, float radius)
            {
                if (lookup.TryGetValue(a, out var start) && lookup.TryGetValue(b, out var end))
                    capsules.Add(new BodyCapsule { a = start, b = end, radius = radius });
            }
        }

        void CacheLayerPairs()
        {
            var pairs = new List<LayerPair>();
            for (int i = 0; i < particles.Length; i++)
            {
                if (particles[i].parent < 0) continue;
                for (int j = i + 1; j < particles.Length; j++)
                {
                    if (particles[j].parent < 0 || particles[i].chain == particles[j].chain) continue;
                    float distance = Vector3.Distance(currentTargets[i], currentTargets[j]);
                    float radius = bones[particles[i].settings].collisionRadius + bones[particles[j].settings].collisionRadius;
                    if (distance > Mathf.Max(radius * 3f, .055f)) continue;
                    pairs.Add(new LayerPair
                    {
                        a = i, b = j, separation = Mathf.Min(distance * .75f, radius),
                        fallback = distance > .00001f ? (currentTargets[i] - currentTargets[j]) / distance : Vector3.right
                    });
                }
            }
            layerPairs = pairs.ToArray();
        }

        // The coarse capsules overlap the hair roots and sewn belt/hem attachments in the bind pose.
        // Those audited initial contacts are excluded so the rest silhouette is never distorted;
        // every other contact stays active.
        void CacheAttachmentContacts()
        {
            attachmentContacts = new bool[particles.Length * Mathf.Max(1, bodyCapsules.Length)];
            for (int i = 0; i < particles.Length; i++)
            {
                if (particles[i].parent < 0) continue;
                float radius = bones[particles[i].settings].collisionRadius;
                for (int c = 0; c < bodyCapsules.Length; c++)
                {
                    var capsule = bodyCapsules[c];
                    float distance = Vector3.Distance(currentTargets[i], ClosestPoint(currentTargets[i], capsule.a.position, capsule.b.position));
                    if (distance < capsule.radius * bodyCollisionScale + radius)
                        attachmentContacts[i * bodyCapsules.Length + c] = true;
                }
            }
        }

        void RestoreRestPose()
        {
            foreach (var setting in bones)
            {
                if (setting == null || !setting.bone) continue;
                setting.bone.localPosition = setting.restLocalPosition;
                setting.bone.localRotation = setting.restLocalRotation;
            }
        }

        void CaptureRestTargets()
        {
            for (int i = 0; i < bones.Count; i++)
            {
                currentTargets[i] = bones[i].bone.position;
                if (tails[i] >= bones.Count) currentTargets[tails[i]] = bones[i].bone.TransformPoint(bones[i].tipLocal);
            }
        }

        public void ResetSimulation()
        {
            if (!initialized) { needsReset = true; return; }
            RestoreRestPose();
            CaptureRestTargets();
            for (int i = 0; i < particles.Length; i++)
            {
                particles[i].position = particles[i].previousPosition = particles[i].renderPrevious = currentTargets[i];
                previousTargets[i] = lastStepTargets[i] = stepTargets[i] = rendered[i] = currentTargets[i];
            }
            Array.Clear(twist, 0, twist.Length);
            Array.Clear(twistVelocity, 0, twistVelocity.Length);
            previousActorPosition = actor.position;
            previousDriverPosition = hips ? hips.position : actor.position;
            previousActorRotation = ActorRotation;
            actorVelocity = actorAcceleration = angularVelocity = Vector3.zero;
            angularAcceleration = Vector3.zero;
            runBlend = 0f;
            accumulator = 0f;
            for (int i = 0; i < bodyCapsules.Length; i++)
            {
                bodyCapsules[i].start = bodyCapsules[i].previousStart = bodyCapsules[i].targetStart = bodyCapsules[i].a.position;
                bodyCapsules[i].end = bodyCapsules[i].previousEnd = bodyCapsules[i].targetEnd = bodyCapsules[i].b.position;
            }
            needsReset = false;
            ResetCount++;
        }

        Quaternion ActorRotation => hips ? hips.rotation
            : motor && motor.visualRoot ? motor.visualRoot.rotation : actor.rotation;

        // ------------------------------------------------------------------ per-frame update

        /// <summary>Deterministic entry point. Call after the body pose and retarget have updated.</summary>
        public void StepSimulation(float deltaTime)
        {
            if (!initialized) InitializeRig();
            if (!initialized || deltaTime <= 0f) return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            RestoreRestPose();
            CaptureRestTargets();
            scale = Mathf.Max(.01f, Mathf.Abs(actor.lossyScale.y));
            bool teleported = Vector3.Distance(actor.position, previousActorPosition) > teleportDistance * scale;
            if (needsReset || teleported || deltaTime > .25f)
            {
                ResetSimulation();
                return;
            }

            Vector3 driverPosition = hips ? hips.position : actor.position;
            Vector3 velocity = (driverPosition - previousDriverPosition) / deltaTime;
            actorAcceleration = Vector3.ClampMagnitude((velocity - actorVelocity) / deltaTime, 60f * scale);
            actorVelocity = Vector3.ClampMagnitude(velocity, 20f * scale);
            Quaternion change = ActorRotation * Quaternion.Inverse(previousActorRotation);
            if (change.w < 0f) change = new Quaternion(-change.x, -change.y, -change.z, -change.w);
            change.ToAngleAxis(out float degrees, out Vector3 axis);
            Vector3 nextAngularVelocity = Finite(axis) ? Vector3.ClampMagnitude(axis * (degrees * Mathf.Deg2Rad / deltaTime), 12f) : Vector3.zero;
            angularAcceleration = Vector3.ClampMagnitude((nextAngularVelocity - angularVelocity) / deltaTime, 60f);
            angularVelocity = nextAngularVelocity;
            Vector3 planar = Vector3.ProjectOnPlane(actorVelocity, Vector3.up) / scale;
            float targetRun = Mathf.InverseLerp(runSpeedRange.x, runSpeedRange.y, planar.magnitude);
            // Rises quickly, falls slowly: the hem keeps flying for a moment after a sudden stop.
            runBlend = Mathf.Lerp(runBlend, targetRun, 1f - Mathf.Exp(-(targetRun > runBlend ? 6f : 2.2f) * deltaTime));
            previousActorPosition = actor.position;
            previousDriverPosition = driverPosition;
            previousActorRotation = ActorRotation;
            for (int i = 0; i < bodyCapsules.Length; i++)
            {
                bodyCapsules[i].targetStart = bodyCapsules[i].a.position;
                bodyCapsules[i].targetEnd = bodyCapsules[i].b.position;
            }

            accumulator += deltaTime;
            int maximum = Mathf.Max(1, maximumSubsteps);
            float permitted = maximum * FixedStep;
            if (accumulator > permitted)
            {
                DiscardedTime += accumulator - permitted;
                accumulator = permitted;
            }
            int steps = 0;
            while (accumulator + .000001f >= FixedStep && steps < maximum)
            {
                float fraction = Mathf.Clamp01((deltaTime - accumulator + FixedStep) / deltaTime);
                for (int i = 0; i < particles.Length; i++)
                    stepTargets[i] = Vector3.Lerp(previousTargets[i], currentTargets[i], fraction);
                for (int i = 0; i < bodyCapsules.Length; i++)
                {
                    bodyCapsules[i].start = Vector3.Lerp(bodyCapsules[i].previousStart, bodyCapsules[i].targetStart, fraction);
                    bodyCapsules[i].end = Vector3.Lerp(bodyCapsules[i].previousEnd, bodyCapsules[i].targetEnd, fraction);
                }
                SimulateStep();
                accumulator = Mathf.Max(0f, accumulator - FixedStep);
                steps++;
            }
            float alpha = Mathf.Clamp01(accumulator / FixedStep);
            for (int i = 0; i < particles.Length; i++)
            {
                rendered[i] = particles[i].parent < 0 ? currentTargets[i]
                    : Vector3.Lerp(particles[i].renderPrevious, particles[i].position, alpha);
                previousTargets[i] = currentTargets[i];
            }
            ApplyBoneRotations();
            for (int i = 0; i < bodyCapsules.Length; i++)
            {
                bodyCapsules[i].previousStart = bodyCapsules[i].targetStart;
                bodyCapsules[i].previousEnd = bodyCapsules[i].targetEnd;
            }
            LastStepMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d
                / System.Diagnostics.Stopwatch.Frequency;
        }

        void SimulateStep()
        {
            float water = motor && motor.IsSwimming ? 1f : 0f;
            Vector3 bodyOrigin = hips ? hips.position : actor.position;
            Vector3 relativeVelocity = actorVelocity / scale;
            float time = Time.time;
            Vector3 planarVelocity = Vector3.ProjectOnPlane(relativeVelocity, Vector3.up);
            float planarSpeed = planarVelocity.magnitude;
            Vector3 flapSide = planarSpeed > .01f ? Vector3.Cross(Vector3.up, planarVelocity / planarSpeed) : Vector3.zero;
            for (int i = 0; i < particles.Length; i++)
            {
                ref Particle particle = ref particles[i];
                particle.renderPrevious = particle.position;
                if (particle.parent < 0)
                {
                    particle.previousPosition = particle.position = stepTargets[i];
                    continue;
                }
                var setting = bones[particle.settings];
                Vector3 transport = (stepTargets[i] - lastStepTargets[i]) * setting.parentMotionInfluence;
                particle.position += transport;
                particle.previousPosition += transport;
                Vector3 inertia = (particle.position - particle.previousPosition)
                    * Mathf.Exp(-setting.damping * Mathf.Lerp(1f, 2.5f, water) * FixedStep);
                Vector3 wind = -relativeVelocity * relativeVelocity.magnitude * setting.velocityResponse * aerodynamicScale;
                Vector3 offset = stepTargets[i] - bodyOrigin;
                Vector3 turn = (-Vector3.Cross(angularAcceleration, offset)
                    - Vector3.Cross(angularVelocity, Vector3.Cross(angularVelocity, offset))) * setting.angularVelocityResponse;
                Vector3 reaction = -actorAcceleration * setting.accelerationResponse;
                float inverseRelativeMass = .025f / Mathf.Max(.005f, setting.mass);
                Vector3 acceleration = Physics.gravity * setting.gravityInfluence * (1f - water)
                    + (wind * scale + turn + reaction) * inverseRelativeMass;
                if (water > 0f)
                    acceleration += Vector3.up * .7f * scale - relativeVelocity * .7f * scale;
                if (idleBreeze > 0f && setting.breeze > 0f)
                {
                    // Slow bounded noise, phase-shifted per chain: strands drift apart instead of in unison.
                    float phase = chainPhase[i];
                    float gx = Mathf.PerlinNoise(phase, time * .21f) - .5f;
                    float gz = Mathf.PerlinNoise(phase + 31.7f, time * .17f) - .5f;
                    acceleration += new Vector3(gx, 0f, gz) * (2f * idleBreeze * setting.breeze * scale);
                }
                if (setting.flutter > 0f && runFlutter > 0f && planarSpeed > .5f)
                {
                    // Travelling wave from the waistband to the free edge (flag-like flapping), modulated
                    // by noise and per-chain phase, plus aerodynamic lift. Scales with speed squared.
                    float depth = setting.depth + 1;
                    float omega = 6f + planarSpeed * 2.2f;
                    float wave = Mathf.Sin(time * omega - depth * .85f + chainPhase[i]);
                    float gust = .6f + .8f * Mathf.PerlinNoise(chainPhase[i] + depth * .31f, time * 1.7f);
                    float lateral = Mathf.Sin(time * omega * .63f - depth * .6f + chainPhase[i] * 1.7f);
                    float strength = setting.flutter * runFlutter * planarSpeed * planarSpeed * runBlend;
                    acceleration += (Vector3.up * (wave * gust + .45f) + flapSide * (lateral * .55f)) * (strength * scale);
                }
                particle.previousPosition = particle.position;
                particle.position += inertia + Vector3.ClampMagnitude(acceleration, 60f * scale) * (FixedStep * FixedStep);
                if (!Finite(particle.position) || Vector3.Distance(particle.position, stepTargets[i]) > 3f * scale)
                    particle.position = particle.previousPosition = stepTargets[i];
            }
            int iterations = Mathf.Max(2, constraintIterations);
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                SolveAngularConstraints(iterations);
                SolveLengths();
                SolveBodyCollisions();
                SolveLayerCollisions();
                // A reverse pass prevents the free tip's correction accumulating at the root.
                SolveLengths(true);
            }
            UpdateTwist();
            if (measureErrors) MeasureErrors();
            for (int i = 0; i < particles.Length; i++) lastStepTargets[i] = stepTargets[i];
            SimulatedSteps++;
        }

        void SolveAngularConstraints(int iterations)
        {
            for (int i = 0; i < particles.Length; i++)
            {
                ref Particle particle = ref particles[i];
                if (particle.parent < 0 || particle.length < .00001f) continue;
                ref Particle parent = ref particles[particle.parent];
                var setting = bones[particle.settings];
                Vector3 baseline = stepTargets[i] - stepTargets[particle.parent];
                Vector3 current = particle.position - parent.position;
                if (baseline.sqrMagnitude < .0000001f || current.sqrMagnitude < .0000001f) continue;
                float stiffness = setting.stiffness * Mathf.Lerp(1f, setting.runSoftening, runBlend);
                float limit = Mathf.Min(85f, setting.rotationLimit * Mathf.Lerp(1f, setting.runLimitBoost, runBlend));
                float spring = 1f - Mathf.Exp(-stiffness * FixedStep / iterations);
                Vector3 desired = Vector3.Slerp(current.normalized, baseline.normalized, spring);
                desired = Vector3.RotateTowards(baseline.normalized, desired, limit * Mathf.Deg2Rad, 0f).normalized;
                if (parent.parent >= 0)
                {
                    Vector3 restPrevious = stepTargets[particle.parent] - stepTargets[parent.parent];
                    Vector3 posedPrevious = parent.position - particles[parent.parent].position;
                    if (restPrevious.sqrMagnitude > .0000001f && posedPrevious.sqrMagnitude > .0000001f)
                    {
                        Vector3 transported = Quaternion.FromToRotation(restPrevious, baseline) * posedPrevious.normalized;
                        // Keep the strand's intrinsic curl relative to the moving parent so a whole
                        // lock can trail without being ironed straight.
                        float curvatureRate = Mathf.Lerp(3f, 24f, curvatureRetention);
                        float curvatureSpring = 1f - Mathf.Exp(-stiffness * curvatureRate * FixedStep / iterations);
                        desired = Vector3.Slerp(desired, transported.normalized, curvatureSpring).normalized;
                        float bendBase = 18f + setting.depth * 1.1f;
                        if (setting.bendLimit > 0f) bendBase = Mathf.Min(bendBase, setting.bendLimit);
                        float bendLimit = Mathf.Min(limit, bendBase * Mathf.Lerp(1f, setting.runLimitBoost * setting.runBendScale, runBlend));
                        desired = Vector3.RotateTowards(transported.normalized, desired,
                            bendLimit * Mathf.Deg2Rad, 0f).normalized;
                    }
                }
                Vector3 correction = parent.position + desired * particle.length - particle.position;
                float sum = particle.inverseMass + parent.inverseMass;
                if (sum <= 0f) continue;
                particle.position += correction * (particle.inverseMass / sum);
                parent.position -= correction * (parent.inverseMass / sum);
            }
        }

        void SolveLengths(bool reverse = false)
        {
            for (int step = 0; step < particles.Length; step++)
            {
                int i = reverse ? particles.Length - step - 1 : step;
                ref Particle particle = ref particles[i];
                if (particle.parent < 0) continue;
                ref Particle parent = ref particles[particle.parent];
                Vector3 difference = particle.position - parent.position;
                float distance = difference.magnitude;
                float sum = particle.inverseMass + parent.inverseMass;
                if (distance < .000001f || sum <= 0f) continue;
                Vector3 correction = difference * ((distance - particle.length) / (distance * sum));
                particle.position -= correction * particle.inverseMass;
                parent.position += correction * parent.inverseMass;
            }
        }

        void SolveBodyCollisions()
        {
            for (int i = 0; i < particles.Length; i++)
            {
                ref Particle particle = ref particles[i];
                if (particle.parent < 0) continue;
                float radius = bones[particle.settings].collisionRadius * scale;
                for (int c = 0; c < bodyCapsules.Length; c++)
                {
                    if (attachmentContacts[i * bodyCapsules.Length + c]) continue;
                    var capsule = bodyCapsules[c];
                    Vector3 center = ClosestPoint(particle.position, capsule.start, capsule.end);
                    Vector3 difference = particle.position - center;
                    float required = capsule.radius * scale * bodyCollisionScale + radius;
                    float distance = difference.magnitude;
                    if (distance >= required) continue;
                    Vector3 direction = distance > .00001f ? difference / distance : actor.rotation * Vector3.back;
                    particle.position = center + direction * required;
                    // Remove the inward component, keeping tangential sliding and recoil.
                    Vector3 movement = particle.position - particle.previousPosition;
                    float inward = Vector3.Dot(movement, direction);
                    if (inward < 0f) particle.previousPosition += direction * inward;
                }
            }
        }

        void SolveLayerCollisions()
        {
            for (int i = 0; i < layerPairs.Length; i++)
            {
                var pair = layerPairs[i];
                ref Particle a = ref particles[pair.a];
                ref Particle b = ref particles[pair.b];
                Vector3 difference = a.position - b.position;
                float distance = difference.magnitude;
                float separation = pair.separation * layerCollisionScale;
                float sum = a.inverseMass + b.inverseMass;
                if (distance >= separation || sum <= 0f) continue;
                Vector3 direction = distance > .00001f ? difference / distance : ActorRotation * pair.fallback;
                Vector3 correction = direction * ((separation - distance) / sum);
                a.position += correction * a.inverseMass;
                b.position -= correction * b.inverseMass;
            }
        }

        void UpdateTwist()
        {
            for (int i = 0; i < bones.Count; i++)
            {
                if (tails[i] < 0) continue;
                var setting = bones[i];
                Vector3 direction = particles[tails[i]].position - particles[i].position;
                float target = direction.sqrMagnitude > .000001f
                    ? -Vector3.Dot(angularVelocity, direction.normalized) * setting.angularVelocityResponse * 4f : 0f;
                float omega = Mathf.Lerp(10f, 5f, Mathf.Clamp01(setting.depth / 18f));
                float acceleration = omega * omega * (target - twist[i]) - 1.5f * omega * twistVelocity[i];
                twistVelocity[i] += acceleration * FixedStep;
                twist[i] = Mathf.Clamp(twist[i] + twistVelocity[i] * FixedStep,
                    -setting.rotationLimit * .25f, setting.rotationLimit * .25f);
            }
        }

        void ApplyBoneRotations()
        {
            for (int i = 0; i < bones.Count; i++)
            {
                if (tails[i] < 0) continue;
                var setting = bones[i];
                Vector3 from = setting.bone.TransformVector(setting.tipLocal);
                Vector3 wanted = rendered[tails[i]] - setting.bone.position;
                if (from.sqrMagnitude < .0000001f || wanted.sqrMagnitude < .0000001f) continue;
                Quaternion rotation = Quaternion.FromToRotation(from, wanted) * setting.bone.rotation;
                rotation = Quaternion.AngleAxis(twist[i], wanted.normalized) * rotation;
                if (Finite(rotation)) setting.bone.rotation = rotation;
                if (trackUtilization)
                    setting.peakDeflection = Mathf.Max(setting.peakDeflection,
                        Quaternion.Angle(setting.bone.localRotation, setting.restLocalRotation));
            }
        }

        void MeasureErrors()
        {
            MaximumLengthError = MaximumBodyPenetration = 0f;
            for (int i = 0; i < particles.Length; i++)
            {
                var particle = particles[i];
                if (particle.parent < 0) continue;
                float error = Mathf.Abs(Vector3.Distance(particle.position, particles[particle.parent].position) - particle.length);
                MaximumLengthError = Mathf.Max(MaximumLengthError, error);
                float radius = bones[particle.settings].collisionRadius * scale;
                for (int c = 0; c < bodyCapsules.Length; c++)
                {
                    if (attachmentContacts[i * bodyCapsules.Length + c]) continue;
                    var capsule = bodyCapsules[c];
                    float distance = Vector3.Distance(particle.position, ClosestPoint(particle.position, capsule.start, capsule.end));
                    float penetration = capsule.radius * scale * bodyCollisionScale + radius - distance;
                    MaximumBodyPenetration = Mathf.Max(MaximumBodyPenetration, penetration);
                }
            }
        }

        // ------------------------------------------------------------------ reporting

        [ContextMenu("Log Joint Utilization")]
        public void LogUtilization() => Debug.Log(BuildUtilizationReport(), this);

        /// <summary>Measured at run time: bones whose local rotation has deviated from rest since the last reset.</summary>
        public string BuildUtilizationReport(float movedThresholdDegrees = 1f)
        {
            int total = bones.Count, weighted = 0, moved = 0, movedWeighted = 0;
            var perKind = new Dictionary<ChainKind, int[]>();
            foreach (var setting in bones)
            {
                if (!perKind.TryGetValue(setting.kind, out var counts)) perKind[setting.kind] = counts = new int[4];
                bool isWeighted = setting.weightedVertices > 0;
                bool isMoved = setting.peakDeflection >= movedThresholdDegrees;
                counts[0]++;
                if (isWeighted) { weighted++; counts[1]++; }
                if (isMoved) { moved++; counts[2]++; }
                if (isWeighted && isMoved) { movedWeighted++; counts[3]++; }
            }
            var text = new StringBuilder();
            text.AppendLine($"[HuTaoChainPhysics] simulated bones={total} weighted(readable meshes only)={weighted} " +
                $"moved>{movedThresholdDegrees:0.#}deg={moved} movedAndWeighted={movedWeighted} " +
                $"particles={particles.Length} layerPairs={layerPairs.Length} capsules={bodyCapsules.Length} " +
                $"steps={SimulatedSteps} lastStepMs={LastStepMilliseconds:0.000}");
            foreach (var pair in perKind)
                text.AppendLine($"  {pair.Key}: bones={pair.Value[0]} weighted={pair.Value[1]} moved={pair.Value[2]} movedAndWeighted={pair.Value[3]}");
            foreach (var setting in bones)
                if (setting.peakDeflection < movedThresholdDegrees && setting.bone)
                    text.AppendLine($"  unmoved: {setting.bone.name} peak={setting.peakDeflection:0.00}deg weighted={setting.weightedVertices}");
            return text.ToString();
        }

        public void ClearUtilization()
        {
            foreach (var setting in bones) setting.peakDeflection = 0f;
        }

        static Vector3 ClosestPoint(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 segment = b - a;
            float length = segment.sqrMagnitude;
            return length < .000001f ? a : a + segment * Mathf.Clamp01(Vector3.Dot(point - a, segment) / length);
        }

        static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x)
            && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
            && !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        static bool Finite(Quaternion value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x)
            && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
            && !float.IsNaN(value.z) && !float.IsInfinity(value.z)
            && !float.IsNaN(value.w) && !float.IsInfinity(value.w);
    }
}
