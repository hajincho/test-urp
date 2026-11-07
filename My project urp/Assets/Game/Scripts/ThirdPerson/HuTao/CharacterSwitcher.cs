using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.ThirdPerson
{
    /// <summary>
    /// Press 1 for the existing Furina model, 2 for the Hu Tao model. Both use the same ThirdPersonMotor, so
    /// locomotion, camera and collision are untouched. Hu Tao copies Furina's final pose, so while Hu Tao is shown
    /// Furina keeps animating invisibly (renderers and her cloth/hair solvers switched off).
    /// Furina also gets multi-joint hair physics (<see cref="FurinaHairRig"/>).
    /// The component bootstraps itself after the scene loads; no scene or prefab edits are required.
    /// Hu Tao is loaded from Resources/HuTao/HuTao (the FBX) and its textures from Resources/HuTao.
    /// F9 logs the measured joint utilization of the active character's chain physics.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class CharacterSwitcher : MonoBehaviour
    {
        const string ModelResource = "HuTao/HuTao";
        const string TextureFolder = "HuTao/";

        ThirdPersonMotor motor;
        ThirdPersonFollowCamera followCamera;
        GameObject furina, huTao;
        int current;
        bool buildFailed;
        float nextBind, boundAt, standingSince = -1f;
        int pendingCharacter = -1;
        UntitledCharacterMotion motion;
        readonly System.Collections.Generic.List<Renderer> hiddenFurinaRenderers = new System.Collections.Generic.List<Renderer>();

        public int CurrentCharacter => current;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (FindAnyObjectByType<CharacterSwitcher>()) return;
            var host = new GameObject("CharacterSwitcher");
            host.AddComponent<CharacterSwitcher>();
            DontDestroyOnLoad(host);
        }

        void Update()
        {
            if (!motor)
            {
                if (Time.unscaledTime < nextBind) return;
                nextBind = Time.unscaledTime + .5f;
                Bind();
                if (!motor) return;
            }

            // Hu Tao's links are aligned to Furina's pose at build time, so build only while Furina has been
            // standing still on the ground for a moment (a build during a step skewed Hu Tao's idle stance).
            bool settled = FurinaSettled();
            if (!huTao && !buildFailed && current == 0 && settled) BuildHuTao();
            if (pendingCharacter == 1 && settled)
            {
                pendingCharacter = -1;
                Activate(1);
            }

            var keyboard = Keyboard.current;
            if (keyboard == null) return;
            if (keyboard.digit1Key.wasPressedThisFrame || keyboard.numpad1Key.wasPressedThisFrame) Activate(0);
            if (keyboard.digit2Key.wasPressedThisFrame || keyboard.numpad2Key.wasPressedThisFrame) Activate(1);
            if (keyboard.f9Key.wasPressedThisFrame)
            {
                var active = current == 1 ? huTao : furina;
                var physics = active ? active.GetComponent<HuTaoChainPhysics>() : null;
                if (physics) physics.LogUtilization();
            }
        }

        bool FurinaSettled()
        {
            bool standing = motor.Grounded && !motor.IsJumping && !motor.IsClimbing && !motor.IsSwimming
                && motor.PlanarSpeed < .05f && (!motion || motion.MotionState == "Idle");
            if (!standing) { standingSince = -1f; return false; }
            if (standingSince < 0f) standingSince = Time.time;
            return Time.time - standingSince > .5f && Time.time - boundAt > .75f;
        }

        void Bind()
        {
            var found = FindAnyObjectByType<ThirdPersonMotor>();
            if (!found) return;
            var retarget = found.GetComponentInChildren<BlenderCharacterRetarget>(true);
            if (!retarget) return; // Not the Furina player rig; stay inert.
            motor = found;
            furina = retarget.gameObject;
            motion = found.GetComponentInChildren<UntitledCharacterMotion>(true);
            standingSince = -1f;
            pendingCharacter = -1;
            followCamera = FindAnyObjectByType<ThirdPersonFollowCamera>();
            huTao = null;
            buildFailed = false;
            current = 0;
            boundAt = Time.time;
            FurinaHairRig.Install(furina);
        }

        void SetFurinaVisible(bool visible)
        {
            if (!furina) return;
            if (!visible)
            {
                hiddenFurinaRenderers.Clear();
                foreach (var renderer in furina.GetComponentsInChildren<Renderer>(true))
                    if (renderer.enabled) { renderer.enabled = false; hiddenFurinaRenderers.Add(renderer); }
            }
            else
            {
                foreach (var renderer in hiddenFurinaRenderers) if (renderer) renderer.enabled = true;
                hiddenFurinaRenderers.Clear();
            }
            // The invisible Furina only needs her body pose; her garment and hair solvers pause meanwhile.
            var cloth = furina.GetComponent<SilkClothDynamics>();
            if (cloth) cloth.enabled = visible;
            var hair = furina.GetComponent<HuTaoChainPhysics>();
            if (hair) hair.enabled = visible;
        }

        bool BuildHuTao()
        {
            var prefab = Resources.Load<GameObject>(ModelResource);
            if (!prefab)
            {
                buildFailed = true;
                Debug.LogWarning("[CharacterSwitcher] Resources/" + ModelResource + " not found. Copy the Hu Tao FBX there; staying on Furina.");
                return false;
            }
            var model = Instantiate(prefab, motor.visualRoot);
            model.name = "HuTao";
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            model.transform.localScale = Vector3.one;
            foreach (var animator in model.GetComponentsInChildren<Animator>(true))
            {
                animator.enabled = false;
                Destroy(animator);
            }
            ApplyMaterials(model);
            var retarget = model.AddComponent<HuTaoRetarget>();
            if (!retarget.Ready)
            {
                Debug.LogWarning("[CharacterSwitcher] Hu Tao retarget could not be built; staying on Furina.");
                Destroy(model);
                buildFailed = true;
                return false;
            }
            var blink = model.AddComponent<HuTaoBlink>();
            if (!blink.Ready) Debug.LogWarning("[CharacterSwitcher] Hu Tao blink shape could not be built.");
            // Twin tails: share the bend of the thick middle section over several joints (before the solver configures).
            HairChainSmoother.Apply(model, "_1", "左后发3_", "右后发3_");
            model.AddComponent<HuTaoChainPhysics>();
            huTao = model;
            huTao.SetActive(current == 1);
            return true;
        }

        void Activate(int index)
        {
            if (!motor || index == current) { pendingCharacter = -1; return; }
            if (index == 1 && !huTao)
            {
                if (!FurinaSettled())
                {
                    // Switch as soon as Furina stands still; building mid-step would bake that step into Hu Tao.
                    if (pendingCharacter != 1) Debug.Log("[CharacterSwitcher] Hu Tao will appear once Furina stands still.");
                    pendingCharacter = 1;
                    return;
                }
                if (!BuildHuTao()) return;
            }
            pendingCharacter = -1;
            current = index;
            if (furina && !furina.activeSelf) furina.SetActive(true);
            SetFurinaVisible(index == 0);
            if (huTao) huTao.SetActive(index == 1);
            var visible = index == 0 ? furina : huTao;
            if (followCamera && visible) followCamera.SetCharacterRoot(visible.transform);
            Debug.Log("[CharacterSwitcher] Active character: " + (index == 0 ? "Furina" : "Hu Tao"));
        }

        // --- Materials (URP Lit, alpha-clipped, double sided). Textures: Hair / Outfit / Face. ---
        static readonly string[] HairMeshes = { "发", "目", "帽", "帽_饰" };
        static readonly string[] OutfitMeshes = { "新規", "鞋袜", "戒指", "带1", "神之眼AL", "肌", "服", "裤" };

        static void ApplyMaterials(GameObject model)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (!shader) { Debug.LogWarning("[CharacterSwitcher] URP Lit shader not found."); return; }
            var hair = MakeMaterial(shader, "Hair");
            var outfit = MakeMaterial(shader, "Outfit");
            var face = MakeMaterial(shader, "Face");
            foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                string meshName = renderer.gameObject.name;
                var material = System.Array.IndexOf(HairMeshes, meshName) >= 0 ? hair
                    : System.Array.IndexOf(OutfitMeshes, meshName) >= 0 ? outfit : face;
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++) materials[i] = material;
                renderer.sharedMaterials = materials;
                var skin = renderer as SkinnedMeshRenderer;
                if (skin) skin.updateWhenOffscreen = true; // moving hair/hem can leave the authored bounds
            }
        }

        static Material MakeMaterial(Shader shader, string textureName)
        {
            var material = new Material(shader) { name = "HuTao_" + textureName };
            var texture = Resources.Load<Texture2D>(TextureFolder + textureName);
            if (texture) material.SetTexture("_BaseMap", texture);
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Smoothness", .2f);
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Cull", 0f);
            material.SetFloat("_AlphaClip", 1f);
            material.SetFloat("_Cutoff", .5f);
            material.EnableKeyword("_ALPHATEST_ON");
            material.SetOverrideTag("RenderType", "TransparentCutout");
            material.renderQueue = 2450;
            return material;
        }
    }
}
