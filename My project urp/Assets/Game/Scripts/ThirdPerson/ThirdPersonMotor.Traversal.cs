using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.ThirdPerson
{
    public partial class ThirdPersonMotor
    {
        public enum LocomotionMode { Grounded, Airborne, Climbing, Swimming }
        public LocomotionMode Mode { get; private set; } = LocomotionMode.Airborne;
        public bool IsClimbing => Mode == LocomotionMode.Climbing;
        public bool IsSwimming => Mode == LocomotionMode.Swimming;
        public bool IsMantling => mantleActive;
        public float MantleProgress => mantleActive ? Mathf.Clamp01(mantleTime / .42f) : 0f;
        public Vector3 MantleHandAnchor => new Vector3(mantleFrom.x,mantleTo.y+BottomOffset-.045f,mantleFrom.z)-wallNormal*(Radius+.09f);
        public float SwimKickPhase { get; private set; }
        public float TraversalPhase { get; private set; }
        public float ModeTime { get; private set; }
        public float WaterSurfaceHeight { get; private set; } = float.NegativeInfinity;
        public Vector3 WallNormal => wallNormal;
        public Vector2 TraversalInput { get; private set; }
        public Vector3 PlanarVelocity => planarVelocity;
        public Vector3 Acceleration { get; private set; }
        public bool IsDashing => dashTime > 0f;
        public float DashBlend => dashTime <= 0 ? 0 : Mathf.SmoothStep(0,1,(.95f-dashTime)/.08f)*Mathf.SmoothStep(0,1,dashTime/.25f);
        public float climbSpeed = 1.25f, swimSpeed = 2.1f, fastSwimSpeed = 3.5f;
        public bool enableClimbing = true, enableSwimming = true;
        public bool useWaterOverride;
        public float waterOverrideHeight;
        public bool SimulatedDrop { get; set; }
        public bool SimulatedDash { get; set; }
        Game.World.WorldMapController worldMap;
        readonly RaycastHit[] wallHits = new RaycastHit[24];
        readonly Collider[] ledgeHits = new Collider[32];
        Vector3 wallNormal = Vector3.back, measuredVelocity, mantleFrom, mantleTo;
        float dashTime, climbCooldown, waterCooldown, climbBurst, swimVerticalVelocity, mantleTime;
        float previousShiftPress = -10f;
        bool dropPressed, mantleActive;

        void CacheTraversal() { worldMap = GetComponent<Game.World.WorldMapController>(); }
        void ResetTraversal()
        {
            SetMode(LocomotionMode.Airborne); ModeTime=0; TraversalPhase=0; SwimKickPhase=0;
            mantleActive=false; mantleTime=0; climbCooldown=.18f; waterCooldown=0;
            climbBurst=dashTime=swimVerticalVelocity=0; measuredVelocity=Acceleration=Vector3.zero;
        }
        void SetMode(LocomotionMode value) { if(Mode!=value){Mode=value;ModeTime=0;} }
        void ReadTraversalInput(Keyboard keyboard,Vector2 input)
        {
            dropPressed=keyboard!=null&&keyboard.leftCtrlKey.wasPressedThisFrame;
            if(keyboard!=null&&keyboard.leftShiftKey.wasPressedThisFrame){
                if(Time.time-previousShiftPress<.32f&&input.sqrMagnitude>.01f)RequestDash();
                previousShiftPress=Time.time;
            }
            if(Mouse.current!=null&&Mouse.current.rightButton.wasPressedThisFrame&&keyboard!=null&&keyboard.leftShiftKey.isPressed)RequestDash();
        }
        public void RequestDash() { if(!IsClimbing&&!IsSwimming)dashTime=.95f; }
        void AdvanceTraversalTimers(float dt)
        {
            ModeTime+=dt; dashTime=Mathf.Max(0,dashTime-dt);climbCooldown=Mathf.Max(0,climbCooldown-dt);waterCooldown=Mathf.Max(0,waterCooldown-dt);climbBurst=Mathf.Max(0,climbBurst-dt);
            if(SimulationInput){dropPressed=SimulatedDrop;SimulatedDrop=false;if(SimulatedDash){RequestDash();SimulatedDash=false;}}
        }
        float SurfaceWater(Vector3 position)
        {
            if(useWaterOverride)return waterOverrideHeight;
            if(!worldMap)CacheTraversal();
            return worldMap?worldMap.GetWaterHeight(position):float.NegativeInfinity;
        }
        float Height => controller.height*Mathf.Abs(transform.lossyScale.y);
        float Radius => controller.radius*Mathf.Max(Mathf.Abs(transform.lossyScale.x),Mathf.Abs(transform.lossyScale.z));
        float BottomOffset => transform.TransformVector(controller.center).y-Height*.5f;
        bool TickTraversal(Vector2 input,bool sprint,bool jump,float dt)
        {
            TraversalInput=input;WaterSurfaceHeight=SurfaceWater(transform.position);
            float foot=transform.position.y+BottomOffset;
            if(IsClimbing){
                if(dropPressed){EndClimb();dropPressed=false;return false;}
                if(mantleActive){TickMantle(dt);return true;}
                if(jump)climbBurst=.25f;
                TickClimb(input,sprint,dt);return IsClimbing;
            }
            if(IsSwimming){
                if(jump){SetMode(LocomotionMode.Airborne);waterCooldown=.70f;verticalSpeed=4.8f;launchSpeed=verticalSpeed;IsJumping=true;Grounded=false;return false;}
                if(!enableSwimming||float.IsNegativeInfinity(WaterSurfaceHeight)||foot>WaterSurfaceHeight-Height*.32f){SetMode(LocomotionMode.Airborne);verticalSpeed=0;waterCooldown=.15f;return false;}
                TickSwim(input,sprint,dt);return true;
            }
            if(enableSwimming&&waterCooldown<=0&&WaterSurfaceHeight-foot>Height*.46f){
                SetMode(LocomotionMode.Swimming);Grounded=IsJumping=Sprinting=false;CurrentTransition=Transition.None;dashTime=0;swimVerticalVelocity=verticalSpeed=0;TickSwim(input,sprint,dt);return true;
            }
            if(enableClimbing&&climbCooldown<=0&&(input.y>.15f||jump)){
                float yaw=cameraTransform?cameraTransform.eulerAngles.y:(visualRoot?visualRoot.eulerAngles.y:transform.eulerAngles.y);
                var forward=Quaternion.Euler(0,yaw,0)*new Vector3(input.x,0,Mathf.Max(.3f,input.y));forward.Normalize();
                if(FindWall(forward,out var wall)){
                    wallNormal=wall.normal;SetMode(LocomotionMode.Climbing);Grounded=IsJumping=Sprinting=false;verticalSpeed=0;CurrentTransition=Transition.None;planarVelocity=Vector3.zero;dashTime=0;TickClimb(input,sprint,dt);return true;
                }
            }
            dropPressed=false;return false;
        }
        bool TraversalIgnored(Collider collider)
        {
            if(!collider||collider.transform.IsChildOf(transform)||collider is CharacterController||collider.GetComponentInParent<Game.World.HouseDoor>())return true;
            string name=collider.name.ToLowerInvariant();
            return name.Contains("tree")||name.Contains("bush")||name.Contains("leaf")||name.Contains("scatter")||name.Contains("water")||name.Contains("river")||name.Contains("lake")||name.Contains("fog");
        }
        bool FindWall(Vector3 forward,out RaycastHit result)
        {
            var origin=transform.position+Vector3.up*(BottomOffset+Height*.56f);float probe=Mathf.Max(.07f,Radius*.58f);
            int count=Physics.SphereCastNonAlloc(origin,probe,forward.normalized,wallHits,Radius+.65f,groundMask,QueryTriggerInteraction.Ignore);
            result=default;float nearest=float.PositiveInfinity;
            for(int i=0;i<count;i++){var h=wallHits[i];if(TraversalIgnored(h.collider)||Mathf.Abs(h.normal.y)>.40f||Vector3.Dot(h.normal,forward)>-.25f||h.distance>=nearest)continue;nearest=h.distance;result=h;}
            return result.collider;
        }
        void EndClimb()
        {
            mantleActive=false;SetMode(LocomotionMode.Airborne);climbCooldown=.45f;verticalSpeed=-.5f;Grounded=IsJumping=false;planarVelocity=wallNormal*.65f;PlanarSpeed=planarVelocity.magnitude;
        }
        void TickClimb(Vector2 input,bool sprint,float dt)
        {
            var before=transform.position;
            if(input.y>.1f&&TryStartMantle()){TickMantle(dt);return;}
            if(!FindWall(-wallNormal,out var hit)){EndClimb();return;}
            wallNormal=Vector3.Slerp(wallNormal,hit.normal,1-Mathf.Exp(-12*dt)).normalized;
            var right=Vector3.Cross(Vector3.up,-wallNormal).normalized;
            float speed=climbBurst>0?4f:climbSpeed*(sprint?1.65f:1f);
            var desired=(right*input.x+Vector3.up*input.y)*speed;
            if(climbBurst>0)desired+=Vector3.up*2f;
            var origin=transform.position+Vector3.up*(BottomOffset+Height*.56f);
            float gap=Vector3.Dot(origin-hit.point,wallNormal);float stick=Mathf.Clamp(Radius+.015f-gap,-.10f,.10f);
            controller.Move(desired*dt+wallNormal*stick);
            var actual=transform.position-before;planarVelocity=new Vector3(actual.x,0,actual.z)/dt;PlanarSpeed=new Vector2(actual.x,actual.z).magnitude/dt;verticalSpeed=actual.y/dt;
            TraversalPhase=Mathf.Repeat(TraversalPhase+actual.magnitude*(2*Mathf.PI/.9f),2*Mathf.PI);
            if(visualRoot)visualRoot.rotation=Quaternion.Slerp(visualRoot.rotation,Quaternion.LookRotation(-wallNormal),1-Mathf.Exp(-14*dt));
            Grounded=IsJumping=Sprinting=false;UphillAngle=0;TrackTraversalVelocity(before,dt);
            if(input.y<-.1f&&ProbeSupport(out _,out float gapBelow)&&gapBelow<.10f){EndClimb();verticalSpeed=-2;}
        }
        bool TryStartMantle()
        {
            var forward=-wallNormal;var origin=transform.position+Vector3.up*(BottomOffset+Height+1f)+forward*(Radius+.45f);
            var hits=Physics.RaycastAll(origin,Vector3.down,Height+1.1f,groundMask,QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits,(a,b)=>a.distance.CompareTo(b.distance));
            foreach(var hit in hits){
                if(TraversalIgnored(hit.collider)||hit.normal.y<.75f)continue;
                float rise=hit.point.y-(transform.position.y+BottomOffset);if(rise<.16f||rise>Height*.88f)continue;
                var destination=new Vector3(origin.x,hit.point.y+.045f-BottomOffset,origin.z);
                float half=Mathf.Max(0,Height*.5f-Radius);var center=destination+transform.TransformVector(controller.center);
                int count=Physics.OverlapCapsuleNonAlloc(center-Vector3.up*half,center+Vector3.up*half,Radius*.9f,ledgeHits,groundMask,QueryTriggerInteraction.Ignore);bool blocked=count==ledgeHits.Length;
                for(int i=0;i<count;i++)if(ledgeHits[i]&&!ledgeHits[i].transform.IsChildOf(transform)){blocked=true;break;}
                if(blocked)continue;
                mantleActive=true;mantleTime=0;mantleFrom=transform.position;mantleTo=destination;return true;
            }
            return false;
        }
        void TickMantle(float dt)
        {
            var before=transform.position;mantleTime+=dt;float t=MantleProgress;float vertical=Mathf.SmoothStep(0,1,Mathf.Clamp01(t/.60f));float across=Mathf.SmoothStep(0,1,Mathf.InverseLerp(.54f,1f,t));
            var next=new Vector3(Mathf.Lerp(mantleFrom.x,mantleTo.x,across),Mathf.Lerp(mantleFrom.y,mantleTo.y,vertical)+.08f*Mathf.Sin(t*Mathf.PI),Mathf.Lerp(mantleFrom.z,mantleTo.z,across));
            controller.Move(next-transform.position);verticalSpeed=(transform.position.y-before.y)/dt;PlanarSpeed=new Vector2(transform.position.x-before.x,transform.position.z-before.z).magnitude/dt;TrackTraversalVelocity(before,dt);
            if(t>=1&&Vector3.Distance(transform.position,mantleTo)<.15f){mantleActive=false;climbCooldown=.4f;SetMode(LocomotionMode.Airborne);verticalSpeed=-2;Grounded=controller.isGrounded;}
            else if(mantleTime>.70f)EndClimb();
        }
        void TickSwim(Vector2 input,bool sprint,float dt)
        {
            var before=transform.position;float yaw=cameraTransform?cameraTransform.eulerAngles.y:0;var direction=Quaternion.Euler(0,yaw,0)*new Vector3(input.x,0,input.y);
            float speed=sprint?fastSwimSpeed:swimSpeed;planarVelocity=Vector3.MoveTowards(planarVelocity,direction*speed,speed*dt/.30f);
            float desiredY=WaterSurfaceHeight-Height*.58f-BottomOffset;
            float nextY=Mathf.SmoothDamp(transform.position.y,desiredY,ref swimVerticalVelocity,.16f,6f,dt);
            var flags=controller.Move(planarVelocity*dt+Vector3.up*(nextY-transform.position.y));
            var delta=transform.position-before;PlanarSpeed=Mathf.Lerp(PlanarSpeed,new Vector2(delta.x,delta.z).magnitude/dt,1-Mathf.Exp(-10*dt));verticalSpeed=delta.y/dt;
            float strokeRate=Mathf.Lerp(.56f,.64f,Mathf.Clamp01(PlanarSpeed/Mathf.Max(.1f,swimSpeed)));
            strokeRate=Mathf.Lerp(strokeRate,1.05f,Mathf.InverseLerp(swimSpeed,fastSwimSpeed,PlanarSpeed));
            float kickRate = Mathf.Lerp(.95f, 1.92f, Mathf.Clamp01(PlanarSpeed / Mathf.Max(.1f, swimSpeed)));
            kickRate = Mathf.Lerp(kickRate, 2.1f, Mathf.InverseLerp(swimSpeed, fastSwimSpeed, PlanarSpeed));
            SwimKickPhase = Mathf.Repeat(SwimKickPhase + dt * kickRate * 2f * Mathf.PI, 2f * Mathf.PI);
            TraversalPhase=Mathf.Repeat(TraversalPhase+dt*strokeRate*2*Mathf.PI,2*Mathf.PI);
            if(direction.sqrMagnitude>.01f&&visualRoot)visualRoot.rotation=Quaternion.Slerp(visualRoot.rotation,Quaternion.LookRotation(direction),1-Mathf.Exp(-5*dt));
            Grounded=IsJumping=Sprinting=false;UphillAngle=0;CurrentTransition=Transition.None;TrackTraversalVelocity(before,dt);
            if((flags&CollisionFlags.Below)!=0&&WaterSurfaceHeight-(transform.position.y+BottomOffset)<Height*.39f){SetMode(LocomotionMode.Grounded);Grounded=true;verticalSpeed=-2;waterCooldown=.18f;}
        }
        void TrackTraversalVelocity(Vector3 before,float dt)
        {
            var velocity=(transform.position-before)/Mathf.Max(dt,.001f);
            var acceleration=(velocity-measuredVelocity)/Mathf.Max(dt,.001f);Acceleration=Vector3.Lerp(Acceleration,Vector3.ClampMagnitude(acceleration,60),1-Mathf.Exp(-10*dt));measuredVelocity=velocity;
        }
    }
}
