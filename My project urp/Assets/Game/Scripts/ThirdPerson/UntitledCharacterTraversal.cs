using UnityEngine;

namespace Game.ThirdPerson
{
    public sealed partial class UntitledCharacterMotion
    {
        Quaternion[] traversalPose;
        Vector3 traversalPosition;
        Quaternion traversalRotation, traversalWaist;
        bool traversalWasActive;
        float traversalEnter, traversalExit;
        static AnimationCurve swimArm, swimForearm;
        void ResetTraversalPose(){traversalWasActive=false;traversalEnter=traversalExit=0;}
        void ApplyTraversalPose(float dt)
        {
            if(!motor||joints==null||joints.Length<14)return;
            bool active=motor.IsClimbing||motor.IsSwimming;
            if(traversalPose==null||traversalPose.Length!=joints.Length)traversalPose=new Quaternion[joints.Length];
            if(active){
                traversalEnter=Mathf.MoveTowards(traversalEnter,1,dt/.16f);traversalExit=1;
                if(motor.IsClimbing)PoseClimbing(traversalEnter);else PoseSwimming(traversalEnter);
                for(int i=0;i<joints.Length;i++)if(joints[i].bone)traversalPose[i]=joints[i].bone.localRotation;
                traversalPosition=transform.localPosition;traversalRotation=transform.localRotation;if(waist)traversalWaist=waist.localRotation;
            }else{
                traversalEnter=0;
                if(traversalWasActive||traversalExit>0){
                    traversalExit=Mathf.MoveTowards(traversalExit,0,dt/.22f);float weight=Mathf.SmoothStep(0,1,traversalExit);
                    transform.localPosition=Vector3.Lerp(transform.localPosition,traversalPosition,weight);transform.localRotation=Quaternion.Slerp(transform.localRotation,traversalRotation,weight);
                    if(waist)waist.localRotation=Quaternion.Slerp(waist.localRotation,traversalWaist,weight);
                    for(int i=0;i<joints.Length;i++)if(joints[i].bone)joints[i].bone.localRotation=Quaternion.Slerp(joints[i].bone.localRotation,traversalPose[i],weight);
                }
            }
            traversalWasActive=active;
        }
        void PoseClimbing(float weight)
        {
            MotionState=motor.IsMantling?"Mantle":"Climb";float phase=motor.TraversalPhase;float wave=Mathf.Sin(phase);float l=.5f+.5f*wave,r=1-l;
            float mantle=motor.IsMantling?motor.MantleProgress:0;float pull=motor.IsMantling?Mathf.Sin(mantle*Mathf.PI):0;
            transform.localPosition=Vector3.Lerp(transform.localPosition,restPosition+Vector3.back*.025f+Vector3.up*(.006f*Mathf.Sin(clock*1.2f)),weight);
            transform.localRotation=Quaternion.Slerp(transform.localRotation,restRotation*Quaternion.Euler(2+12*pull,0,wave*1.3f),weight);
            if(waist)waist.localRotation=Quaternion.Slerp(waist.localRotation,waistRest*Quaternion.AngleAxis(11+20*pull,waistPitchAxis),weight);
            BlendJumpPitch(0,3+5*pull,weight);BlendJumpPitch(1,-9-12*pull,weight);
            for(int hip=6;hip<=10;hip+=4){
                BlendJumpPitch(hip,-22-35*r-16*pull,weight);BlendJumpPitch(hip+1,-22-35*l-16*pull,weight);
                BlendJumpPitch(hip+2,48+48*r+15*pull,weight);BlendJumpPitch(hip+3,48+48*l+15*pull,weight);
            }
            float scale=Mathf.Abs(motor.transform.lossyScale.y);var cc=motor.GetComponent<CharacterController>();float height=cc.height*scale;float radius=cc.radius*Mathf.Max(Mathf.Abs(motor.transform.lossyScale.x),Mathf.Abs(motor.transform.lossyScale.z));
            ClimbHand(2,4,leftHand,l,weight,height,radius,mantle);
            ClimbHand(3,5,rightHand,r,weight,height,radius,mantle);
        }
        void ClimbHand(int upperIndex,int lowerIndex,Transform hand,float reach,float weight,float height,float radius,float mantle)
        {
            if(!hand)return;var upper=joints[upperIndex];var elbow=joints[lowerIndex].bone;if(!upper.bone||!elbow)return;
            float handHeight=Mathf.Lerp(.77f,1.02f,reach);if(motor.IsMantling)handHeight=Mathf.Lerp(.98f,.55f,mantle);
            var target=motor.transform.position+Vector3.up*(height*handHeight)-motor.WallNormal*(radius-.015f)+motor.visualRoot.right*(upper.side*height*.14f);
            if(motor.IsMantling){
                var ledge=motor.MantleHandAnchor+motor.visualRoot.right*(upper.side*height*.14f);
                target=Vector3.Lerp(ledge,target,Mathf.SmoothStep(0,1,Mathf.InverseLerp(.70f,1f,mantle)));
            }
            var pole=motor.visualRoot.right*upper.side-motor.visualRoot.up*.4f+motor.WallNormal*.2f;
            SolveTraversalArm(upper.bone,elbow,hand,target,pole,weight);
        }
        static void SolveTraversalArm(Transform upper,Transform lower,Transform hand,Vector3 target,Vector3 pole,float weight)
        {
            var from=upper.position;float a=Vector3.Distance(from,lower.position),b=Vector3.Distance(lower.position,hand.position);var delta=target-from;float d=delta.magnitude;if(a<.001f||b<.001f||d<.001f)return;
            var direction=delta/d;d=Mathf.Clamp(d,Mathf.Abs(a-b)+.005f,a+b-.005f);target=from+direction*d;
            float along=(a*a-b*b+d*d)/(2*d);float h=Mathf.Sqrt(Mathf.Max(0,a*a-along*along));var bend=Vector3.ProjectOnPlane(pole,direction).normalized;if(bend.sqrMagnitude<.5f)return;
            var knee=from+direction*along+bend*h;upper.rotation=Quaternion.Slerp(upper.rotation,Quaternion.FromToRotation(lower.position-from,knee-from)*upper.rotation,weight);
            lower.rotation=Quaternion.Slerp(lower.rotation,Quaternion.FromToRotation(hand.position-lower.position,target-lower.position)*lower.rotation,weight);
        }
        void PoseSwimming(float weight)
        {
            MotionState=motor.PlanarSpeed>.12f?"Swim":"TreadWater";
            if(swimArm==null){swimArm=MakeCycle(152,133,80,24,-12,25,105,148);swimForearm=MakeCycle(160,148,100,56,32,85,132,158);}
            float travel=Mathf.SmoothStep(0,1,Mathf.InverseLerp(.08f,motor.swimSpeed*.75f,motor.PlanarSpeed));
            float fast=Mathf.SmoothStep(0,1,Mathf.InverseLerp(motor.swimSpeed*1.06f,Mathf.Max(motor.swimSpeed*1.07f,motor.fastSwimSpeed*.93f),motor.PlanarSpeed));
            float phase=motor.TraversalPhase;float cycle=Mathf.Repeat(phase/(2*Mathf.PI),1);float height=motor.GetComponent<CharacterController>().height*Mathf.Abs(motor.transform.lossyScale.y);
            // The ordinary reference stroke keeps the head up, sweeps both arms together,
            // and leaves the legs somewhat submerged. Reserve the flatter crawl for fast swim.
            float lean=Mathf.Lerp(60f,72f,fast)*travel;
            float buoyantLift=height*Mathf.Lerp(.33f,.43f,fast)*travel;
            transform.localPosition=Vector3.Lerp(transform.localPosition,restPosition+Vector3.up*(buoyantLift+Mathf.Sin(clock*1.7f)*.014f),weight);
            float roll=Mathf.Lerp(Mathf.Sin(phase*2)*.65f,Mathf.Sin(phase)*3f,fast)*travel;
            transform.localRotation=Quaternion.Slerp(transform.localRotation,restRotation*Quaternion.Euler(lean,0,roll),weight);
            if(waist)waist.localRotation=Quaternion.Slerp(waist.localRotation,waistRest*Quaternion.AngleAxis(-4*travel,waistPitchAxis),weight);
            BlendJumpPitch(0,-5*travel,weight);BlendJumpPitch(1,-Mathf.Lerp(39f,44f,fast)*travel,weight);
            SwimArm(2,4,leftHand,cycle,travel,fast,weight);SwimArm(3,5,rightHand,cycle,travel,fast,weight);
            float kick=Mathf.Sin(motor.SwimKickPhase);
            float hipBase=Mathf.Lerp(-3f,-8f-2f*fast,travel),hipSwing=Mathf.Lerp(10f,14f+6f*fast,travel);
            float kneeBase=Mathf.Lerp(12f,12f+6f*fast,travel),kneeSwing=Mathf.Lerp(24f,26f+9f*fast,travel);
            for(int hip=6;hip<=10;hip+=4){
                BlendJumpPitch(hip,hipBase+kick*hipSwing,weight);BlendJumpPitch(hip+1,hipBase-kick*hipSwing,weight);
                BlendJumpPitch(hip+2,kneeBase+Mathf.Max(0,-kick)*kneeSwing,weight);BlendJumpPitch(hip+3,kneeBase+Mathf.Max(0,kick)*kneeSwing,weight);
            }
        }
        static Vector3 ReferenceScull(float cycle,Vector3 reach,Vector3 open,Vector3 pull,Vector3 recover)
        {
            if(cycle<.22f)return Vector3.Lerp(reach,open,Mathf.SmoothStep(0,1,cycle/.22f));
            if(cycle<.52f)return Vector3.Lerp(open,pull,Mathf.SmoothStep(0,1,(cycle-.22f)/.30f));
            if(cycle<.74f)return Vector3.Lerp(pull,recover,Mathf.SmoothStep(0,1,(cycle-.52f)/.22f));
            return Vector3.Lerp(recover,reach,Mathf.SmoothStep(0,1,(cycle-.74f)/.26f));
        }
        void SwimArm(int upperIndex,int lowerIndex,Transform hand,float cycle,float travel,float fast,float weight)
        {
            var upper=joints[upperIndex];var lower=joints[lowerIndex].bone;if(!upper.bone||!lower||!hand)return;
            float side=upper.side;
            var idleUpper=ReferenceScull(cycle,new Vector3(side*.38f,-.18f,.60f),new Vector3(side*.90f,-.15f,.20f),new Vector3(side*.70f,-.35f,.08f),new Vector3(side*.24f,-.42f,.45f));
            var idleFore=ReferenceScull(cycle,new Vector3(side*.03f,-.10f,.80f),new Vector3(side*.65f,-.10f,.40f),new Vector3(-side*.60f,.15f,.30f),new Vector3(-side*.04f,.24f,.60f));
            var strokeUpper=ReferenceScull(cycle,new Vector3(side*.17f,.81f,.37f),new Vector3(side*.70f,.40f,.26f),new Vector3(side*.62f,-.12f,.35f),new Vector3(side*.30f,.35f,.38f));
            var strokeFore=ReferenceScull(cycle,new Vector3(side*.03f,.87f,.42f),new Vector3(side*.75f,.10f,.15f),new Vector3(-side*.46f,.65f,.10f),new Vector3(-side*.18f,.74f,.45f));
            float crawlCycle=Mathf.Repeat(cycle+(side>0?.5f:0f),1f);
            float angle=swimArm.Evaluate(crawlCycle)*Mathf.Deg2Rad,fore=swimForearm.Evaluate(crawlCycle)*Mathf.Deg2Rad;
            var crawlUpper=new Vector3(side*(.18f+.26f*Mathf.Sin(crawlCycle*Mathf.PI)),-Mathf.Cos(angle),Mathf.Sin(angle));
            var crawlFore=new Vector3(side*.15f,-Mathf.Cos(fore),Mathf.Sin(fore));
            AimSegment(upper.bone,lower,Vector3.Lerp(idleUpper,Vector3.Lerp(strokeUpper,crawlUpper,fast),travel),weight);
            AimSegment(lower,hand,Vector3.Lerp(idleFore,Vector3.Lerp(strokeFore,crawlFore,fast),travel),weight);
        }
    }
}
