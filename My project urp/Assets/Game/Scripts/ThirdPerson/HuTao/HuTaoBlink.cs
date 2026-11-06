using System.Collections.Generic;
using UnityEngine;

namespace Game.ThirdPerson
{
    /// <summary>
    /// The Hu Tao model ships without any shape keys, so this builds a "Blink" blend shape at run time and
    /// drives it exactly like Furina's IdleBlink: from the motion skeleton's IdleBlink mesh, or from
    /// <see cref="UntitledCharacterMotion.BlinkWeight"/>.
    ///
    /// Shape: for each eye an ellipse is taken from the eye-white mesh (白目). Everything of the upper lid
    /// (face 面 / 面1, lashes 睫, double-lid line 二重) above the closing curve is compressed onto that curve,
    /// with the skin above the lid easing back to rest; the eyeball meshes (白目 / 目 / 目星) collapse onto the
    /// same curve so nothing shows through. Brows (眉) are untouched. Only the eye regions move.
    /// </summary>
    [DefaultExecutionOrder(196)]
    [DisallowMultipleComponent]
    public sealed class HuTaoBlink : MonoBehaviour
    {
        public const string ShapeName = "Blink";
        static readonly string[] EyeMeshes = { "白目", "目", "目星" };
        static readonly string[] LidMeshes = { "面", "面1", "睫", "二重" };

        [Tooltip("Closing curve depth below the eye centre, in eye half-heights.")]
        [Range(.5f, 1.2f)] public float closeDepth = .95f;
        [Tooltip("Width of the closing curve relative to the eye-white half-width.")]
        [Range(1f, 1.5f)] public float cornerWidth = 1.12f;
        [Tooltip("Remaining thickness of the closed upper lid (0 = flat line).")]
        [Range(0f, .3f)] public float lidSquash = .06f;
        [Tooltip("Height of the skin band above the eye that eases back to rest, in eye half-heights.")]
        [Range(.2f, 1.5f)] public float skinRamp = .6f;
        [Tooltip("Horizontal reach of the deformation, in eye half-widths.")]
        [Range(1.1f, 2.5f)] public float horizontalReach = 2f;

        struct Eye { public float cx, cy, a, b; }

        readonly List<SkinnedMeshRenderer> skins = new List<SkinnedMeshRenderer>();
        readonly List<int> shapeIndices = new List<int>();
        readonly List<Mesh> originals = new List<Mesh>();
        readonly List<Mesh> created = new List<Mesh>();
        UntitledCharacterMotion motionSource;
        SkinnedMeshRenderer sourceEyes;
        int sourceBlink = -1;

        public bool Ready => skins.Count > 0;
        public float Weight { get; private set; }

        void Awake()
        {
            var motor = GetComponentInParent<ThirdPersonMotor>();
            motionSource = motor ? motor.GetComponentInChildren<UntitledCharacterMotion>(true) : null;
            if (motionSource)
                foreach (var part in motionSource.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    int index = part.sharedMesh ? part.sharedMesh.GetBlendShapeIndex("IdleBlink") : -1;
                    if (index >= 0) { sourceEyes = part; sourceBlink = index; break; }
                }
            Build(motor && motor.visualRoot ? motor.visualRoot.up : Vector3.up);
        }

