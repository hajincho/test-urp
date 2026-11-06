using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace Game.ThirdPerson
{
    [DefaultExecutionOrder(200)]
    public class ThirdPersonFollowCamera : MonoBehaviour
    {
        public Transform target;
        public Transform playerRoot;
        public Transform characterRoot;
        public float distance = 4f;
        public float minDistance = 0f;
        public float maxDistance = 9f;
        public float pitch = 18f;
        public float yaw;
        public float mouseSensitivity = .15f;
        public float zoomStep = .6f;
        public float firstPersonThreshold = .2f;
        public LayerMask collisionMask = ~0;
        public bool IsFirstPerson => distance <= firstPersonThreshold;
        public float CurrentDistance { get; private set; }
        Renderer[] characterRenderers;
        RaycastHit[] cameraHits = new RaycastHit[32];
        ShadowCastingMode[] originalShadows;
        bool hidden;
        CursorLockMode oldLock;
        bool oldVisible;

        void OnEnable()
        {
            CurrentDistance = distance;
            oldLock = Cursor.lockState; oldVisible = Cursor.visible;
            if (Application.isPlaying) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
            SetCharacterRoot(characterRoot);
        }

        public void SetCharacterRoot(Transform root)
        {
            SetHidden(false);
            characterRoot = root;
            characterRenderers = root ? root.GetComponentsInChildren<Renderer>(true) : null;
            if (characterRenderers == null) return;
            originalShadows = new ShadowCastingMode[characterRenderers.Length];
            for (int i = 0; i < characterRenderers.Length; i++)
                originalShadows[i] = characterRenderers[i].shadowCastingMode;
            SetHidden(IsFirstPerson);
        }

        public void Zoom(float steps) { distance = Mathf.Clamp(distance - steps * zoomStep, minDistance, maxDistance); }
        public void Look(Vector2 delta) { yaw += delta.x; pitch = Mathf.Clamp(pitch - delta.y, -90f, 85f); }
        void LateUpdate()
        {
            if (!target) return;
            var mouse = Mouse.current;
            var pad = Gamepad.current;
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            { Cursor.lockState=CursorLockMode.None; Cursor.visible=true; }
            if (mouse != null && mouse.leftButton.wasPressedThisFrame && Application.isFocused)
            { Cursor.lockState=CursorLockMode.Locked; Cursor.visible=false; }
            Vector2 look = pad != null ? pad.rightStick.ReadValue() * (120f * Time.deltaTime) : Vector2.zero;
            if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
            {
                look += mouse.delta.ReadValue() * mouseSensitivity;
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > .001f) Zoom(Mathf.Clamp(scroll / 120f, -3f, 3f));
            }
            Look(look);
            UpdateView(Time.deltaTime);
        }
        public void UpdateView(float dt)
        {
            if (!target) return;
            bool first = IsFirstPerson;
            SetHidden(first);
            var rotation = Quaternion.Euler(pitch, yaw, 0f);
            var backward = rotation * Vector3.back;
            float desired = first ? 0f : distance;
            CurrentDistance = first ? 0f : Mathf.Lerp(CurrentDistance, desired, 1f-Mathf.Exp(-14f*dt));
            float safeDistance=CurrentDistance;
            if (!first && safeDistance > 0f)
            {
                int count;
                while ((count = Physics.SphereCastNonAlloc(target.position, .15f, backward, cameraHits, safeDistance, collisionMask, QueryTriggerInteraction.Ignore)) == cameraHits.Length)
                    System.Array.Resize(ref cameraHits, cameraHits.Length * 2);
                for (int i = 0; i < count; i++)
                {
                    var hit = cameraHits[i];
                    if (playerRoot && hit.collider.transform.IsChildOf(playerRoot)) continue;
                    safeDistance = Mathf.Min(safeDistance, Mathf.Max(0f, hit.distance-.05f));
                }
            }
            transform.SetPositionAndRotation(target.position+backward*safeDistance,rotation);
        }
        void SetHidden(bool value)
        {
            if (value==hidden || characterRenderers==null) return;
            hidden=value;
            for(int i=0;i<characterRenderers.Length;i++)
                if(characterRenderers[i]) characterRenderers[i].shadowCastingMode=value ? ShadowCastingMode.ShadowsOnly : originalShadows[i];
        }
        void OnDisable()
        {
            SetHidden(false);
            if(Application.isPlaying) { Cursor.lockState=oldLock; Cursor.visible=oldVisible; }
        }
    }
}
