using UnityEngine;
using UnityEngine.EventSystems;

namespace Game.World
{
    public sealed class MapCanvasClick : MonoBehaviour, IPointerClickHandler, IPointerMoveHandler, IPointerExitHandler
    {
        public WorldMapController owner;
        RectTransform rect;
        bool Position(PointerEventData data, out Vector2 uv)
        {
            if (!rect) rect = GetComponent<RectTransform>();
            uv = default;
            if (!rect || !RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, data.position, data.pressEventCamera, out var local)) return false;
            var bounds = rect.rect;
            if (bounds.width <= 0f || bounds.height <= 0f || !bounds.Contains(local)) return false;
            uv = new Vector2((local.x - bounds.xMin) / bounds.width, (local.y - bounds.yMin) / bounds.height);
            return true;
        }
        public void OnPointerClick(PointerEventData data)
        {
            if (owner && data.button == PointerEventData.InputButton.Left && Position(data, out var uv)) owner.TryTeleport(uv);
        }
        public void OnPointerMove(PointerEventData data)
        {
            if (owner && Position(data, out var uv)) owner.SetHoveredPosition(uv);
        }
        public void OnPointerExit(PointerEventData data) { if (owner) owner.ClearHoveredPosition(); }
    }
}
