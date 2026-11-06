using System.Collections.Generic;
using UnityEngine;

namespace Game.ThirdPerson
{
    /// <summary>
    /// Drives the Hu Tao (MMD-named) skeleton from Furina's FINAL pose (after BlenderCharacterRetarget has
    /// applied locomotion, leg fitting and arm lowering), so Hu Tao's idle stance, arm pose and gait match Furina's.
    /// Furina stays active but hidden while Hu Tao is shown (see CharacterSwitcher).
    /// It uses rest-relative world-rotation links, local-delta finger links and an analytic two-bone leg fit,
    /// mapped onto this rig's bones:
    /// 腰 / 上半身 chain, 肩-腕-ひじ-手首 arms, 足-ひざ-足首 legs and the D (deform) leg bones that carry the skin weights.
    /// All hair, hat, belt and hem chains are left untouched here; <see cref="HuTaoChainPhysics"/> owns them.
    ///
    /// Size: Hu Tao's hat-less height is matched to Furina's hat-less height. Both ratios below were measured
    /// from the bind-pose geometry of the two FBX files (top of the head/hair excluding hat meshes or hat-bone
    /// weighted vertices, divided by hip-knee-ankle leg length), so the runtime only needs leg lengths.
    /// Furina's pelvis translation is scaled by the resulting leg ratio so strides and pelvis bob fit.
    /// </summary>
    [DefaultExecutionOrder(197)] // after BlenderCharacterRetarget (195)
    public sealed class HuTaoRetarget : MonoBehaviour
    {
        /// <summary>Furina: hat-less height 1.5634 / leg 0.8062 (BlenderAlternate.fbx).</summary>
        public const float FurinaHatlessHeightPerLeg = 1.93935f;
        /// <summary>Hu Tao: hat-less height 18.921 / leg 9.915 (Hu tao.fbx, meshes 帽 and 帽_饰 excluded).</summary>
        public const float HuTaoHatlessHeightPerLeg = 1.90838f;

        struct Link { public Transform from, to; public Quaternion offset; }
        struct Detail
        {
            public Transform from, to;
            public Quaternion fromRest, toRest, basis;
            public float share;
        }
        struct Follow { public Transform leader, follower; public Quaternion offset; }
        struct Leg
        {
            public Transform hip, knee, foot, sourceHip, sourceKnee, sourceFoot;
            public Vector3 restCorrection; // root-local, keeps Hu Tao's own stance width
            public float side;             // -1 left, +1 right (root.right)
        }

        readonly List<Link> pose = new List<Link>();
        readonly List<Detail> details = new List<Detail>();
        readonly List<Follow> follows = new List<Follow>();
        readonly Dictionary<string, Transform> source = new Dictionary<string, Transform>();
        readonly Dictionary<string, Transform> target = new Dictionary<string, Transform>();
        readonly List<string> missing = new List<string>();
        Transform root, pelvis, sourcePelvis, spineLow, spineMid, spineHigh;
        Quaternion spineMidOffset = Quaternion.identity;
        int spineMidAfter = -1;
        Vector3 hipOffset;
        Leg left, right;
        ThirdPersonMotor motor;
        float gaitBlend;

        [Tooltip("Extra outward foot offset per leg while walking/running (m). Idle stance is unchanged.")]
        [Range(0f, .08f)] public float gaitWidening = .03f;

        public bool Ready { get; private set; }
        public int LinkedBones => pose.Count + details.Count + follows.Count;
        /// <summary>Hu Tao leg length / Furina leg length.</summary>
        public float MotionScale { get; private set; } = 1f;
        /// <summary>Estimated hat-less standing height in world units after fitting.</summary>
        public float HatlessHeight { get; private set; }
        /// <summary>Furina's hat-less height in world units (0 if Furina was not found).</summary>
        public float FurinaHatlessHeight { get; private set; }
        public UntitledCharacterMotion MotionSource { get; private set; }

