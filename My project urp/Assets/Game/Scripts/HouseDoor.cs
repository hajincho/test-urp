using System.Collections.Generic;
using UnityEngine;
namespace Game.World
{
    public sealed class HouseDoor : MonoBehaviour
    {
        public static readonly List<HouseDoor> Active=new List<HouseDoor>();
        public string houseName;
        public Transform leaf;
        public Collider doorCollider;
        public float width,height,swing=105f;
        public bool IsOpen {get;private set;}
        public float OpenAmount {get;private set;}
        Quaternion closed=Quaternion.identity;Rigidbody body;float direction=1;
        public Vector3 InteractionPoint=>transform.TransformPoint(new Vector3(width*.6f,height*.5f,0));
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void RegisterSceneDoors(){Active.Clear();foreach(var door in Object.FindObjectsByType<HouseDoor>(FindObjectsInactive.Exclude))if(door.isActiveAndEnabled)Active.Add(door);}
        void Awake(){closed=leaf.localRotation;body=leaf.GetComponent<Rigidbody>();}
        void OnEnable(){if(Application.isPlaying&&!Active.Contains(this))Active.Add(this);}
        void OnDisable(){Active.Remove(this);}
        public bool SetOpen(bool open,Transform actor=null)
        {
            if(!open){var center=transform.TransformPoint(new Vector3(width*.5f,height*.5f,0));foreach(var c in Physics.OverlapBox(center,new Vector3(Mathf.Abs(width)*.5f+.05f,height*.5f,.22f),transform.rotation,~0,QueryTriggerInteraction.Ignore))if(c is CharacterController)return false;}
            if(open&&!IsOpen&&OpenAmount<.01f&&actor)direction=(Vector3.Dot(actor.position-transform.position,transform.forward)>0?1:-1)*Mathf.Sign(width);
            IsOpen=open;
            foreach(var other in Active){
                if(!other||other==this||Vector3.Distance(other.InteractionPoint,InteractionPoint)>.45f||Mathf.Abs(Vector3.Dot(other.transform.forward,transform.forward))<.9f)continue;
                if(open&&actor)other.direction=(Vector3.Dot(actor.position-other.transform.position,other.transform.forward)>0?1:-1)*Mathf.Sign(other.width);
                other.IsOpen=open;
            }
            return true;
        }
        void FixedUpdate()
        {
            float target = IsOpen ? 1f : 0f;
            if (Mathf.Approximately(OpenAmount, target)) return;
            OpenAmount=Mathf.MoveTowards(OpenAmount,target,Time.fixedDeltaTime/.65f);
            var local=closed*Quaternion.Euler(0,direction*swing*Mathf.SmoothStep(0,1,OpenAmount),0);
            if(body)body.MoveRotation(transform.rotation*local);else leaf.localRotation=local;
        }
    }
}

