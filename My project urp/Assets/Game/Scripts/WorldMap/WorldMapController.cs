using System;
using System.Collections.Generic;
using Game.ThirdPerson;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;

namespace Game.World
{
    [DefaultExecutionOrder(-200)]
    [RequireComponent(typeof(ThirdPersonMotor))]
    public sealed class WorldMapController : MonoBehaviour
    {
        [Serializable]
        public sealed class VillageWarp
        {
            public string name;
            public Vector3 position;
        }

        public Texture2D mapTexture;
        public Vector2 worldMin = new Vector2(-300f, -160f);
        public Vector2 worldSize = new Vector2(720f, 720f);
        public VillageWarp[] villages = Array.Empty<VillageWarp>();
        public LayerMask groundMask = ~0;
        public LayerMask blockingMask = ~0;
        public float rayHeight = 600f;
        public float maxDrop = 1500f;
        public float nearbySearchRadius = 5f;
        public bool useWaterLevel;
        public float waterLevel;
        public Mesh waterSurfaceMesh;
        public bool IsOpen { get; private set; }
        public string LastStatus { get; private set; }

        ThirdPersonMotor motor;
        ThirdPersonFollowCamera follow;
        CharacterController capsule;
        DoorInteractor doors;
        Canvas canvas;
        GameObject overlay, hud, createdEventSystem;
        RectTransform panel, content, mapRect, sidebar, playerMarker;
        UnityEngine.UI.RawImage mapImage;
        UnityEngine.UI.Text status, coordinates;
        Font font;
        bool ownsFont;
        bool capturedInput, motorWasEnabled, cameraWasEnabled, doorsWereEnabled;
        bool pendingCameraRestore;
        int cameraRestoreFrame;
        CursorLockMode priorLock;
        bool priorCursorVisible;
        Vector2 lastCanvasSize;
        readonly List<RectTransform> villageMarkers = new List<RectTransform>();
        readonly Collider[] overlapHits = new Collider[64];
        readonly RaycastHit[] skyHits = new RaycastHit[48];
        Mesh cachedWaterMesh;
        Vector3[] waterVertices = Array.Empty<Vector3>();
        int[] waterTriangles = Array.Empty<int>();

        static readonly Color Navy = new Color(.025f, .045f, .075f, .985f);
        static readonly Color PanelColor = new Color(.045f, .070f, .105f, 1f);
        static readonly Color Brass = new Color(.82f, .69f, .40f, 1f);
        static readonly Color Cream = new Color(.95f, .94f, .88f, 1f);
        static readonly Color Muted = new Color(.60f, .69f, .76f, 1f);
        static readonly Color Cyan = new Color(.39f, .90f, .94f, 1f);