        void Awake()
        {
            motor = GetComponentInParent<ThirdPersonMotor>();
            var original = motor ? motor.GetComponentInChildren<UntitledCharacterMotion>(true) : null;
            if (!original || !motor.visualRoot)
            {
                Debug.LogWarning("[HuTaoRetarget] No motion source / visualRoot found; Hu Tao will not animate.");
                enabled = false;
                return;
            }
            MotionSource = original;
            root = motor.visualRoot;
            var furina = motor.GetComponentInChildren<BlenderCharacterRetarget>(true);
            if (!furina)
            {
                Debug.LogWarning("[HuTaoRetarget] Furina (BlenderCharacterRetarget) not found; Hu Tao will not animate.");
                enabled = false;
                return;
            }
            foreach (var t in furina.GetComponentsInChildren<Transform>(true)) source[t.name] = t;
            foreach (var t in GetComponentsInChildren<Transform>(true)) target[t.name] = t;
            if (!source.TryGetValue("Hips", out sourcePelvis) || !target.TryGetValue("腰", out pelvis))
            {
                Debug.LogWarning("[HuTaoRetarget] Pelvis bones missing (Furina Hips / Hu Tao 腰).");
                enabled = false;
                return;
            }

            Fit();
            hipOffset = root.InverseTransformVector(pelvis.position - Scaled(sourcePelvis.position));

            Bind("Hips", "腰");
            Bind("Spine", "上半身", "Chest", "上半身2");
            spineMidAfter = pose.Count - 1;
            if (target.TryGetValue("上半身", out spineLow) && target.TryGetValue("上半身3", out spineMid)
                && target.TryGetValue("上半身2", out spineHigh))
                spineMidOffset = Quaternion.Inverse(Quaternion.Slerp(spineLow.rotation, spineHigh.rotation, .5f)) * spineMid.rotation;
            else spineMid = null;
            Bind("Chest", "上半身2", "Neck", "首");
            Bind("Neck", "首", "Head", "頭");
            Bind("Head", "頭");
            left = BindSide(true);
            right = BindSide(false);
            BalanceLegCorrection();

            if (missing.Count > 0)
                Debug.LogWarning("[HuTaoRetarget] Unmapped bones: " + string.Join(", ", missing));
            Ready = pose.Count >= 10 && left.hip && right.hip && left.sourceHip && right.sourceHip;
            if (!Ready) enabled = false;
            else Debug.Log($"[HuTaoRetarget] fitted: hatless height={HatlessHeight:0.000} (Furina hatless={FurinaHatlessHeight:0.000}) motionScale={MotionScale:0.000}");
        }

        /// <summary>Motion-skeleton position, scaled about the visual root by the leg ratio.</summary>
        Vector3 Scaled(Vector3 world) => root.TransformPoint(root.InverseTransformPoint(world) * MotionScale);

        static float LegLength(Transform hip, Transform knee, Transform ankle) =>
            Vector3.Distance(hip.position, knee.position) + Vector3.Distance(knee.position, ankle.position);

        // Match orientation, size and position of this rig to Furina before any link is built.
        void Fit()
        {
            if (!target.TryGetValue("左足", out var lf) || !target.TryGetValue("右足", out var rf) ||
                !target.TryGetValue("頭", out var head) || !target.TryGetValue("左ひざ", out var lk) ||
                !target.TryGetValue("左足首", out var la) || !target.TryGetValue("右足首", out var ra)) return;

            // Orientation: character-right is 左足 -> 右足, character-up is hips -> head, snapped to the model's
            // nearest principal axis. The MMD head joint sits ~3.5 deg behind the hips, so using it unsnapped
            // pitched the whole rig forward: hips and head leaned and both feet stayed toe-down while walking.
            Vector3 mid = (lf.position + rf.position) * .5f;
            Vector3 up = SnapToAxis(root.InverseTransformDirection(head.position - mid));
            Vector3 side = Vector3.ProjectOnPlane(root.InverseTransformDirection(rf.position - lf.position), up);
            if (up.sqrMagnitude > .5f && side.sqrMagnitude > .000001f)
            {
                Vector3 forward = Vector3.Cross(side.normalized, up).normalized;
                var frame = Quaternion.LookRotation(forward, up);
                transform.rotation = root.rotation * Quaternion.Inverse(frame)
                    * Quaternion.Inverse(root.rotation) * transform.rotation;
            }

            // Size: Hu Tao hat-less height = Furina hat-less height.
            if (!source.TryGetValue("Left leg", out var fh) || !source.TryGetValue("Left knee", out var fk)
                || !source.TryGetValue("Left ankle", out var fa)) return;
            float sourceLeg = LegLength(fh, fk, fa);
            FurinaHatlessHeight = sourceLeg * FurinaHatlessHeightPerLeg;
            float desiredLeg = FurinaHatlessHeight / HuTaoHatlessHeightPerLeg;
            float targetLeg = LegLength(lf, lk, la);
            if (desiredLeg > .0001f && targetLeg > .0001f) transform.localScale *= desiredLeg / targetLeg;
            float fittedLeg = LegLength(lf, lk, la);
            HatlessHeight = fittedLeg * HuTaoHatlessHeightPerLeg;
            MotionScale = sourceLeg > .0001f ? fittedLeg / sourceLeg : 1f;

            // Position: ankles centred on Furina's (scaled) ankles.
            if (source.TryGetValue("Right ankle", out var fr))
                transform.position += Scaled((fa.position + fr.position) * .5f) - (la.position + ra.position) * .5f;
        }