        void Build(Vector3 worldUp)
        {
            var bones = new Dictionary<string, Transform>();
            foreach (var t in GetComponentsInChildren<Transform>(true)) bones[t.name] = t;
            if (!bones.TryGetValue("左目", out var leftEye) || !bones.TryGetValue("右目", out var rightEye))
            {
                Debug.LogWarning("[HuTaoBlink] Eye bones 左目/右目 not found; blink disabled.");
                return;
            }
            Vector3 mid = (leftEye.position + rightEye.position) * .5f;
            Vector3 right = (rightEye.position - leftEye.position).normalized;
            Vector3 up = Vector3.ProjectOnPlane(worldUp, right).normalized;
            if (right.sqrMagnitude < .5f || up.sqrMagnitude < .5f) return;

            var renderers = new Dictionary<string, SkinnedMeshRenderer>();
            foreach (var skin in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (skin.sharedMesh) renderers[skin.gameObject.name] = skin;
            if (!renderers.TryGetValue("白目", out var whites) || !whites.sharedMesh.isReadable)
            {
                Debug.LogWarning("[HuTaoBlink] Eye-white mesh 白目 missing or not readable; blink disabled.");
                return;
            }

            // Eye ellipses from the eye-white vertices, split left/right of the eye midpoint.
            var whiteMatrix = SkinMatrix(whites);
            float[] minX = { float.MaxValue, float.MaxValue }, maxX = { float.MinValue, float.MinValue };
            float[] minY = { float.MaxValue, float.MaxValue }, maxY = { float.MinValue, float.MinValue };
            foreach (var vertex in whites.sharedMesh.vertices)
            {
                Vector3 p = whiteMatrix.MultiplyPoint3x4(vertex) - mid;
                float x = Vector3.Dot(p, right), y = Vector3.Dot(p, up);
                int s = x >= 0f ? 1 : 0;
                minX[s] = Mathf.Min(minX[s], x); maxX[s] = Mathf.Max(maxX[s], x);
                minY[s] = Mathf.Min(minY[s], y); maxY[s] = Mathf.Max(maxY[s], y);
            }
            var eyes = new List<Eye>(2);
            for (int s = 0; s < 2; s++)
            {
                if (maxX[s] <= minX[s] || maxY[s] <= minY[s]) continue;
                eyes.Add(new Eye { cx = (minX[s] + maxX[s]) * .5f, cy = (minY[s] + maxY[s]) * .5f,
                    a = (maxX[s] - minX[s]) * .5f, b = (maxY[s] - minY[s]) * .5f });
            }
            if (eyes.Count == 0) return;

            foreach (var eyeMesh in EyeMeshes) AddShape(renderers, eyeMesh, true, mid, right, up, eyes);
            foreach (var lidMesh in LidMeshes) AddShape(renderers, lidMesh, false, mid, right, up, eyes);
            if (skins.Count == 0) Debug.LogWarning("[HuTaoBlink] No eye meshes received a blink shape.");
        }

        void AddShape(Dictionary<string, SkinnedMeshRenderer> renderers, string meshName, bool eyeball,
            Vector3 mid, Vector3 right, Vector3 up, List<Eye> eyes)
        {
            if (!renderers.TryGetValue(meshName, out var skin)) return;
            var mesh = skin.sharedMesh;
            if (!mesh || !mesh.isReadable || mesh.GetBlendShapeIndex(ShapeName) >= 0) return;
            var matrix = SkinMatrix(skin);
            var inverse = matrix.inverse;
            var vertices = mesh.vertices;
            var deltas = new Vector3[vertices.Length];
            bool any = false;
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 p = matrix.MultiplyPoint3x4(vertices[i]) - mid;
                float x = Vector3.Dot(p, right), y = Vector3.Dot(p, up), dy = 0f;
                foreach (var eye in eyes)
                {
                    float u = (x - eye.cx) / eye.a;
                    float reach = Mathf.Clamp01((horizontalReach - Mathf.Abs(u)) / (horizontalReach - 1f));
                    if (reach <= 0f) continue;
                    reach = Mathf.SmoothStep(0f, 1f, reach);
                    float q = Mathf.Sqrt(Mathf.Clamp01(1f - (u / cornerWidth) * (u / cornerWidth)));
                    float qTop = Mathf.Sqrt(Mathf.Clamp01(1f - u * u));
                    float yClose = eye.cy - eye.b * closeDepth * q;
                    float closed;
                    if (eyeball) closed = yClose;
                    else
                    {
                        if (y <= yClose) continue;
                        float yTop = eye.cy + eye.b * Mathf.Max(qTop, .35f) * 1.05f;
                        float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((y - yTop) / (skinRamp * eye.b)));
                        float squash = lidSquash + (1f - lidSquash) * t;
                        closed = yClose + (y - yClose) * squash;
                    }
                    dy += (closed - y) * reach;
                }
                if (dy == 0f) continue;
                deltas[i] = inverse.MultiplyVector(up * dy);
                any = true;
            }
            if (!any) return;
            var copy = Instantiate(mesh);
            copy.name = mesh.name + " (Blink)";
            copy.AddBlendShapeFrame(ShapeName, 100f, deltas, null, null);
            originals.Add(mesh);
            created.Add(copy);
            skin.sharedMesh = copy;
            skins.Add(skin);
            shapeIndices.Add(copy.GetBlendShapeIndex(ShapeName));
        }

        // Mesh space -> world at the current (rest) pose, through the head bone when the mesh is skinned to it.
        static Matrix4x4 SkinMatrix(SkinnedMeshRenderer skin)
        {
            var bones = skin.bones;
            var bind = skin.sharedMesh.bindposes;
            int chosen = -1;
            for (int i = 0; i < bones.Length && i < bind.Length; i++)
            {
                if (!bones[i]) continue;
                if (bones[i].name == "頭") { chosen = i; break; }
                if (chosen < 0) chosen = i;
            }
            return chosen >= 0 ? bones[chosen].localToWorldMatrix * bind[chosen] : skin.transform.localToWorldMatrix;
        }

        void LateUpdate()
        {
            if (skins.Count == 0) return;
            float weight = sourceEyes ? sourceEyes.GetBlendShapeWeight(sourceBlink)
                : motionSource ? motionSource.BlinkWeight : 0f;
            Weight = Mathf.Clamp(weight, 0f, 100f);
            for (int i = 0; i < skins.Count; i++)
                if (skins[i]) skins[i].SetBlendShapeWeight(shapeIndices[i], Weight);
        }

        void OnDestroy()
        {
            for (int i = 0; i < skins.Count; i++)
                if (skins[i] && skins[i].sharedMesh == created[i]) skins[i].sharedMesh = originals[i];
            foreach (var mesh in created) if (mesh) Destroy(mesh);
        }
    }
}