        void Awake() { Cache(); EnsureUi(); }
        void OnEnable() { if (Application.isPlaying) { Cache(); EnsureUi(); } }
        void Cache()
        {
            if (!motor) motor = GetComponent<ThirdPersonMotor>();
            if (!capsule) capsule = GetComponent<CharacterController>();
            if (!doors) doors = GetComponent<DoorInteractor>();
            if (!follow && motor && motor.cameraTransform) follow = motor.cameraTransform.GetComponent<ThirdPersonFollowCamera>();
        }
        void Update()
        {
            if (pendingCameraRestore && Time.frameCount >= cameraRestoreFrame) RestoreCamera();
            var keys = Keyboard.current;
            if (keys != null && keys.mKey.wasPressedThisFrame) Toggle();
            else if (IsOpen && keys != null && keys.escapeKey.wasPressedThisFrame) SetOpen(false);
            if (!IsOpen) return;
            Layout(); UpdatePlayerMarker();
        }
        public Vector2 WorldToMap(Vector3 position)
        {
            return new Vector2((position.x - worldMin.x) / Mathf.Max(.001f, worldSize.x), (position.z - worldMin.y) / Mathf.Max(.001f, worldSize.y));
        }
        public Vector3 MapToWorld(Vector2 uv) { return new Vector3(worldMin.x + uv.x * worldSize.x, 0f, worldMin.y + uv.y * worldSize.y); }
        public void Toggle() { SetOpen(!IsOpen); }
        public void SetOpen(bool value)
        {
            if (value && !Application.isPlaying) return;
            if (value == IsOpen) return;
            Cache(); EnsureUi();
            if (value)
            {
                if (pendingCameraRestore) RestoreCamera();
                priorLock = Cursor.lockState; priorCursorVisible = Cursor.visible;
                motorWasEnabled = motor && motor.enabled; cameraWasEnabled = follow && follow.enabled; doorsWereEnabled = doors && doors.enabled;
                capturedInput = true;
                if (motor) motor.enabled = false;
                if (follow) follow.enabled = false;
                if (doors) doors.enabled = false;
                Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
                IsOpen = true; overlay.SetActive(true); hud.SetActive(false);
                if (mapImage) mapImage.texture = mapTexture;
                LastStatus = "지도 위 지점이나 중앙 광장 마커를 선택하세요.";
                SetStatus(LastStatus, false); ClearHoveredPosition(); Layout(); UpdatePlayerMarker();
                if (EventSystem.current) EventSystem.current.SetSelectedGameObject(null);
            }
            else
            {
                IsOpen = false; if (overlay) overlay.SetActive(false); if (hud) hud.SetActive(true);
                if (motor) motor.enabled = motorWasEnabled;
                if (doors) doors.enabled = doorsWereEnabled;
                // Escape and the click that closes the map must not also rotate/lock the camera.
                if (capturedInput && isActiveAndEnabled)
                {
                    pendingCameraRestore = true; cameraRestoreFrame = Time.frameCount + 1;
                    Cursor.lockState = priorLock; Cursor.visible = priorCursorVisible;
                }
                else RestoreCamera();
            }
        }
        void RestoreCamera()
        {
            pendingCameraRestore = false;
            if (!capturedInput) return;
            if (follow) { follow.enabled = cameraWasEnabled; if (cameraWasEnabled) follow.UpdateView(1f); }
            Cursor.lockState = priorLock; Cursor.visible = priorCursorVisible;
            capturedInput = false;
        }
        void OnDisable()
        {
            if (IsOpen) SetOpen(false);
            RestoreCamera();
            if (canvas) canvas.gameObject.SetActive(false);
        }
        void OnDestroy()
        {
            RestoreCamera();
            if (canvas) Destroy(canvas.gameObject);
            if (createdEventSystem) Destroy(createdEventSystem);
            if (ownsFont && font) Destroy(font);
        }

