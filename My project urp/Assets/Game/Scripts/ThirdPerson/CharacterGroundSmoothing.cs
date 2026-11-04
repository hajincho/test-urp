using UnityEngine;

namespace Game.ThirdPerson
{
    // Smooth body/camera height before ground IK; soles can still meet the real floor.
    [DefaultExecutionOrder(170)]
    [RequireComponent(typeof(ThirdPersonMotor))]
    public sealed class CharacterGroundSmoothing : MonoBehaviour
    {
        public float smoothTime = .075f;
        public float maximumOffset = .12f;
        public float HeightOffset {get;private set;}
        ThirdPersonMotor motor;
        Transform model, cameraTarget;
        Vector3 modelRest, cameraRest, previous;
        float height, velocity;
        void Awake()
        {
            motor=GetComponent<ThirdPersonMotor>();model=motor.visualRoot;modelRest=model.localPosition;
            var follow=motor.cameraTransform?motor.cameraTransform.GetComponent<ThirdPersonFollowCamera>():null;
            cameraTarget=follow?follow.target:null;if(cameraTarget)cameraRest=cameraTarget.localPosition;
            previous=transform.position;height=previous.y;
        }
        void LateUpdate(){Step(Time.deltaTime);}
        public void Step(float dt)
        {
            if(!model||dt<=0f)return;
            var current=transform.position;float scale=Mathf.Abs(transform.lossyScale.y);
            bool reset=Vector3.Distance(previous,current)>1.5f*scale||motor.IsJumping||!motor.Grounded;
            if(reset){height=current.y;velocity=0f;HeightOffset=0f;}
            else{
                height=Mathf.SmoothDamp(height,current.y,ref velocity,smoothTime,Mathf.Infinity,Mathf.Min(dt,.05f));
                HeightOffset=Mathf.Clamp(height-current.y,-maximumOffset*scale,maximumOffset*scale);
            }
            model.localPosition=modelRest+model.parent.InverseTransformVector(Vector3.up*HeightOffset);
            if(cameraTarget)cameraTarget.localPosition=cameraRest+cameraTarget.parent.InverseTransformVector(Vector3.up*HeightOffset);
            previous=current;
        }
        void OnDisable(){if(model)model.localPosition=modelRest;if(cameraTarget)cameraTarget.localPosition=cameraRest;}
    }
}
