using UnityEngine;
using System;
using System.Collections.Generic;
namespace Game.ThirdPerson {
[DefaultExecutionOrder(150)]
public sealed class FurinaMotion : MonoBehaviour {
 public ThirdPersonMotor motor;
 sealed class Joint {public Transform bone;public Quaternion rest;public Vector3 pitch,roll;}
 Joint[] joints;Joint[] cloth;float phase,blend,run;Vector3 restPosition;
 string[] names={"Chest","Head","Left arm","Right arm","Left elbow","Right elbow","Left leg","Right leg","Left knee","Right knee"};
 public void LowerArms(){foreach(var t in GetComponentsInChildren<Transform>(true))if((t.name=="Left arm"||t.name=="Right arm")&&t.childCount>0){var d=t.GetChild(0).position-t.position;t.rotation=Quaternion.FromToRotation(d,-transform.up)*t.rotation;}}
 Joint Make(Transform t){return new Joint{bone=t,rest=t.localRotation,pitch=t.InverseTransformDirection(transform.right),roll=t.InverseTransformDirection(transform.forward)};}
 void Awake(){if(!motor)motor=GetComponentInParent<ThirdPersonMotor>();LowerArms();var all=GetComponentsInChildren<Transform>(true);joints=new Joint[names.Length];for(int i=0;i<names.Length;i++){var t=Array.Find(all,x=>x.name==names[i]);if(t)joints[i]=Make(t);}var secondary=new List<Joint>();foreach(var t in all)if(t.name=="F_0_1_L"||t.name=="F_6_1_L"||t.name=="F_12_1_L"||t.name=="F_0_1_R"||t.name=="F_6_1_R"||t.name=="F_12_1_R"||(t.name.StartsWith("HB")&&!t.parent.name.StartsWith("HB")))secondary.Add(Make(t));cloth=secondary.ToArray();restPosition=transform.localPosition;}
 void Pose(int i,float pitch,float roll=0){var j=joints[i];if(j!=null&&j.bone)j.bone.localRotation=j.rest*Quaternion.AngleAxis(roll,j.roll)*Quaternion.AngleAxis(pitch,j.pitch);}
 void LateUpdate(){if(!motor||joints==null)return;float dt=Time.deltaTime;blend=Mathf.Lerp(blend,Mathf.Clamp01(motor.PlanarSpeed/1.2f),1-Mathf.Exp(-9*dt));run=Mathf.Lerp(run,Mathf.InverseLerp(motor.walkSpeed,motor.sprintSpeed,motor.PlanarSpeed),1-Mathf.Exp(-7*dt));phase+=dt*Mathf.Lerp(6.5f,10.5f,run)*blend;float wave=Mathf.Sin(phase);float air=motor.Grounded?0:1;float stride=Mathf.Lerp(23,37,run)*blend*(1-air);float knee=Mathf.Lerp(30,58,run)*blend*(1-air);float arms=Mathf.Lerp(15,32,run)*blend;Pose(0,2*run*blend+Mathf.Sin(Time.time*1.7f)*.5f);Pose(1,-2*run*blend);Pose(2,-wave*arms-15*air);Pose(3,wave*arms-15*air);Pose(4,-12-32*run*blend);Pose(5,-12-32*run*blend);Pose(6,wave*stride-18*air);Pose(7,-wave*stride-12*air);Pose(8,Mathf.Max(0,-wave)*knee+28*air);Pose(9,Mathf.Max(0,wave)*knee+20*air);transform.localPosition=restPosition+Vector3.up*(Mathf.Abs(wave)*.018f*blend);for(int i=0;i<cloth.Length;i++){var j=cloth[i];if(j.bone)j.bone.localRotation=j.rest*Quaternion.AngleAxis(Mathf.Sin(Time.time*1.8f+i*.7f)*(1+3*run),j.pitch);}}
}
}