        public bool TryTeleport(Vector2 uv)
        {
            if (!TryResolveLanding(uv, out var destination)) { SetStatus("이곳은 이동하기 어렵습니다. 가까운 빈 땅을 선택하세요.", true); return false; }
            Teleport(destination); return true;
        }
        public bool WarpToVillage(int index)
        {
            Cache();
            if (villages == null || index < 0 || index >= villages.Length || villages[index] == null) return false;
            var waypoint = villages[index];
            if (!ResolveNearby(new Vector2(waypoint.position.x, waypoint.position.z), waypoint.position.y, out var destination))
            { SetStatus("광장의 안전한 도착 지점을 찾지 못했습니다.", true); return false; }
            Teleport(destination); return true;
        }
        void Teleport(Vector3 destination)
        {
            Cache(); motor.TeleportTo(destination);
            if (follow) follow.UpdateView(1f);
            SetOpen(false);
        }
        public bool TryResolveLanding(Vector2 uv, out Vector3 destination)
        {
            Cache(); destination = default;
            if (uv.x < 0f || uv.y < 0f || uv.x > 1f || uv.y > 1f || float.IsNaN(uv.x) || float.IsNaN(uv.y) || float.IsInfinity(uv.x) || float.IsInfinity(uv.y)) return false;
            var point = MapToWorld(uv);
            return ResolveNearby(new Vector2(point.x, point.z), float.NaN, out destination);
        }
        bool ResolveNearby(Vector2 xz, float preferredY, out Vector3 destination)
        {
            if (ResolveExact(xz, preferredY, out destination)) return true;
            float radius = Mathf.Max(0f, nearbySearchRadius);
            if (radius < .001f) return false;
            for (int ring = 1; ring <= 3; ring++)
            {
                float distance = radius * ring / 3f;
                for (int i = 0; i < 12; i++)
                {
                    float a = i * Mathf.PI / 6f;
                    var offset = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * distance;
                    var test = xz + offset;
                    if (test.x < worldMin.x || test.y < worldMin.y || test.x > worldMin.x + worldSize.x || test.y > worldMin.y + worldSize.y) continue;
                    if (ResolveExact(test, preferredY, out destination)) return true;
                }
            }
            destination = default; return false;
        }
        struct Surface
        {
            public RaycastHit hit;
            public int score;
            public bool structure, walkway;
        }
        bool ResolveExact(Vector2 xz, float preferredY, out Vector3 destination)
        {
            destination = default;
            if (!capsule) return false;
            float top = Mathf.Max(rayHeight, transform.position.y + 250f);
            if (!float.IsNaN(preferredY)) top = Mathf.Max(top, preferredY + 250f);
            float remaining = Mathf.Max(10f, maxDrop);
            var origin = new Vector3(xz.x, top, xz.y);
            var surfaces = new List<Surface>(16);
            float waterSurface = useWaterLevel ? waterLevel : float.NegativeInfinity;
            waterSurface = Mathf.Max(waterSurface, WaterSurfaceAt(xz));
            float minimumNormal = Mathf.Max(.70f, Mathf.Cos(capsule.slopeLimit * Mathf.Deg2Rad));
            // Recasting below each surface also sees floors underneath the roof of the same batched mesh.
            for (int layer = 0; layer < 64 && remaining > .02f; layer++)
            {
                if (!Physics.Raycast(origin, Vector3.down, out var hit, remaining, groundMask, QueryTriggerInteraction.Ignore)) break;
                float travel = hit.distance + .025f; origin.y -= travel; remaining -= travel;
                if (!hit.collider || hit.collider.transform.IsChildOf(transform)) continue;
                string label = ColliderLabel(hit.collider);
                if (IsWater(label)) { waterSurface = Mathf.Max(waterSurface, hit.point.y); continue; }
                if (Ignored(hit.collider, label) || hit.normal.y < minimumNormal) continue;
                bool walkway = HasAny(label, "bridge", "walkway", "stairs", "stair", "paving", "pavement", "plaza", "courtyard", "road", "street", "path");
                bool ground = HasAny(label, "ground", "terrain", "landscape");
                bool structure = HasAny(label, "openedfacade", "house", "roof", "church", "tower", "barn", "shelter", "stables", "castle", "windmill", "outhouse");
                int score = walkway ? 10 : ground && !(hit.collider is TerrainCollider) ? 9 : hit.collider is TerrainCollider ? 3 : 0;
                if (structure && !walkway && score < 9 && float.IsNaN(preferredY)) continue;
                if (!float.IsNaN(preferredY) && Mathf.Abs(hit.point.y - preferredY) > 2f) continue;
                surfaces.Add(new Surface { hit = hit, score = score, structure = structure, walkway = walkway });
            }
            surfaces.Sort((a, b) =>
            {
                if (!float.IsNaN(preferredY)) return Mathf.Abs(a.hit.point.y - preferredY).CompareTo(Mathf.Abs(b.hit.point.y - preferredY));
                int rank = b.score.CompareTo(a.score);
                return rank != 0 ? rank : b.hit.point.y.CompareTo(a.hit.point.y);
            });
            foreach (var surface in surfaces)
            {
                var hit = surface.hit;
                if (hit.point.y <= waterSurface + .15f) continue;
                // Unknown building/prop meshes need a named plaza height or an explicit walkway.
                if (surface.score == 0 && float.IsNaN(preferredY) && !surface.walkway) continue;
                var centerOffset = transform.TransformVector(capsule.center);
                float yScale = Mathf.Abs(transform.lossyScale.y);
                float radiusScale = Mathf.Max(Mathf.Abs(transform.lossyScale.x), Mathf.Abs(transform.lossyScale.z));
                float height = Mathf.Max(.1f, capsule.height * yScale);
                float radius = Mathf.Min(height * .49f, Mathf.Max(.06f, capsule.radius * radiusScale));
                float bottomOffset = centerOffset.y - height * .5f;
                var root = new Vector3(xz.x, hit.point.y + .045f - bottomOffset, xz.y);
                var center = root + centerOffset;
                float halfSegment = Mathf.Max(0f, height * .5f - radius);
                int overlaps = Physics.OverlapCapsuleNonAlloc(center - Vector3.up * halfSegment, center + Vector3.up * halfSegment,
                    radius * .90f, overlapHits, blockingMask, QueryTriggerInteraction.Ignore);
                if (overlaps == overlapHits.Length) continue;
                bool blocked = false;
                for (int i = 0; i < overlaps; i++)
                {
                    var collider = overlapHits[i];
                    if (!collider || collider.transform.IsChildOf(transform)) continue;
                    string blockerName = ColliderLabel(collider);
                    // Ground sampling may ignore vegetation and doors; the destination capsule
                    // must still respect solid trunks, branches, shut doors and other actors.
                    if (IsWater(blockerName) || HasAny(blockerName, "particle", "fog", "sky")) continue;
                    blocked = true; break;
                }
                if (blocked || !Supported(hit.point, radius, minimumNormal) || !ClearSky(center + Vector3.up * (height * .5f + .05f))) continue;
                destination = root; return true;
            }
            return false;
        }
        float WaterSurfaceAt(Vector2 xz)
        {
            if (cachedWaterMesh != waterSurfaceMesh)
            {
                cachedWaterMesh = waterSurfaceMesh;
                waterVertices = cachedWaterMesh && cachedWaterMesh.isReadable ? cachedWaterMesh.vertices : Array.Empty<Vector3>();
                waterTriangles = cachedWaterMesh && cachedWaterMesh.isReadable ? cachedWaterMesh.triangles : Array.Empty<int>();
            }
            float height = float.NegativeInfinity;
            for (int i = 0; i + 2 < waterTriangles.Length; i += 3)
            {
                var a = waterVertices[waterTriangles[i]]; var b = waterVertices[waterTriangles[i + 1]]; var c = waterVertices[waterTriangles[i + 2]];
                if (xz.x < Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - .001f || xz.x > Mathf.Max(a.x, Mathf.Max(b.x, c.x)) + .001f
                    || xz.y < Mathf.Min(a.z, Mathf.Min(b.z, c.z)) - .001f || xz.y > Mathf.Max(a.z, Mathf.Max(b.z, c.z)) + .001f) continue;
                float abx = b.x - a.x, abz = b.z - a.z, acx = c.x - a.x, acz = c.z - a.z;
                float determinant = abx * acz - abz * acx;
                if (Mathf.Abs(determinant) < .00001f) continue;
                float dx = xz.x - a.x, dz = xz.y - a.z;
                float u = (dx * acz - dz * acx) / determinant;
                float v = (abx * dz - abz * dx) / determinant;
                if (u < -.0001f || v < -.0001f || u + v > 1.0001f) continue;
                height = Mathf.Max(height, a.y + u * (b.y - a.y) + v * (c.y - a.y));
            }
            return height;
        }
        bool Supported(Vector3 foot, float radius, float minimumNormal)
        {
            int found = 0;
            for (int i = 0; i < 5; i++)
            {
                Vector3 offset = i == 0 ? Vector3.zero : i == 1 ? Vector3.right : i == 2 ? Vector3.left : i == 3 ? Vector3.forward : Vector3.back;
                var ray = new Ray(foot + offset * radius * .7f + Vector3.up * .22f, Vector3.down);
                if (!Physics.Raycast(ray, out var hit, .58f, groundMask, QueryTriggerInteraction.Ignore)) continue;
                if (hit.collider.transform.IsChildOf(transform) || Ignored(hit.collider, ColliderLabel(hit.collider)) || hit.normal.y < minimumNormal || Mathf.Abs(hit.point.y - foot.y) > .24f) continue;
                if (i == 0 || found > 0) found++;
            }
            return found >= 4;
        }
        bool ClearSky(Vector3 head)
        {
            int count;
            bool backfaces = Physics.queriesHitBackfaces;
            try
            {
                // A roof can be a single outward-facing sheet. It still covers the floor below.
                Physics.queriesHitBackfaces = true;
                count = Physics.RaycastNonAlloc(head, Vector3.up, skyHits, Mathf.Max(250f, rayHeight), blockingMask, QueryTriggerInteraction.Ignore);
            }
            finally { Physics.queriesHitBackfaces = backfaces; }
            if (count == skyHits.Length) return false;
            for (int i = 0; i < count; i++)
            {
                var collider = skyHits[i].collider;
                if (!collider || collider.transform.IsChildOf(transform) || Ignored(collider, ColliderLabel(collider))) continue;
                return false;
            }
            return true;
        }
        bool Ignored(Collider collider, string label)
        {
            return collider is CharacterController || collider.GetComponentInParent<HouseDoor>() || IsWater(label)
                || HasAny(label, "tree", "bush", "foliage", "grass", "scatter", "canopy", "leaf", "leaves", "flower", "particle", "fog", "sky");
        }
        static bool IsWater(string label) { return HasAny(label, "water", "river", "lake", "ocean"); }
        static bool HasAny(string text, params string[] tokens)
        {
            foreach (string token in tokens) if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
        static string ColliderLabel(Collider collider)
        {
            string label = "";
            for (var t = collider.transform; t; t = t.parent) label += "/" + t.name;
            if (collider is MeshCollider mesh && mesh.sharedMesh) label += "/" + mesh.sharedMesh.name;
            return label;
        }

        public void SetHoveredPosition(Vector2 uv)
        {
            if (!coordinates) return;
            var position = MapToWorld(uv);
            coordinates.text = "선택 위치   " + position.x.ToString("F0") + "  /  " + position.z.ToString("F0");
        }
        public void ClearHoveredPosition() { if (coordinates) coordinates.text = "▲ 현재 위치     ◆ 중앙 광장 워프"; }
        void SetStatus(string message, bool error)
        {
            LastStatus = message;
            if (status) { status.text = message; status.color = error ? new Color(1f, .68f, .46f) : Muted; }
        }
        void UpdatePlayerMarker()
        {
            if (!playerMarker) return;
            var uv = WorldToMap(transform.position);
            playerMarker.gameObject.SetActive(uv.x >= 0 && uv.y >= 0 && uv.x <= 1 && uv.y <= 1);
            playerMarker.anchorMin = playerMarker.anchorMax = uv;
            playerMarker.anchoredPosition = Vector2.zero;
            var arrow = playerMarker.Find("Arrow");
            if (arrow) arrow.localRotation = Quaternion.Euler(0f, 0f, -(motor && motor.visualRoot ? motor.visualRoot.eulerAngles.y : transform.eulerAngles.y));
        }
        public void EnsureUi()
        {
            if (!Application.isPlaying) return;
            if (canvas) { canvas.gameObject.SetActive(true); return; }
            font = Font.CreateDynamicFontFromOSFont(new[] { "Malgun Gothic", "Noto Sans CJK KR", "Arial" }, 28);
            ownsFont = font;
            if (!font) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var root = new GameObject("World Map Canvas", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler), typeof(UnityEngine.UI.GraphicRaycaster));
            canvas = root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 500;
            var scaler = root.GetComponent<UnityEngine.UI.CanvasScaler>(); scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f); scaler.matchWidthOrHeight = .5f;
            EnsureEventSystem();
            hud = Button("Map shortcut", root.transform, "M   지도", Toggle, Brass, new Color(.035f, .055f, .08f, .92f), 20).gameObject;
            var hudRect = hud.GetComponent<RectTransform>(); hudRect.anchorMin = hudRect.anchorMax = hudRect.pivot = Vector2.one;
            hudRect.anchoredPosition = new Vector2(-28f, -28f); hudRect.sizeDelta = new Vector2(132f, 44f);
            overlay = Image("Map overlay", root.transform, Navy).gameObject; Stretch(overlay.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            overlay.GetComponent<UnityEngine.UI.Image>().raycastTarget = true;
            panel = Image("Atlas panel", overlay.transform, PanelColor); panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(.5f, .5f); Border(panel, Brass * new Color(1, 1, 1, .5f));
            var heading = Label("Title", panel, "세계 지도", 34, Cream, TextAnchor.MiddleLeft); Anchor(heading.rectTransform, new Vector2(0, 1), new Vector2(24, -46), new Vector2(440, 48), new Vector2(0, .5f));
            var eyebrow = Label("Eyebrow", panel, "WORLD ATLAS  /  빠른 이동", 13, Brass, TextAnchor.MiddleLeft); Anchor(eyebrow.rectTransform, new Vector2(0, 1), new Vector2(26, -82), new Vector2(450, 24), new Vector2(0, .5f));
            var close = Button("Close map", panel, "닫기   [ M ]", () => SetOpen(false), Cream, new Color(.09f, .13f, .18f), 19);
            Anchor(close.GetComponent<RectTransform>(), new Vector2(1, 1), new Vector2(-24, -48), new Vector2(160, 44), new Vector2(1, .5f));
            var line = Image("Header rule", panel, new Color(Brass.r, Brass.g, Brass.b, .3f)); Stretch(line, new Vector2(0, 1), new Vector2(1, 1), new Vector2(24, -108), new Vector2(-24, -107));
            content = Rect("Map content", panel); Stretch(content, Vector2.zero, Vector2.one, new Vector2(24, 66), new Vector2(-24, -126));
            mapRect = Image("Map frame", content, new Color(.015f, .025f, .035f)); Border(mapRect, Brass);
            var raw = new GameObject("Map artwork", typeof(RectTransform), typeof(UnityEngine.UI.RawImage), typeof(MapCanvasClick)); raw.transform.SetParent(mapRect, false);
            mapImage = raw.GetComponent<UnityEngine.UI.RawImage>(); mapImage.texture = mapTexture; mapImage.color = Color.white; mapImage.raycastTarget = true;
            Stretch(raw.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.one * 3f, Vector2.one * -3f);
            // Markers are children of the actual image, not its border or fitted container.
            var interaction = raw.GetComponent<MapCanvasClick>(); interaction.owner = this;
            var north = Label("North", raw.transform, "N\n↑", 18, Cream, TextAnchor.MiddleCenter); Anchor(north.rectTransform, new Vector2(1, 1), new Vector2(-26, -32), new Vector2(40, 58), new Vector2(.5f, .5f));
            sidebar = Image("Travel panel", content, new Color(.025f, .045f, .070f, .95f)); Border(sidebar, new Color(Brass.r, Brass.g, Brass.b, .25f));
            var title = Label("Travel heading", sidebar, "중앙 광장", 26, Cream, TextAnchor.UpperLeft); Anchor(title.rectTransform, new Vector2(0, 1), new Vector2(22, -24), new Vector2(300, 40), Vector2.up);
            var helper = Label("Travel description", sidebar, "마을을 선택하면\n중앙 광장으로 이동합니다.", 17, Muted, TextAnchor.UpperLeft); Anchor(helper.rectTransform, new Vector2(0, 1), new Vector2(24, -78), new Vector2(300, 66), Vector2.up);
            int count = villages == null ? 0 : villages.Length;
            for (int i = 0; i < count; i++)
            {
                if (villages[i] == null) continue;
                int index = i;
                var button = Button("Village " + i, sidebar, "◆   " + villages[i].name + "\n      중앙 광장으로 이동", () => WarpToVillage(index), Cream, new Color(.075f, .11f, .15f), 19);
                var br = button.GetComponent<RectTransform>(); br.anchorMin = new Vector2(0, 1); br.anchorMax = new Vector2(1, 1); br.pivot = Vector2.up; br.offsetMin = new Vector2(18, -222 - i * 92); br.offsetMax = new Vector2(-18, -144 - i * 92);
                var marker = Button("Plaza marker " + i, raw.transform, "◆", () => WarpToVillage(index), Brass, new Color(.025f, .045f, .07f, .94f), 24);
                var mr = marker.GetComponent<RectTransform>(); mr.anchorMin = mr.anchorMax = WorldToMap(villages[i].position); mr.pivot = new Vector2(.5f, .5f); mr.sizeDelta = new Vector2(40, 40); mr.anchoredPosition = Vector2.zero; Border(mr, Brass);
                var label = Label("Plaza name", mr, villages[i].name, 15, Cream, TextAnchor.UpperCenter); Anchor(label.rectTransform, new Vector2(.5f, 0), new Vector2(0, -8), new Vector2(160, 40), new Vector2(.5f, 1));
                var shadow = label.gameObject.AddComponent<UnityEngine.UI.Shadow>(); shadow.effectColor = new Color(0, 0, 0, .9f); shadow.effectDistance = new Vector2(1, -1);
                villageMarkers.Add(mr);
            }
            playerMarker = Rect("Current player", raw.transform); playerMarker.sizeDelta = new Vector2(30, 30); playerMarker.pivot = new Vector2(.5f, .5f);
            var arrowText = Label("Arrow", playerMarker, "▲", 30, Cyan, TextAnchor.MiddleCenter); Stretch(arrowText.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var arrowShadow = arrowText.gameObject.AddComponent<UnityEngine.UI.Shadow>(); arrowShadow.effectColor = Color.black; arrowShadow.effectDistance = new Vector2(1, -1);
            var you = Label("Player label", playerMarker, "현재 위치", 15, Cyan, TextAnchor.UpperCenter); Anchor(you.rectTransform, new Vector2(.5f, 0), new Vector2(0, -4), new Vector2(110, 30), new Vector2(.5f, 1));
            var tip = Label("Map instructions", sidebar, "원하는 곳으로 이동\n\n지도 위 빈 땅을 클릭하세요.\n물·급경사·건물 위는 피해서\n안전한 땅에 도착합니다.", 17, Muted, TextAnchor.UpperLeft); Anchor(tip.rectTransform, new Vector2(0, 0), new Vector2(24, 28), new Vector2(310, 176), Vector2.zero);
            status = Label("Map status", panel, "", 17, Muted, TextAnchor.MiddleLeft); Anchor(status.rectTransform, new Vector2(0, 0), new Vector2(26, 40), new Vector2(930, 28), new Vector2(0, .5f));
            coordinates = Label("Map coordinates", panel, "", 15, Brass, TextAnchor.MiddleRight); Anchor(coordinates.rectTransform, new Vector2(1, 0), new Vector2(-26, 38), new Vector2(420, 28), new Vector2(1, .5f));
            var footer = Label("Map keyboard hint", overlay.transform, "[ M ] / [ Esc ]  닫기    ·    지도 클릭 순간이동", 17, Cream, TextAnchor.MiddleCenter); Anchor(footer.rectTransform, new Vector2(.5f, 0), new Vector2(0, 22), new Vector2(900, 30), new Vector2(.5f, .5f));
            overlay.SetActive(false); Layout();
        }
        void EnsureEventSystem()
        {
            var systems = FindObjectsByType<EventSystem>(FindObjectsInactive.Include);
            EventSystem system = null;
            foreach (var candidate in systems) if (candidate.isActiveAndEnabled) { system = candidate; break; }
            if (!system && systems.Length > 0) { system = systems[0]; system.gameObject.SetActive(true); system.enabled = true; }
            if (!system) { createdEventSystem = new GameObject("World Map EventSystem", typeof(EventSystem)); system = createdEventSystem.GetComponent<EventSystem>(); }
            var input = system.GetComponent<InputSystemUIInputModule>();
            if (!input)
            {
                foreach (var other in system.GetComponents<BaseInputModule>()) other.enabled = false;
                input = system.gameObject.AddComponent<InputSystemUIInputModule>(); input.AssignDefaultActions();
            }
            input.enabled = true;
        }
        void Layout()
        {
            if (!panel || !canvas) return;
            var root = canvas.GetComponent<RectTransform>().rect.size;
            if ((root - lastCanvasSize).sqrMagnitude < .1f && panel.sizeDelta.sqrMagnitude > 1f) return;
            lastCanvasSize = root;
            panel.sizeDelta = new Vector2(Mathf.Min(1580f, Mathf.Max(760f, root.x - 96f)), Mathf.Min(980f, Mathf.Max(540f, root.y - 84f)));
            var available = panel.sizeDelta - new Vector2(48, 192);
            float sideWidth = Mathf.Clamp(available.x * .25f, 270f, 360f);
            float aspect = Mathf.Max(.05f, worldSize.x / Mathf.Max(.001f, worldSize.y));
            float maxWidth = Mathf.Max(200f, available.x - sideWidth - 28f), maxHeight = Mathf.Max(240f, available.y);
            float width = Mathf.Min(maxWidth - 6f, (maxHeight - 6f) * aspect), height = width / aspect;
            float outerWidth = width + 6f;
            float total = outerWidth + 28f + sideWidth;
            mapRect.anchorMin = mapRect.anchorMax = mapRect.pivot = new Vector2(.5f, .5f); mapRect.sizeDelta = new Vector2(outerWidth, height + 6f); mapRect.anchoredPosition = new Vector2(-total * .5f + outerWidth * .5f, 0);
            sidebar.anchorMin = sidebar.anchorMax = sidebar.pivot = new Vector2(.5f, .5f); sidebar.sizeDelta = new Vector2(sideWidth, maxHeight); sidebar.anchoredPosition = new Vector2(total * .5f - sideWidth * .5f, 0);
        }
        RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false); return go.GetComponent<RectTransform>();
        }
        RectTransform Image(string name, Transform parent, Color color)
        {
            var rect = Rect(name, parent); var image = rect.gameObject.AddComponent<UnityEngine.UI.Image>(); image.color = color; image.raycastTarget = false; return rect;
        }
        UnityEngine.UI.Text Label(string name, Transform parent, string text, int size, Color color, TextAnchor alignment)
        {
            var rect = Rect(name, parent); var label = rect.gameObject.AddComponent<UnityEngine.UI.Text>(); label.font = font; label.text = text; label.fontSize = size; label.color = color; label.alignment = alignment;
            label.raycastTarget = false; label.horizontalOverflow = HorizontalWrapMode.Wrap; label.verticalOverflow = VerticalWrapMode.Overflow; return label;
        }
        UnityEngine.UI.Button Button(string name, Transform parent, string text, UnityEngine.Events.UnityAction action, Color foreground, Color background, int size)
        {
            var rect = Image(name, parent, background); var graphic = rect.GetComponent<UnityEngine.UI.Image>(); graphic.raycastTarget = true;
            var button = rect.gameObject.AddComponent<UnityEngine.UI.Button>(); button.targetGraphic = graphic;
            var colors = button.colors; colors.normalColor = Color.white; colors.highlightedColor = new Color(1.18f, 1.18f, 1.12f); colors.pressedColor = new Color(.78f, .78f, .70f); colors.selectedColor = Color.white; button.colors = colors;
            button.navigation = new UnityEngine.UI.Navigation { mode = UnityEngine.UI.Navigation.Mode.None }; button.onClick.AddListener(action);
            var label = Label("Label", rect, text, size, foreground, TextAnchor.MiddleCenter); Stretch(label.rectTransform, Vector2.zero, Vector2.one, new Vector2(8, 3), new Vector2(-8, -3)); return button;
        }
        static void Border(RectTransform rect, Color color)
        {
            var outline = rect.gameObject.AddComponent<UnityEngine.UI.Outline>(); outline.effectColor = color; outline.effectDistance = Vector2.one; outline.useGraphicAlpha = false;
        }
        static void Anchor(RectTransform rect, Vector2 anchor, Vector2 position, Vector2 size, Vector2 pivot)
        {
            rect.anchorMin = rect.anchorMax = anchor; rect.pivot = pivot; rect.anchoredPosition = position; rect.sizeDelta = size;
        }
        static void Stretch(RectTransform rect, Vector2 min, Vector2 max, Vector2 low, Vector2 high)
        {
            rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = low; rect.offsetMax = high;
        }
    }
}
