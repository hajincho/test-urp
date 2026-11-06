using System.Collections.Generic;
using UnityEngine;

namespace Game.ThirdPerson
{
    /// <summary>
    /// Thick hair sections deform like plastic when one joint carries a whole bulge: the inside of the bend collapses.
    /// For the given chains this measures each segment's cross-section (in bind/mesh space) and, where the strand is
    /// thick relative to the joint spacing, re-spreads the chain's skin weight over several neighbouring joints with a
    /// Gaussian along the strand. The bend is then shared by 3-4 joints instead of one. Thin parts keep their
    /// original weights (blend factor = thickness), non-chain influences are never changed.
    /// The measured thickness (mean cross-section radius / segment length) is exposed for the solver, which also
    /// limits per-joint bending there (<see cref="HuTaoChainPhysics"/>).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HairChainSmoother : MonoBehaviour
    {
        /// <summary>Thickness ratio where smoothing / bend limiting starts and where it is fully applied.</summary>
        public const float ThickStart = .8f, ThickFull = 1.6f;

        readonly Dictionary<Transform, float> thickness = new Dictionary<Transform, float>();
        readonly List<SkinnedMeshRenderer> skins = new List<SkinnedMeshRenderer>();
        readonly List<Mesh> originals = new List<Mesh>(), created = new List<Mesh>();

        public int SmoothedVertices { get; private set; }

        public bool TryGetThickness(Transform bone, out float ratio) => thickness.TryGetValue(bone, out ratio);
        public static float Thickness01(float ratio) => Mathf.InverseLerp(ThickStart, ThickFull, ratio);

        /// <summary>Chains are named prefix + index + suffix (e.g. 左后发3_0_1 ... 左后发3_15_1).</summary>
        public static HairChainSmoother Apply(GameObject model, string suffix, params string[] prefixes)
        {
            var smoother = model.GetComponent<HairChainSmoother>();
            if (!smoother) smoother = model.AddComponent<HairChainSmoother>();
            var names = new Dictionary<string, Transform>();
            foreach (var t in model.GetComponentsInChildren<Transform>(true)) names[t.name] = t;
            foreach (var prefix in prefixes)
            {
                var chain = new List<Transform>();
                for (int i = 0; names.TryGetValue(prefix + i + suffix, out var bone); i++) chain.Add(bone);
                if (chain.Count >= 3) smoother.Smooth(chain);
            }
            Debug.Log($"[HairChainSmoother] thick-section vertices re-weighted={smoother.SmoothedVertices} measured bones={smoother.thickness.Count}");
            return smoother;
        }

        void Smooth(List<Transform> chain)
        {
            foreach (var skin in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var bones = skin.bones;
                int[] index = new int[chain.Count];
                bool any = false;
                for (int j = 0; j < chain.Count; j++) { index[j] = System.Array.IndexOf(bones, chain[j]); any |= index[j] >= 0; }
                if (!any || !skin.sharedMesh || !skin.sharedMesh.isReadable) continue;
                SmoothSkin(skin, chain, index);
            }
        }

        void SmoothSkin(SkinnedMeshRenderer skin, List<Transform> chain, int[] index)
        {
            var mesh = skin.sharedMesh;
            var bind = mesh.bindposes;
            var vertices = mesh.vertices;
            var weights = mesh.boneWeights;
            int n = chain.Count;
            // Joint positions in mesh space; the last point is the tail of the last bone (its first child).
            var points = new Vector3[n + 1];
            for (int j = 0; j < n; j++)
            {
                if (index[j] < 0) return; // chain must be fully skinned to this mesh
                points[j] = bind[index[j]].inverse.MultiplyPoint3x4(Vector3.zero);
            }
            var last = chain[n - 1];
            Vector3 tailLocal = last.childCount > 0 ? last.GetChild(0).localPosition : (points[n - 1] - points[n - 2]);
            points[n] = last.childCount > 0 ? bind[index[n - 1]].inverse.MultiplyPoint3x4(tailLocal) : points[n - 1] + tailLocal;
            var boneOf = new Dictionary<int, int>();
            for (int j = 0; j < n; j++) boneOf[index[j]] = j;

            // Arc parameter of every chain-weighted vertex, and dominant chain joint.
            var param = new float[vertices.Length];
            var dominant = new int[vertices.Length];
            for (int v = 0; v < vertices.Length; v++)
            {
                dominant[v] = -1;
                var w = weights[v];
                if (ChainWeight(w, boneOf) <= 0f) continue;
                param[v] = Project(points, vertices[v]);
                int best = Dominant(w);
                dominant[v] = boneOf.TryGetValue(best, out var j) ? j : -1;
            }

            // Per-joint centre (mean arc parameter) and thickness ((min+max) principal radius / segment length).
            var centre = new float[n];
            var ratio = new float[n];
            for (int j = 0; j < n; j++)
            {
                Vector3 axis = points[j + 1] - points[j];
                float length = axis.magnitude;
                centre[j] = j + .5f;
                if (length < 1e-6f) continue;
                axis /= length;
                Vector3 u = Vector3.Cross(axis, Mathf.Abs(axis.z) < .9f ? Vector3.forward : Vector3.right).normalized;
                Vector3 w2 = Vector3.Cross(axis, u);
                double su = 0, sv = 0, suu = 0, svv = 0, suv = 0, sp = 0; int count = 0;
                for (int v = 0; v < vertices.Length; v++)
                {
                    if (dominant[v] != j) continue;
                    Vector3 rel = vertices[v] - points[j];
                    double pu = Vector3.Dot(rel, u), pv = Vector3.Dot(rel, w2);
                    su += pu; sv += pv; suu += pu * pu; svv += pv * pv; suv += pu * pv; sp += param[v]; count++;
                }
                if (count < 4) continue;
                centre[j] = (float)(sp / count);
                double mu = su / count, mv = sv / count;
                double cuu = suu / count - mu * mu, cvv = svv / count - mv * mv, cuv = suv / count - mu * mv;
                double tr = cuu + cvv, det = cuu * cvv - cuv * cuv, disc = System.Math.Sqrt(System.Math.Max(0, tr * tr / 4 - det));
                double rMin = 2 * System.Math.Sqrt(System.Math.Max(0, tr / 2 - disc));
                double rMax = 2 * System.Math.Sqrt(System.Math.Max(0, tr / 2 + disc));
                ratio[j] = (float)((rMin + rMax) * .5 / length);
                thickness[chain[j]] = ratio[j];
            }

            bool changed = false;
            var gauss = new float[n];
            var list = new List<KeyValuePair<int, float>>(8);
            for (int v = 0; v < vertices.Length; v++)
            {
                if (dominant[v] < 0 && ChainWeight(weights[v], boneOf) <= 0f) continue;
                float s = param[v];
                int nearest = 0;
                for (int j = 1; j < n; j++) if (Mathf.Abs(centre[j] - s) < Mathf.Abs(centre[nearest] - s)) nearest = j;
                float thick = Thickness01(ratio[nearest]);
                if (thick <= 0f) continue;
                var w = weights[v];
                float chainWeight = ChainWeight(w, boneOf);
                float sigma = Mathf.Lerp(.45f, 1.4f, thick);
                float sum = 0f;
                for (int j = 0; j < n; j++)
                {
                    float d = (s - centre[j]) / sigma;
                    gauss[j] = Mathf.Exp(-.5f * d * d);
                    sum += gauss[j];
                }
                if (sum <= 1e-6f) continue;
                // Blend original chain weights toward the Gaussian spread by the local thickness.
                var combined = new Dictionary<int, float>();
                int[] bi = { w.boneIndex0, w.boneIndex1, w.boneIndex2, w.boneIndex3 };
                float[] bw = { w.weight0, w.weight1, w.weight2, w.weight3 };
                for (int k = 0; k < 4; k++)
                {
                    if (bw[k] <= 0f) continue;
                    float scale = boneOf.ContainsKey(bi[k]) ? 1f - thick : 1f;
                    combined.TryGetValue(bi[k], out var old);
                    combined[bi[k]] = old + bw[k] * scale;
                }
                for (int j = 0; j < n; j++)
                {
                    float add = chainWeight * thick * gauss[j] / sum;
                    if (add <= 1e-5f) continue;
                    combined.TryGetValue(index[j], out var old);
                    combined[index[j]] = old + add;
                }
                list.Clear();
                foreach (var pair in combined) list.Add(pair);
                list.Sort((a, b) => b.Value.CompareTo(a.Value));
                while (list.Count < 4) list.Add(new KeyValuePair<int, float>(0, 0f));
                float total = list[0].Value + list[1].Value + list[2].Value + list[3].Value;
                if (total <= 1e-6f) continue;
                weights[v] = new BoneWeight
                {
                    boneIndex0 = list[0].Key, weight0 = list[0].Value / total,
                    boneIndex1 = list[1].Key, weight1 = list[1].Value / total,
                    boneIndex2 = list[2].Key, weight2 = list[2].Value / total,
                    boneIndex3 = list[3].Key, weight3 = list[3].Value / total
                };
                changed = true;
                SmoothedVertices++;
            }
            if (!changed) return;
            var copy = Instantiate(mesh);
            copy.name = mesh.name + " (smoothed thick hair)";
            copy.boneWeights = weights;
            originals.Add(mesh); created.Add(copy); skins.Add(skin);
            skin.sharedMesh = copy;
        }

        static float ChainWeight(BoneWeight w, Dictionary<int, int> boneOf) =>
            (boneOf.ContainsKey(w.boneIndex0) ? w.weight0 : 0f) + (boneOf.ContainsKey(w.boneIndex1) ? w.weight1 : 0f)
            + (boneOf.ContainsKey(w.boneIndex2) ? w.weight2 : 0f) + (boneOf.ContainsKey(w.boneIndex3) ? w.weight3 : 0f);

        static int Dominant(BoneWeight w)
        {
            int best = w.boneIndex0; float value = w.weight0;
            if (w.weight1 > value) { best = w.boneIndex1; value = w.weight1; }
            if (w.weight2 > value) { best = w.boneIndex2; value = w.weight2; }
            if (w.weight3 > value) best = w.boneIndex3;
            return best;
        }

        // Arc-length parameter (in segments) of the closest point on the joint polyline.
        static float Project(Vector3[] points, Vector3 p)
        {
            float best = float.MaxValue, result = 0f;
            for (int k = 0; k < points.Length - 1; k++)
            {
                Vector3 a = points[k], ab = points[k + 1] - a;
                float len2 = ab.sqrMagnitude;
                float t = len2 > 1e-12f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2) : 0f;
                float d = (a + ab * t - p).sqrMagnitude;
                if (d < best) { best = d; result = k + t; }
            }
            return result;
        }

        void OnDestroy()
        {
            for (int i = skins.Count - 1; i >= 0; i--) // newest first: each copy may wrap an earlier one
                if (skins[i] && skins[i].sharedMesh == created[i]) skins[i].sharedMesh = originals[i];
            foreach (var mesh in created) if (mesh) Destroy(mesh);
        }
    }
}
