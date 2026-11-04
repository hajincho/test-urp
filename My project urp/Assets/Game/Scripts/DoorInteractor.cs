using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
namespace Game.World
{
    public sealed class DoorInteractor : MonoBehaviour
    {
        public float reach=2.5f;
        public HouseDoor FocusedDoor {get;private set;}
        Text prompt;GameObject ui;
        void Awake()
        {
            ui=new GameObject("Original Door Prompt",typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler));
            var canvas=ui.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.sortingOrder=80;
            var scale=ui.GetComponent<CanvasScaler>();scale.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;scale.referenceResolution=new Vector2(1920,1080);
            var panel=new GameObject("Prompt",typeof(RectTransform),typeof(UnityEngine.UI.Image));panel.transform.SetParent(ui.transform,false);
            var rect=panel.GetComponent<RectTransform>();rect.anchorMin=rect.anchorMax=new Vector2(.5f,.18f);rect.sizeDelta=new Vector2(240,46);
            panel.GetComponent<UnityEngine.UI.Image>().color=new Color(.035f,.045f,.065f,.88f);panel.GetComponent<UnityEngine.UI.Image>().raycastTarget=false;
            var label=new GameObject("Label",typeof(RectTransform),typeof(Text));label.transform.SetParent(panel.transform,false);
            var tr=label.GetComponent<RectTransform>();tr.anchorMin=Vector2.zero;tr.anchorMax=Vector2.one;tr.offsetMin=tr.offsetMax=Vector2.zero;
            prompt=label.GetComponent<Text>();prompt.font=Font.CreateDynamicFontFromOSFont(new[]{"Malgun Gothic","Noto Sans CJK KR","Arial"},22);
            if(!prompt.font)prompt.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            prompt.fontSize=22;prompt.alignment=TextAnchor.MiddleCenter;prompt.color=new Color(1f,.95f,.81f);prompt.raycastTarget=false;panel.SetActive(false);
        }
        public HouseDoor FindDoor()
        {
            if(HouseDoor.Active.Count==0)foreach(var door in Object.FindObjectsByType<HouseDoor>(FindObjectsInactive.Exclude))if(door.isActiveAndEnabled)HouseDoor.Active.Add(door);
            HouseDoor nearest=null;float closest=reach*reach;var eye=transform.position+Vector3.up*1.1f;
            foreach(var d in HouseDoor.Active){
                if(!d||!d.isActiveAndEnabled)continue;var vector=d.InteractionPoint-eye;float distance=vector.sqrMagnitude;if(distance>=closest)continue;
                bool blocked=false;foreach(var hit in Physics.RaycastAll(eye,vector.normalized,Mathf.Max(0,vector.magnitude-.12f),~0,QueryTriggerInteraction.Ignore)){if(hit.transform.IsChildOf(transform)||hit.transform.IsChildOf(d.transform))continue;blocked=true;break;}
                if(blocked)continue;nearest=d;closest=distance;
            }
            return nearest;
        }
        void Update(){FocusedDoor=FindDoor();prompt.transform.parent.gameObject.SetActive(FocusedDoor);if(FocusedDoor)prompt.text=FocusedDoor.IsOpen?"[ F ]  문 닫기":"[ F ]  문 열기";if(Keyboard.current!=null&&Keyboard.current.fKey.wasPressedThisFrame)TryInteract();}
        public bool TryInteract(){var door=FindDoor();return door&&door.SetOpen(!door.IsOpen,transform);}
        void OnDestroy(){if(ui)Destroy(ui);}
    }
}