        static Vector3 SnapToAxis(Vector3 v)
        {
            float x = Mathf.Abs(v.x), y = Mathf.Abs(v.y), z = Mathf.Abs(v.z);
            if (x >= y && x >= z) return new Vector3(Mathf.Sign(v.x), 0f, 0f);
            if (y >= z) return new Vector3(0f, Mathf.Sign(v.y), 0f);
            return new Vector3(0f, 0f, Mathf.Sign(v.z));
        }

        // The rest correction was a constant root-space vector added to a swinging hip->ankle reach. Its
        // vertical/forward part (Hu Tao's straight MMD rest leg vs Furina's softly bent idle leg) lengthened the
        // trailing leg and shortened the leading one, so in side view the knees locked, the back foot was
        // dragged and the stride was lopsided. That shared part now moves the pelvis instead (same idle foot
        // placement, Furina's knee bend); only each leg's lateral stance difference stays on the foot goal.
        void BalanceLegCorrection()
        {
            if (!left.hip || !right.hip) return;
            Vector3 shared = (left.restCorrection + right.restCorrection) * .5f;
            shared.x = 0f;
            left.restCorrection -= shared;
            right.restCorrection -= shared;
            hipOffset += shared;
        }

        Leg BindSide(bool isLeft)
        {
            string side = isLeft ? "左" : "右", s = isLeft ? "_L" : "_R", f = isLeft ? "Left " : "Right ";
            Bind(f + "shoulder", side + "肩", f + "arm", side + "腕");
            Bind(f + "arm", side + "腕", f + "elbow", side + "ひじ");
            Bind(f + "elbow", side + "ひじ", f + "wrist", side + "手首");
            string[] furinaDigits = { "Thumb", "IndexFinger", "MiddleFinger", "RingFinger", "LittleFinger" };
            string[] digits = { "親指", "人指", "中指", "薬指", "小指" };
            // Hand and fingers are aligned as full frames (direction + palm normal), not only by direction, so the
            // palm faces the same way as Furina's and every finger curls in Furina's bend plane.
            if (HandFrame(source, "MiddleFinger1" + s, "IndexFinger1" + s, "LittleFinger1" + s, f + "wrist", out var srcDir, out var srcPalm)
                && HandFrame(target, side + "中指１", side + "人指１", side + "小指１", side + "手首", out var tgtDir, out var tgtPalm))
            {
                BindFrame(f + "wrist", side + "手首", srcDir, srcPalm, tgtDir, tgtPalm);
                for (int d = 0; d < 5; d++)
                {
                    int start = d == 0 ? 0 : 1;
                    string F(int j) => furinaDigits[d] + (start + j) + s;
                    string H(int j) => side + digits[d] + (char)(0xFF10 + start + j); // full-width digit, as named in the rig
                    for (int j = 0; j < 3; j++)
                    {
                        if (!source.TryGetValue(F(j), out var sj) || !target.TryGetValue(H(j), out var tj)) { missing.Add(H(j)); continue; }
                        Vector3 sd;
                        if (j < 2 && source.TryGetValue(F(j + 1), out var sn)) sd = sn.position - sj.position;
                        else if (j == 2 && source.TryGetValue(F(1), out var sm)) // Furina has no fingertip bones
                            sd = sj.rotation * (Quaternion.Inverse(sm.rotation) * (sj.position - sm.position));
                        else continue;
                        string next = j < 2 ? H(j + 1) : side + digits[d] + "先";
                        if (!target.TryGetValue(next, out var tn)) continue;
                        BindFrame(F(j), H(j), sd, srcPalm, tn.position - tj.position, tgtPalm);
                    }
                }
            }
            else Bind(f + "wrist", side + "手首", "MiddleFinger1" + s, side + "中指１");

            Bind(f + "leg", side + "足", f + "knee", side + "ひざ");
            Bind(f + "knee", side + "ひざ", f + "ankle", side + "足首");
            Bind(f + "ankle", side + "足首");
            Secondary(f + "toe", side + "つま先", 1f);
            // The skin weights sit on the D (deform) copies, which append the IK-style leg bones 1:1.
            AddFollow(side + "足", side + "足D");
            AddFollow(side + "ひざ", side + "ひざD");
            AddFollow(side + "足首", side + "足首D");
            AddFollow(side + "つま先", side + "足先EX");

            var result = new Leg { side = isLeft ? -1f : 1f };
            if (target.TryGetValue(side + "足", out result.hip) && target.TryGetValue(side + "ひざ", out result.knee)
                && target.TryGetValue(side + "足首", out result.foot) && source.TryGetValue(f + "leg", out result.sourceHip)
                && source.TryGetValue(f + "knee", out result.sourceKnee) && source.TryGetValue(f + "ankle", out result.sourceFoot))
            {
                // Rest difference between Hu Tao's own hip->ankle vector and Furina's scaled one.
                Vector3 own = result.foot.position - result.hip.position;
                Vector3 scaledSource = (result.sourceFoot.position - result.sourceHip.position) * MotionScale;
                result.restCorrection = root.InverseTransformVector(own - scaledSource);
            }
            return result;
        }

