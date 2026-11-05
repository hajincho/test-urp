using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.ThirdPerson
{
    /// <summary>Fixed-step particle dynamics for the imported, weighted HB cloth chains.</summary>
    [DefaultExecutionOrder(198)]
    [DisallowMultipleComponent]
    public sealed class SilkClothDynamics : MonoBehaviour
    {
        [Serializable]
        public sealed class BoneSettings
        {
            public Transform bone;
            public string chain;
            public string role;
            public int depth;
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
        }

        [Tooltip("All weighted garment bones, including the weighted terminal bones.")]
        public List<BoneSettings> bones = new List<BoneSettings>();
        [Range(2, 16)] public int constraintIterations = 8;
        [Range(1, 12)] public int maximumSubsteps = 6;
        [Range(0f, 2f)] public float aerodynamicScale = 1f;
        [Range(0f, 2f)] public float bodyCollisionScale = 1f;
        [Range(0f, 2f)] public float layerCollisionScale = 1f;
        [Range(0f, 1f)] public float curvatureRetention = .65f;
        [Range(.1f, 5f)] public float teleportDistance = 1.5f;
        [Tooltip("Physical seconds, independent of rendering frame rate.")]
        public const float FixedStep = 1f / 60f;

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
        LayerPair[] layerPairs = Array.Empty<LayerPair>();
        BodyCapsule[] bodyCapsules = Array.Empty<BodyCapsule>();
        bool[] attachmentContacts = Array.Empty<bool>();
        ThirdPersonMotor motor;
        Transform actor, hips;
        Vector3 previousActorPosition, actorVelocity, actorAcceleration, angularVelocity;
        Vector3 previousDriverPosition, angularAcceleration;
        Quaternion previousActorRotation;
        float accumulator, scale = 1f;
        bool initialized, needsReset = true;

        public bool OwnsCloth => isActiveAndEnabled && initialized;
        public int ActiveBoneCount => initialized ? bones.Count : 0;
        public int ParticleCount => particles.Length;
        public int LayerConstraintCount => layerPairs.Length;
        public int AttachmentContactExclusions { get; private set; }
        public int ResetCount { get; private set; }
        public long SimulatedSteps { get; private set; }
        public float DiscardedTime { get; private set; }
        public float MaximumLengthError { get; private set; }
        public float MaximumBodyPenetration { get; private set; }
        public double LastStepMilliseconds { get; private set; }

        void Awake() => InitializeRig();
        void OnEnable() => needsReset = true;
        void OnDisable()
        {
            RestoreRestPose();
            needsReset = true;
        }

        void LateUpdate() => StepSimulation(Time.deltaTime);

        /// <summary>Authoring only. Existing named settings survive rebuilding this list.</summary>
        [ContextMenu("Configure All Weighted Cloth Bones")]
        public void ConfigureRig()
        {
            var existing = new Dictionary<Transform, BoneSettings>();
            foreach (var setting in bones)
                if (setting != null && setting.bone) existing[setting.bone] = setting;

            var all = GetComponentsInChildren<Transform>(true);
            var cloth = new List<Transform>();
            foreach (var bone in all)
                if (bone.name.StartsWith("HB", StringComparison.Ordinal)) cloth.Add(bone);
            cloth.Sort((a, b) => HierarchyDepth(a).CompareTo(HierarchyDepth(b)));

            var configured = new List<BoneSettings>(cloth.Count);
            foreach (var bone in cloth)
            {
                if (existing.TryGetValue(bone, out var saved))
                {
                    configured.Add(saved);
                    continue;
                }
                Transform root = bone;
                int depth = 0;
                while (root.parent && root.parent.name.StartsWith("HB", StringComparison.Ordinal))
                {
                    root = root.parent;
                    depth++;
                }
                int chainLength = LongestDepth(root);
                float free = chainLength > 0 ? (float)depth / chainLength : 0f;
                bool longLayer = root.name.StartsWith("HB2", StringComparison.Ordinal)
                    || root.name.StartsWith("HB3", StringComparison.Ordinal)
                    || root.name.StartsWith("HB4", StringComparison.Ordinal);
                float layer = root.name.StartsWith("HB2", StringComparison.Ordinal) ? 1.1f
                    : root.name.StartsWith("HB4", StringComparison.Ordinal) ? .91f : 1f;
                Transform child = FirstClothChild(bone);
                float fallbackLength = bone.parent ? bone.localPosition.magnitude : .035f;
                Vector3 tip = child ? child.localPosition : Vector3.up * Mathf.Max(.008f, fallbackLength * .8f);
                configured.Add(new BoneSettings {
                    bone = bone, chain = root.name, depth = depth,
                    role = depth == 0 ? "Pinned attachment; dynamic outgoing segment"
                        : child ? (longLayer ? "Flexible layered strip" : "Short skirt panel")
                        : "Weighted terminal with virtual tail particle",
                    restLocalPosition = bone.localPosition, restLocalRotation = bone.localRotation,
                    tipLocal = tip,
                    stiffness = Mathf.Lerp(longLayer ? 10f : 13f, longLayer ? 4.8f : 4f, free) * layer,
                    damping = Mathf.Lerp(6f, longLayer ? 5.8f : 4.2f, free),
                    mass = Mathf.Lerp(.04f, .022f, free) * layer,
                    gravityInfluence = Mathf.Lerp(.16f, longLayer ? .38f : .25f, free),
                    rotationLimit = Mathf.Lerp(24f, longLayer ? 58f : 42f, free),
                    parentMotionInfluence = Mathf.Lerp(.78f, longLayer ? .12f : .26f, free),
                    velocityResponse = Mathf.Lerp(.055f, longLayer ? .20f : .11f, free) / layer,
                    accelerationResponse = Mathf.Lerp(.18f, longLayer ? .65f : .36f, free),
                    angularVelocityResponse = Mathf.Lerp(.18f, longLayer ? .65f : .4f, free),
                    collisionRadius = Mathf.Lerp(.008f, longLayer ? .014f : .011f, free)
                });
            }
            bones = configured;
            CountWeightedVertices();
            initialized = false;
            needsReset = true;
        }

        static int HierarchyDepth(Transform bone)
        {
            int depth = 0;
            for (var parent = bone.parent; parent; parent = parent.parent) depth++;
            return depth;
        }

        static int LongestDepth(Transform bone)
        {
            int result = 0;
            for (int i = 0; i < bone.childCount; i++)
            {
                var child = bone.GetChild(i);
                if (child.name.StartsWith("HB", StringComparison.Ordinal))
                    result = Mathf.Max(result, 1 + LongestDepth(child));
            }
            return result;
        }

        static Transform FirstClothChild(Transform bone)
        {
            for (int i = 0; i < bone.childCount; i++)
            {
                var child = bone.GetChild(i);
                if (child.name.StartsWith("HB", StringComparison.Ordinal)) return child;
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
            int terminals = 0;
            for (int i = 0; i < bones.Count; i++)
            {
                parents[i] = bones[i].bone.parent && lookup.TryGetValue(bones[i].bone.parent, out var parent) ? parent : -1;
                var child = FirstClothChild(bones[i].bone);
                if (child && lookup.TryGetValue(child, out var tail)) tails[i] = tail;
                else tails[i] = bones.Count + terminals++;
            }
            particles = new Particle[bones.Count + terminals];
            currentTargets = new Vector3[particles.Length];
            previousTargets = new Vector3[particles.Length];
            stepTargets = new Vector3[particles.Length];
            lastStepTargets = new Vector3[particles.Length];
            rendered = new Vector3[particles.Length];
            twist = new float[bones.Count];
            twistVelocity = new float[bones.Count];
            var chains = new Dictionary<string, int>();
            for (int i = 0; i < bones.Count; i++)
            {
                var setting = bones[i];
                if (!chains.ContainsKey(setting.chain)) chains[setting.chain] = chains.Count;
                particles[i] = new Particle { parent = parents[i], settings = parents[i] >= 0 ? parents[i] : i,
                    chain = chains[setting.chain], inverseMass = parents[i] >= 0 ? 1f / Mathf.Max(.005f, bones[parents[i]].mass) : 0f };
                if (tails[i] >= bones.Count)
                    particles[tails[i]] = new Particle { parent = i, settings = i, chain = chains[setting.chain],
                        inverseMass = 1f / Mathf.Max(.005f, setting.mass) };
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

        void CacheBodyCapsules()
        {
            var lookup = new Dictionary<string, Transform>();
            foreach (var bone in GetComponentsInChildren<Transform>(true)) lookup[bone.name] = bone;
            lookup.TryGetValue("Hips", out hips);
            var capsules = new List<BodyCapsule>();
            Add("Hips", "Chest", .115f);
            Add("Left leg", "Left knee", .082f);
            Add("Left knee", "Left ankle", .055f);
            Add("Right leg", "Right knee", .082f);
            Add("Right knee", "Right ankle", .055f);
            Add("Left ankle", "Left toe", .045f);
            Add("Right ankle", "Right toe", .045f);
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
                    pairs.Add(new LayerPair { a = i, b = j,
                        separation = Mathf.Min(distance * .75f, radius),
                        fallback = distance > .00001f ? (currentTargets[i] - currentTargets[j]) / distance : Vector3.right });
                }
            }
            layerPairs = pairs.ToArray();
        }

        void CacheAttachmentContacts()
        {
            attachmentContacts = new bool[particles.Length * bodyCapsules.Length];
            AttachmentContactExclusions = 0;
            for (int i = 0; i < particles.Length; i++)
            {
                if (particles[i].parent < 0 || bones[particles[i].settings].depth > 2) continue;
                float radius = bones[particles[i].settings].collisionRadius;
                for (int c = 0; c < bodyCapsules.Length; c++)
                {
                    var capsule = bodyCapsules[c];
                    float distance = Vector3.Distance(currentTargets[i], ClosestPoint(currentTargets[i], capsule.a.position, capsule.b.position));
                    if (distance >= capsule.radius + radius) continue;
                    // Sewn attachments can lie inside the coarse body proxy in the bind pose.
                    // Preserve only those audited initial contacts, keeping all other contacts active.
                    attachmentContacts[i * bodyCapsules.Length + c] = true;
                    AttachmentContactExclusions++;
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

        /// <summary>Deterministic test entry point. Call after the body pose and retarget have updated.</summary>
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
                particle.previousPosition = particle.position;
                particle.position += inertia + Vector3.ClampMagnitude(acceleration, 45f * scale) * (FixedStep * FixedStep);
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
            MeasureErrors();
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
                float spring = 1f - Mathf.Exp(-setting.stiffness * FixedStep / iterations);
                Vector3 desired = Vector3.Slerp(current.normalized, baseline.normalized, spring);
                desired = Vector3.RotateTowards(baseline.normalized, desired,
                    setting.rotationLimit * Mathf.Deg2Rad, 0f).normalized;
                if (parent.parent >= 0)
                {
                    Vector3 restPrevious = stepTargets[particle.parent] - stepTargets[parent.parent];
                    Vector3 posedPrevious = parent.position - particles[parent.parent].position;
                    if (restPrevious.sqrMagnitude > .0000001f && posedPrevious.sqrMagnitude > .0000001f)
                    {
                        Vector3 transported = Quaternion.FromToRotation(restPrevious, baseline) * posedPrevious.normalized;
                        // Retain the garment's intrinsic folds relative to the moving parent.
                        // This permits an entire ribbon to trail without ironing out its curvature.
                        float curvatureRate = Mathf.Lerp(3f, 24f, curvatureRetention);
                        float curvatureSpring = 1f - Mathf.Exp(-setting.stiffness * curvatureRate * FixedStep / iterations);
                        desired = Vector3.Slerp(desired, transported.normalized, curvatureSpring).normalized;
                        float bendLimit = Mathf.Min(setting.rotationLimit, 18f + setting.depth * 1.1f);
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
                    // Remove the inward component, retaining tangential sliding and recoil.
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
                var setting = bones[i];
                Vector3 from = setting.bone.TransformVector(setting.tipLocal);
                Vector3 wanted = rendered[tails[i]] - setting.bone.position;
                if (from.sqrMagnitude < .0000001f || wanted.sqrMagnitude < .0000001f) continue;
                Quaternion rotation = Quaternion.FromToRotation(from, wanted) * setting.bone.rotation;
                rotation = Quaternion.AngleAxis(twist[i], wanted.normalized) * rotation;
                if (Finite(rotation)) setting.bone.rotation = rotation;
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

        [Serializable]
        sealed class ConfigurationReport
        {
            public int bones, particles, layerPairs, fixedHz;
            public BoneSettings[] settings;
        }

        public string ExportConfigurationJson() => JsonUtility.ToJson(new ConfigurationReport {
            bones = bones.Count, particles = particles.Length, layerPairs = layerPairs.Length,
            fixedHz = 60, settings = bones.ToArray()
        }, true);
    }
}