        void Bind(string a, string b, string aEnd = null, string bEnd = null)
        {
            if (!source.TryGetValue(a, out var from)) { missing.Add("source:" + a); return; }
            if (!target.TryGetValue(b, out var to)) { missing.Add(b); return; }
            var align = Quaternion.identity;
            if (aEnd != null)
            {
                if (!source.TryGetValue(aEnd, out var fromEnd)) { missing.Add("source:" + aEnd); return; }
                if (!target.TryGetValue(bEnd, out var toEnd)) { missing.Add(bEnd); return; }
                Vector3 targetAxis = toEnd.position - to.position;
                Vector3 sourceAxis = fromEnd.position - from.position;
                if (targetAxis.sqrMagnitude > .0000001f && sourceAxis.sqrMagnitude > .0000001f)
                    align = Quaternion.FromToRotation(targetAxis, sourceAxis);
            }
            pose.Add(new Link { from = from, to = to, offset = Quaternion.Inverse(from.rotation) * align * to.rotation });
        }

        // Hand direction (wrist -> middle knuckle) and palm normal (direction x index->little knuckles).
        static bool HandFrame(Dictionary<string, Transform> bones, string middle, string index, string little, string wrist,
            out Vector3 direction, out Vector3 palm)
        {
            direction = palm = Vector3.zero;
            if (!bones.TryGetValue(middle, out var m) || !bones.TryGetValue(index, out var i)
                || !bones.TryGetValue(little, out var l) || !bones.TryGetValue(wrist, out var w)) return false;
            direction = m.position - w.position;
            palm = Vector3.Cross(direction, l.position - i.position);
            return direction.sqrMagnitude > 1e-10f && palm.sqrMagnitude > 1e-12f;
        }

        void BindFrame(string a, string b, Vector3 sourceDir, Vector3 sourceUp, Vector3 targetDir, Vector3 targetUp)
        {
            if (!source.TryGetValue(a, out var from)) { missing.Add("source:" + a); return; }
            if (!target.TryGetValue(b, out var to)) { missing.Add(b); return; }
            var align = Quaternion.identity;
            if (sourceDir.sqrMagnitude > 1e-10f && targetDir.sqrMagnitude > 1e-10f
                && Vector3.Cross(sourceDir, sourceUp).sqrMagnitude > 1e-12f && Vector3.Cross(targetDir, targetUp).sqrMagnitude > 1e-12f)
                align = Quaternion.LookRotation(sourceDir, sourceUp) * Quaternion.Inverse(Quaternion.LookRotation(targetDir, targetUp));
            pose.Add(new Link { from = from, to = to, offset = Quaternion.Inverse(from.rotation) * align * to.rotation });
        }

        void BindAxis(string a, string b, Vector3 sourceAxis, string bEnd)
        {
            if (!source.TryGetValue(a, out var from)) { missing.Add("source:" + a); return; }
            if (!target.TryGetValue(b, out var to)) { missing.Add(b); return; }
            var align = Quaternion.identity;
            if (target.TryGetValue(bEnd, out var toEnd))
            {
                Vector3 targetAxis = toEnd.position - to.position;
                if (targetAxis.sqrMagnitude > .0000001f && sourceAxis.sqrMagnitude > .0000001f)
                    align = Quaternion.FromToRotation(targetAxis, sourceAxis);
            }
            pose.Add(new Link { from = from, to = to, offset = Quaternion.Inverse(from.rotation) * align * to.rotation });
        }

        void Secondary(string a, string b, float share)
        {
            if (!source.TryGetValue(a, out var from)) { missing.Add("source:" + a); return; }
            if (!target.TryGetValue(b, out var to)) { missing.Add(b); return; }
            details.Add(new Detail
            {
                from = from, to = to, fromRest = from.localRotation, toRest = to.localRotation,
                basis = Quaternion.Inverse(to.rotation) * from.rotation, share = share
            });
        }

        void AddFollow(string leaderName, string followerName)
        {
            if (!target.TryGetValue(leaderName, out var leader)) return;
            if (!target.TryGetValue(followerName, out var follower)) { missing.Add(followerName); return; }
            follows.Add(new Follow { leader = leader, follower = follower, offset = Quaternion.Inverse(leader.rotation) * follower.rotation });
        }

        void LateUpdate()
        {
            if (!Ready || !sourcePelvis || !root) return;
            pelvis.position = Scaled(sourcePelvis.position) + root.TransformVector(hipOffset);
            for (int i = 0; i < pose.Count; i++)
            {
                var link = pose[i];
                link.to.rotation = link.from.rotation * link.offset;
                // 上半身3 sits between 上半身 and 上半身2: blend it before the chest is placed.
                if (i == spineMidAfter && spineMid)
                    spineMid.rotation = Quaternion.Slerp(spineLow.rotation, spineHigh.rotation, .5f) * spineMidOffset;
            }
            for (int i = 0; i < details.Count; i++)
            {
                var d = details[i];
                var delta = d.basis * (Quaternion.Inverse(d.fromRest) * d.from.localRotation) * Quaternion.Inverse(d.basis);
                d.to.localRotation = d.toRest * Quaternion.SlerpUnclamped(Quaternion.identity, delta, d.share);
            }
            // Hu Tao's thighs are fuller than Furina's: widen the moving gait a little, never the idle stance.
            float speed = motor ? motor.PlanarSpeed : 0f;
            float walk = motor ? Mathf.Max(.2f, motor.walkSpeed) : 1f;
            float gaitTarget = motor && motor.Grounded ? Mathf.InverseLerp(.1f, walk, speed) : gaitBlend;
            gaitBlend = Mathf.Lerp(gaitBlend, gaitTarget, 1f - Mathf.Exp(-6f * Time.deltaTime));
            FitLeg(left);
            FitLeg(right);
            for (int i = 0; i < follows.Count; i++)
                follows[i].follower.rotation = follows[i].leader.rotation * follows[i].offset;
        }

        // Two-bone fit. The ankle goal is Hu Tao's own hip plus the source hip->ankle vector scaled by the leg
        // ratio (plus the rest stance correction), so a wider pelvis never pulls the feet across each other.
        // The knee bends in the source leg's bend plane, biased forward so a straight leg never flips inward.
        void FitLeg(Leg leg)
        {
            if (!leg.hip || !leg.knee || !leg.foot || !leg.sourceHip || !leg.sourceKnee || !leg.sourceFoot) return;
            var hip = leg.hip.position; var knee = leg.knee.position; var foot = leg.foot.position;
            Vector3 sourceReach = leg.sourceFoot.position - leg.sourceHip.position;
            if (sourceReach.sqrMagnitude < .0000001f) return;
            var goal = hip + sourceReach * MotionScale + root.TransformVector(leg.restCorrection)
                + root.right * (leg.side * gaitWidening * gaitBlend);
            var reach = goal - hip;
            if (reach.sqrMagnitude < .000001f) return;
            var axis = reach.normalized;
            float upper = (knee - hip).magnitude, lower = (foot - knee).magnitude;
            float dist = Mathf.Clamp(reach.magnitude, Mathf.Abs(upper - lower) + .0001f, upper + lower - .0001f);
            float along = (upper * upper - lower * lower + dist * dist) / (2f * dist);
            Vector3 sourceBend = Vector3.ProjectOnPlane(leg.sourceKnee.position - leg.sourceHip.position, sourceReach.normalized);
            var pole = Vector3.ProjectOnPlane(sourceBend + root.forward * (.03f * (upper + lower)), axis);
            if (pole.sqrMagnitude < .0000001f) pole = Vector3.ProjectOnPlane(knee - hip, axis);
            if (pole.sqrMagnitude < .0000001f) return;
            var newKnee = hip + along * axis + pole.normalized * Mathf.Sqrt(Mathf.Max(0, upper * upper - along * along));
            var footRotation = leg.foot.rotation;
            leg.hip.rotation = Quaternion.FromToRotation(knee - hip, newKnee - hip) * leg.hip.rotation;
            leg.knee.rotation = Quaternion.FromToRotation(leg.foot.position - leg.knee.position, hip + axis * dist - leg.knee.position) * leg.knee.rotation;
            leg.foot.rotation = footRotation;
        }
    }
}
