using System.Collections.Generic;
using UnityEngine;

namespace Game.ThirdPerson
{
    // Read the final pose after locomotion, ground IK and secondary motion have finished.
    [DefaultExecutionOrder(195)]
    public sealed class BlenderCharacterRetarget : MonoBehaviour
    {
        struct Link { public Transform from, to; public Quaternion offset; }
        struct Detail
        {
            public Transform from, to;
            public Quaternion fromRest, toRest, basis;
            public float share;
            public bool cape, braid, rearCapeRoot;
            public Vector3 rearCapeAxis;
            public float capePhase, capeIdleAmount;
        }
        struct Leg
        {
            public Transform hip, knee, foot, sourceFoot;
            public Vector3 pivotOffset;
        }
        readonly List<Link> pose = new List<Link>();
        readonly List<Detail> details = new List<Detail>();
        readonly Dictionary<string, Transform> source = new Dictionary<string, Transform>();
        readonly Dictionary<string, Transform> target = new Dictionary<string, Transform>();
        Transform root, pelvis, sourcePelvis;
        Vector3 hipOffset;
        Leg left, right;
        SkinnedMeshRenderer eyes, sourceEyes;
        int blink, sourceBlink;
        UntitledCharacterMotion motionSource;
        // The alternate model already has 18 skinned joints in each braid.
        readonly Transform[][] braids = { new Transform[18], new Transform[18] };
        readonly Vector3[][] braidPrevious = { new Vector3[18], new Vector3[18] };
        readonly Vector3[][] braidVelocity = { new Vector3[18], new Vector3[18] };
        bool braidReady;
        Vector3 braidRootPrevious;
        Transform chest;
        ThirdPersonMotor motor;
        SilkClothDynamics garmentPhysics;
        float braidRunBlend, capeRunBlend, capeIdleBlend;
        const int CurlSegments = 5;
        readonly Transform[,] curlTips = new Transform[2, CurlSegments];
        readonly float[,] curlAngles = new float[2, CurlSegments];
        readonly float[,] curlVelocities = new float[2, CurlSegments];
        Mesh curlMesh;
        SkinnedMeshRenderer curlSkin;
        Mesh originalCurlMesh;
        Transform[] originalCurlBones;
        Vector3 previousActorPosition, actorVelocity, localAcceleration;
        float previousActorYaw, turnVelocity, swimBlend, climbBlend;
        float waterFloat, waterFloatVelocity;
        bool referenceMotionReady;

        sealed class SilkFold
        {
            public Transform bone;
            public Quaternion rest;
            public Vector3 pitchAxis, twistAxis;
            public float length, lane, bend, velocity;
        }
        readonly List<SilkFold> silkFolds = new List<SilkFold>();

        void CacheSilkFolds()
        {
            for (int panel = 2; panel <= 4; panel++)
            {
                for (int index = 1; index < 18; index++)
                {
                    // The mapped cloth anchors already drive joints 0, 3 and 8.
                    if (index == 3 || index == 8) continue;
                    if (!target.TryGetValue($"HB{panel}_{index}_1", out var bone) || bone.childCount == 0) continue;
                    silkFolds.Add(new SilkFold {
                        bone = bone, rest = bone.localRotation,
                        pitchAxis = bone.InverseTransformDirection(root.right).normalized,
                        twistAxis = bone.InverseTransformDirection(root.forward).normalized,
                        length = index / (panel == 4 ? 16f : 18f), lane = panel - 3
                    });
                }
            }
        }

        void FollowSilkFolds()
        {
            float phase = motionSource.GaitPhase;
            float dt = Mathf.Min(Time.deltaTime, .05f);
            if (dt <= 0f) return;
            foreach (var fold in silkFolds)
            {
                float length = fold.length;
                float wave = Mathf.Sin(2f * phase - length * 3.8f - fold.lane * .17f);
                float stride = Mathf.Sin(phase - length * 2.2f);
                float response = Mathf.SmoothStep(.25f, 1f, length);
                float load = .9f + response * (2.9f * wave + 1.0f * stride);
                load += Mathf.Clamp(localAcceleration.z * .10f, -.8f, .8f) * response;
                fold.bend = Mathf.SmoothDamp(fold.bend, capeRunBlend * load, ref fold.velocity,
                    Mathf.Lerp(.045f, .10f, length), Mathf.Infinity, dt);
                float twist = capeRunBlend * response * Mathf.Clamp(turnVelocity * .006f + fold.lane * stride * .55f, -1.6f, 1.6f);
                fold.bone.localRotation = fold.rest * Quaternion.AngleAxis(fold.bend, fold.pitchAxis)
                    * Quaternion.AngleAxis(twist, fold.twistAxis);
            }
        }

        void Awake()
        {
            garmentPhysics = GetComponent<SilkClothDynamics>();
            motor = GetComponentInParent<ThirdPersonMotor>();
            var original = motor ? motor.GetComponentInChildren<UntitledCharacterMotion>(true) : null;
            if (!original) { enabled = false; return; }
            motionSource = original;
            root = motor.visualRoot;
            foreach (var t in original.GetComponentsInChildren<Transform>(true)) source[t.name] = t;
            foreach (var t in GetComponentsInChildren<Transform>(true)) target[t.name] = t;
            sourcePelvis = source["Bone_006"]; pelvis = target["Hips"]; chest = target["Chest"];
            hipOffset = root.InverseTransformVector(pelvis.position - sourcePelvis.position);
            Bind("Bone_006","Hips");
            Bind("Bone_163","Spine","Chest","Chest");
            Bind("Chest","Chest","Bone_166","Neck");
            Bind("Bone_166","Neck","Head","Head");
            Bind("Head","Head");
            left = BindSide(true); right = BindSide(false);
            // Split long locks over several hinges so their combined swing follows the short source chain.
            for (int side=0; side<2; side++)
            {
                string suffix=side==0?"_L":"_R"; int hair=side==0?175:181;
                for(int i=0;i<18;i++) {
                    string name = "F_"+i+"_1"+suffix;
                    target.TryGetValue(name, out braids[side][i]);
                    Secondary(Bone(hair+i/6),name,1f/6f);
                }
                Secondary(Bone(side==0?217:226),"CF_1"+suffix,1);
                Secondary(Bone(side==0?218:227),"CF_2"+suffix,1);
            }
            Secondary(Bone(201),"帽D1_1",1); Secondary(Bone(202),"帽D1_2",1);
            Secondary(Bone(235),"领巾",1); Secondary(Bone(29),"SZYParent",.65f);
            Secondary(Bone(31),"SZY_J1_1",1); Secondary(Bone(32),"SZY_J1_2",1);
            Secondary(Bone(33),"SZY_S_1",.8f);
            int[] panels={130,119,108,97,86,86,75,64,53};
            for(int panel=0;panel<panels.Length;panel++)
            {
                int first=(panel==1||panel==7)?2:1;
                Secondary(Bone(panels[panel]),"HB1_"+first+"_"+panel,.8f);
                Secondary(Bone(panels[panel]+1),"HB1_3_"+panel,.65f);
            }
            for(int i=2;i<=4;i++) {
                Secondary(Bone(86),"HB"+i+"_0_1",.85f);
                Secondary(Bone(87),"HB"+i+"_3_1",.8f);
                Secondary(Bone(92),"HB"+i+"_8_1",.8f);
            }
            CacheSilkFolds();
            SetupCurlTips();
            eyes = FindEyes(transform,out blink);
            sourceEyes = FindEyes(original.transform,out sourceBlink);
        }

        static string Bone(int i) => "Bone_"+i.ToString("D3");
        static SkinnedMeshRenderer FindEyes(Transform obj,out int index)
        {
            foreach(var part in obj.GetComponentsInChildren<SkinnedMeshRenderer>(true)){
                index=part.sharedMesh?part.sharedMesh.GetBlendShapeIndex("IdleBlink"):-1;
                if(index>=0)return part;
            }
            index=-1; return null;
        }
        Leg BindSide(bool isLeft)
        {
            string side=isLeft?"Left ":"Right ", s=isLeft?"_L":"_R";
            var shoulder=Bone(isLeft?238:276); var wrist=Bone(isLeft?244:282);
            Bind(shoulder,side+"shoulder","UpperArm"+s,side+"arm");
            Bind("UpperArm"+s,side+"arm","Forearm"+s,side+"elbow");
            Bind("Forearm"+s,side+"elbow",wrist,side+"wrist");
            Bind(wrist,side+"wrist",Bone(isLeft?254:292),"MiddleFinger1"+s);
            string[] digits={"Thumb","IndexFinger","MiddleFinger","RingFinger","LittleFinger"};
            for(int d=0;d<5;d++)
                for(int j=0;j<3;j++)
                    Secondary(Bone((isLeft?246:284)+d*4+j),digits[d]+(j+(d==0?0:1))+s,1);
            string foot=Bone(isLeft?15:25),toe=Bone(isLeft?16:26);
            Bind("ThighAux"+s,side+"leg","ShinAux"+s,side+"knee");
            Bind("ShinAux"+s,side+"knee",foot,side+"ankle");
            Bind(foot,side+"ankle");
            Secondary(toe,side+"toe",1);
            var f=target[side+"ankle"];var sf=source[foot];
            return new Leg { hip=target[side+"leg"], knee=target[side+"knee"], foot=f, sourceFoot=sf,
                pivotOffset=Quaternion.Inverse(sf.rotation)*(f.position-sf.position) };
        }
        void Bind(string a,string b,string aEnd=null,string bEnd=null)
        {
            var from=source[a];var to=target[b];var align=Quaternion.identity;
            if(aEnd!=null)align=Quaternion.FromToRotation(target[bEnd].position-to.position,source[aEnd].position-from.position);
            pose.Add(new Link {from=from,to=to,offset=Quaternion.Inverse(from.rotation)*align*to.rotation});
        }
        void Secondary(string a,string b,float share)
        {
            if(!source.TryGetValue(a,out var from)||!target.TryGetValue(b,out var to))return;
            details.Add(new Detail {from=from,to=to,fromRest=from.localRotation,toRest=to.localRotation,
                basis=Quaternion.Inverse(to.rotation)*from.rotation,share=share,
                cape=b.StartsWith("HB", System.StringComparison.Ordinal),
                braid=b.StartsWith("F_", System.StringComparison.Ordinal),
                rearCapeRoot=b.StartsWith("HB", System.StringComparison.Ordinal) && to.parent == pelvis &&
                    root.InverseTransformPoint(to.position).z < root.InverseTransformPoint(pelvis.position).z - .02f,
                rearCapeAxis=to.InverseTransformDirection(root.right).normalized,
                capePhase=root.InverseTransformPoint(to.position).x * 8f + root.InverseTransformPoint(to.position).z * 4f,
                capeIdleAmount=to.parent == pelvis ? .20f : .85f});
        }
        void LateUpdate()
        {
            if (!root || !pelvis || !sourcePelvis || !motionSource) return;
            SampleReferenceForces();
            pelvis.position=sourcePelvis.position+root.TransformVector(hipOffset);
            foreach(var p in pose)p.to.rotation=p.from.rotation*p.offset;
            float targetRun = motor && !motor.IsClimbing && !motor.IsSwimming ? Mathf.InverseLerp(motor.walkSpeed, motor.sprintSpeed, motor.PlanarSpeed) : 0f;
            // Keep the hair's response continuous as the body brakes sharply.
            float response = targetRun < braidRunBlend ? 2.2f : 4f;
            braidRunBlend = Mathf.Lerp(braidRunBlend, targetRun, 1f - Mathf.Exp(-response * Time.deltaTime));
            float run = braidRunBlend;
            float capeTarget = motor && motor.Grounded && !motor.IsJumping ? targetRun : 0f;
            capeRunBlend = Mathf.Lerp(capeRunBlend, capeTarget, 1f - Mathf.Exp(-3f * Time.deltaTime));
            float idleTarget = motor && motor.Grounded && !motor.IsJumping
                ? 1f - Mathf.InverseLerp(.08f, Mathf.Max(.1f, motor.walkSpeed * .55f), motor.PlanarSpeed) : 0f;
            capeIdleBlend = Mathf.Lerp(capeIdleBlend, idleTarget, 1f - Mathf.Exp(-2.5f * Time.deltaTime));
            bool braking = motor && (motor.CurrentTransition == ThirdPersonMotor.Transition.SprintStop ||
                                     motor.CurrentTransition == ThirdPersonMotor.Transition.WalkStop ||
                                     motor.CurrentTransition == ThirdPersonMotor.Transition.AirStop);
            foreach(var d in details){
                if (d.cape && garmentPhysics && garmentPhysics.OwnsCloth) continue;
                var delta=d.basis*(Quaternion.Inverse(d.fromRest)*d.from.localRotation)*Quaternion.Inverse(d.basis);
                float braidShare = Mathf.Max(Mathf.Lerp(.045f, .30f, run), .28f * climbBlend);
                braidShare = Mathf.Lerp(braidShare, .72f, waterFloat);
                float dryCapeShare = d.capeIdleAmount > .5f ? Mathf.Lerp(.72f, .88f, run) : .6f;
                float capeShare = Mathf.Lerp(dryCapeShare, d.capeIdleAmount > .5f ? 1f : .6f, waterFloat);
                var desired = d.toRest * Quaternion.SlerpUnclamped(Quaternion.identity, delta, d.share * (d.cape ? capeShare : d.braid ? braidShare : 1f));
                // Tilt the rear waist panels back gently during a run, then settle with inertia.
                if (d.rearCapeRoot) desired *= Quaternion.AngleAxis(30f * capeRunBlend, d.rearCapeAxis);
                if (d.cape && capeIdleBlend > .001f) {
                    float breeze = Mathf.Sin(Time.time * .72f + d.capePhase)
                        + .25f * Mathf.Sin(Time.time * 1.17f + d.capePhase * 1.4f);
                    desired *= Quaternion.AngleAxis(breeze * d.capeIdleAmount * capeIdleBlend, d.rearCapeAxis);
                }
                float rate = d.cape ? (braking ? 4.6f : 7.5f) : d.braid ? (braking ? 3.5f : 9f) : 0f;
                d.to.localRotation = rate > 0f
                    ? Quaternion.Slerp(d.to.localRotation, desired, 1f - Mathf.Exp(-rate * Time.deltaTime))
                    : desired;
            }
            if (!garmentPhysics || !garmentPhysics.OwnsCloth) FollowSilkFolds();
            FitLeg(left); FitLeg(right);
            UpdateBraids();
            UpdateCurlTips();
            if(eyes)eyes.SetBlendShapeWeight(blink,sourceEyes ? sourceEyes.GetBlendShapeWeight(sourceBlink) : motionSource.BlinkWeight);
        }
        // Add five skinned hinges to each curled tip. Only the existing tip's
        // influence is redistributed; all other body weights stay unchanged.
        void SetupCurlTips()
        {
            if (curlMesh && curlSkin && curlSkin.sharedMesh == curlMesh) return;
            SkinnedMeshRenderer skin = null;
            foreach (var candidate in GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (System.Array.IndexOf(candidate.bones, braids[0][17]) >= 0 &&
                    System.Array.IndexOf(candidate.bones, braids[1][17]) >= 0) { skin = candidate; break; }
            if (!skin || !skin.sharedMesh || !skin.sharedMesh.isReadable) return;
            var sourceMesh = skin.sharedMesh;
            var oldBones = skin.bones;
            originalCurlMesh = sourceMesh; originalCurlBones = oldBones; curlSkin = skin;
            var bones = new Transform[oldBones.Length + 2 * CurlSegments];
            var bind = new Matrix4x4[bones.Length];
            System.Array.Copy(oldBones, bones, oldBones.Length);
            System.Array.Copy(sourceMesh.bindposes, bind, oldBones.Length);
            int[] tipIndex = new int[2];
            var vertices = sourceMesh.vertices;
            var weights = sourceMesh.boneWeights;
            float[] curlStep = new float[2], curlDirection = new float[2];
            for(int side=0;side<2;side++){
                tipIndex[side]=System.Array.IndexOf(oldBones,braids[side][17]);if(tipIndex[side]<0)return;
                float low=0f,high=0f;
                for(int i=0;i<weights.Length;i++){
                    var w=weights[i];bool affects=(w.boneIndex0==tipIndex[side]&&w.weight0>.0001f)||(w.boneIndex1==tipIndex[side]&&w.weight1>.0001f)||(w.boneIndex2==tipIndex[side]&&w.weight2>.0001f)||(w.boneIndex3==tipIndex[side]&&w.weight3>.0001f);
                    if(!affects)continue;float y=bind[tipIndex[side]].MultiplyPoint3x4(vertices[i]).y;low=Mathf.Min(low,y);high=Mathf.Max(high,y);
                }
                // Use the actual weighted curl extent, rather than adding unweighted hinges
                // beyond the end of a shorter mesh. Keep the original positive-Y convention
                // whenever that end has useful length; mirrored/negative-axis rigs also work.
                curlDirection[side]=high>.006f?1f:-1f;float extent=curlDirection[side]>0f?high:-low;
                if(extent<.002f)return;curlStep[side]=extent*.92f/CurlSegments;
            }
            for (int side=0; side<2; side++) {
                var tip = braids[side][17];
                Transform parent = tip;
                for (int j=0; j<CurlSegments; j++) {
                    string name = "ReferenceCurlTip_" + side + "_" + j;
                    var added = parent.Find(name);
                    if (!added) { added = new GameObject(name).transform; added.SetParent(parent, false); }
                    added.localPosition = Vector3.up * (curlDirection[side]*curlStep[side]); added.localRotation = Quaternion.identity; added.localScale = Vector3.one;
                    curlTips[side,j] = added;
                    int index = oldBones.Length + side*CurlSegments + j;
                    bones[index] = added;
                    float offset = (j + 1) * curlDirection[side]*curlStep[side];
                    bind[index] = Matrix4x4.Translate(Vector3.down * offset) * bind[tipIndex[side]];
                    parent = added;
                }
            }
            for (int i=0; i<weights.Length; i++) {
                var w = weights[i];
                bool affected = (w.weight0>0f&&(w.boneIndex0==tipIndex[0]||w.boneIndex0==tipIndex[1]))
                    || (w.weight1>0f&&(w.boneIndex1==tipIndex[0]||w.boneIndex1==tipIndex[1]))
                    || (w.weight2>0f&&(w.boneIndex2==tipIndex[0]||w.boneIndex2==tipIndex[1]))
                    || (w.weight3>0f&&(w.boneIndex3==tipIndex[0]||w.boneIndex3==tipIndex[1]));
                if (!affected) continue;
                var list = new List<KeyValuePair<int,float>>(6);
                int[] indices = { w.boneIndex0, w.boneIndex1, w.boneIndex2, w.boneIndex3 };
                float[] values = { w.weight0, w.weight1, w.weight2, w.weight3 };
                bool changed = false;
                for (int k=0; k<4; k++) {
                    float value = values[k]; if (value <= 0f) continue;
                    int side = indices[k] == tipIndex[0] ? 0 : indices[k] == tipIndex[1] ? 1 : -1;
                    if (side < 0) { list.Add(new KeyValuePair<int,float>(indices[k],value)); continue; }
                    float y = bind[tipIndex[side]].MultiplyPoint3x4(vertices[i]).y;
                    float sample = Mathf.Clamp(y*curlDirection[side]/curlStep[side], 0f, CurlSegments);
                    int lower = Mathf.FloorToInt(sample), upper = Mathf.Min(CurlSegments, lower + 1);
                    float mix = sample - lower;
                    int first = lower == 0 ? indices[k] : oldBones.Length + side*CurlSegments + lower - 1;
                    int second = upper == 0 ? indices[k] : oldBones.Length + side*CurlSegments + upper - 1;
                    list.Add(new KeyValuePair<int,float>(first, value * (1f-mix)));
                    if (mix > 0f) list.Add(new KeyValuePair<int,float>(second, value*mix));
                    changed = true;
                }
                if (!changed) continue;
                // Merge coincident influences before selecting the four strongest weights.
                for(int a=0;a<list.Count;a++)for(int b=list.Count-1;b>a;b--)if(list[a].Key==list[b].Key){list[a]=new KeyValuePair<int,float>(list[a].Key,list[a].Value+list[b].Value);list.RemoveAt(b);}
                list.Sort((a,b) => b.Value.CompareTo(a.Value));
                while (list.Count < 4) list.Add(new KeyValuePair<int,float>(0,0f));
                float sum=list[0].Value+list[1].Value+list[2].Value+list[3].Value;
                if (sum <= .000001f) continue;
                weights[i] = new BoneWeight {
                    boneIndex0=list[0].Key, weight0=list[0].Value/sum,
                    boneIndex1=list[1].Key, weight1=list[1].Value/sum,
                    boneIndex2=list[2].Key, weight2=list[2].Value/sum,
                    boneIndex3=list[3].Key, weight3=list[3].Value/sum };
            }
            curlMesh = Instantiate(sourceMesh);
            curlMesh.name = sourceMesh.name + " (five weighted curl hinges)";
            curlMesh.bindposes = bind;
            curlMesh.boneWeights = weights;
            skin.bones = bones;
            skin.sharedMesh = curlMesh;
        }

        void UpdateCurlTips()
        {
            for (int side=0; side<2; side++)
                for (int j=0; j<CurlSegments; j++) {
                    var tip = curlTips[side,j]; if (!tip) continue;
                    // Opposite signs mirror the left/right curled ends.
                    float flow = Mathf.Clamp01(braidRunBlend + .35f * Mathf.Max(swimBlend, waterFloat) + .10f * climbBlend);
                    float angle = (side == 0 ? -1f : 1f) * flow * Mathf.Lerp(.9f, 1.5f, (float)j/(CurlSegments-1));
                    angle += swimBlend * .20f * Mathf.Sin(Time.time * .91f - j * .37f - side * .8f);
                    curlAngles[side,j] = Mathf.SmoothDamp(curlAngles[side,j], angle, ref curlVelocities[side,j], .26f + j*.014f, Mathf.Infinity, Mathf.Min(Time.deltaTime,.05f));
                    tip.localRotation = Quaternion.AngleAxis(curlAngles[side,j], Vector3.forward);
                }
        }

        void OnDestroy()
        {
            if (curlSkin && curlSkin.sharedMesh == curlMesh) { curlSkin.sharedMesh = originalCurlMesh; curlSkin.bones = originalCurlBones; }
            if (curlMesh) Destroy(curlMesh);
            // Removing the component also removes its generated hinges after skin restoration.
            for(int side=0;side<2;side++)if(curlTips[side,0])Destroy(curlTips[side,0].gameObject);
        }
        void OnEnable() { braidReady=false; referenceMotionReady=false; }
        void OnDisable()
        {
            braidReady = referenceMotionReady = false; braidRunBlend = capeRunBlend = capeIdleBlend = swimBlend = climbBlend = 0f;
            waterFloat = waterFloatVelocity = 0f;
            for(int side=0;side<2;side++){
                System.Array.Clear(braidVelocity[side],0,braidVelocity[side].Length);
                for(int j=0;j<CurlSegments;j++){curlAngles[side,j]=curlVelocities[side,j]=0f;if(curlTips[side,j])curlTips[side,j].localRotation=Quaternion.identity;}
            }
        }
        void SampleReferenceForces()
        {
            float dt=Mathf.Max(.0001f,Mathf.Min(Time.deltaTime,.05f));var actor=motor?motor.transform:root;float scale=Mathf.Max(.01f,root.TransformVector(Vector3.up).magnitude);
            if(!referenceMotionReady || Vector3.Distance(actor.position,previousActorPosition)>1.5f*scale || Time.deltaTime>.25f){
                actorVelocity=localAcceleration=Vector3.zero;turnVelocity=0f;previousActorPosition=actor.position;previousActorYaw=root.eulerAngles.y;braidReady=false;
                for(int side=0;side<2;side++)System.Array.Clear(braidVelocity[side],0,braidVelocity[side].Length);referenceMotionReady=true;
            }
            var velocity=Vector3.ClampMagnitude((actor.position-previousActorPosition)/dt,15f*scale);
            var acceleration=root.InverseTransformDirection((velocity-actorVelocity)/dt)/scale;
            localAcceleration=Vector3.Lerp(localAcceleration,Vector3.ClampMagnitude(acceleration,30f),1f-Mathf.Exp(-8f*dt));
            actorVelocity=Vector3.Lerp(actorVelocity,velocity,1f-Mathf.Exp(-14f*dt));previousActorPosition=actor.position;
            float yaw=Mathf.Clamp(Mathf.DeltaAngle(previousActorYaw,root.eulerAngles.y)/dt,-240f,240f);previousActorYaw=root.eulerAngles.y;
            turnVelocity=Mathf.Lerp(turnVelocity,yaw,1f-Mathf.Exp(-8f*dt));
            swimBlend=Mathf.Lerp(swimBlend,motor&&motor.IsSwimming?1f:0f,1f-Mathf.Exp(-3.5f*dt));
            climbBlend=Mathf.Lerp(climbBlend,motor&&motor.IsClimbing?1f:0f,1f-Mathf.Exp(-5f*dt));
            bool inWater=motor&&motor.IsSwimming;
            waterFloat=Mathf.SmoothDamp(waterFloat,inWater?1f:0f,ref waterFloatVelocity,inWater?.24f:.80f,Mathf.Infinity,dt);
        }

        void UpdateBraids()
        {
            if (!root || !pelvis || !chest || Time.deltaTime <= 0f) return;
            // Reset after switching character or teleporting rather than stretching the chain.
            float scale = root.TransformVector(Vector3.up).magnitude;
            bool reset = !braidReady || Vector3.Distance(root.position, braidRootPrevious) > 1.5f * scale;
            float run = braidRunBlend;
            float dt = Mathf.Min(Time.deltaTime,.05f);
            float water = Mathf.Clamp01(Mathf.Max(swimBlend,waterFloat));
            float flow = Mathf.Clamp01(run + .32f*water + .18f*climbBlend);
            Vector3 rootTravel = reset ? Vector3.zero : root.position-braidRootPrevious;
            var localVelocity = root.InverseTransformDirection(actorVelocity)/Mathf.Max(.01f,scale);
            Vector3 hip = root.InverseTransformPoint(pelvis.position);
            Vector3 upper = root.InverseTransformPoint(chest.position);
            for (int side = 0; side < braids.Length; side++)
            {
                var chain = braids[side];
                var previous = braidPrevious[side];
                var velocities = braidVelocity[side];
                if (!chain[0]) continue;
                for (int i = 0; i < chain.Length - 1; i++)
                {
                    var joint = chain[i]; var next = chain[i + 1];
                    if (!joint || !next) continue;
                    Vector3 animated = next.position;
                    float length = Vector3.Distance(joint.position, animated);
                    if (length < .00001f) continue;
                    float free = Mathf.SmoothStep(0f,1f,i/16f);
                    float settle = Mathf.Lerp(.038f,.095f,free) * Mathf.Lerp(1f,1.35f,water);
                    // Carry walking/vertical root motion with the anchor. The free-end lag
                    // comes from the acceleration spring, not from delaying every foot bob.
                    Vector3 carried = previous[i+1] + rootTravel*Mathf.Lerp(1f,.94f,run);
                    Vector3 lagged = reset ? animated : Vector3.SmoothDamp(carried,animated,ref velocities[i+1],settle,Mathf.Infinity,dt);
                    if(reset)velocities[i+1]=Vector3.zero;
                    Vector3 inertial = new Vector3(
                        Mathf.Clamp(-localAcceleration.x*.00026f-turnVelocity*.000018f,-.006f,.006f),
                        Mathf.Clamp(-actorVelocity.y/Mathf.Max(.01f,scale)*.0013f,-.005f,.005f),
                        Mathf.Clamp(-localVelocity.z*.0018f-localAcceleration.z*.00038f,-.016f,.010f));
                    inertial *= free*(.12f+.88f*flow);
                    inertial.y += water*free*.0025f*Mathf.Sin(Time.time*.95f-i*.19f-side*.8f);
                    Vector3 goal = Vector3.Lerp(animated,lagged,Mathf.Lerp(.018f,.30f,flow))+root.TransformVector(inertial);
                    goal = animated + Vector3.ClampMagnitude(goal-animated,Mathf.Lerp(.0018f,.022f,flow)*Mathf.Lerp(.30f,1f,free)*scale);
                    Vector3 local = root.InverseTransformPoint(goal);
                    Vector3 animatedLocal = root.InverseTransformPoint(animated);
                    // Walking should not pump the strands vertically with each step.
                    local.y = Mathf.Lerp(animatedLocal.y, local.y, .45f*flow);
                    if (waterFloat > .001f && i >= 3)
                    {
                        // A small distributed world-space bend keeps the floating
                        // lower locks behind the upright treading body. Roots stay
                        // attached, while the last curled region keeps its shape.
                        Vector3 forward=Vector3.ProjectOnPlane(root.forward,Vector3.up).normalized;
                        if(forward.sqrMagnitude<.5f)forward=Vector3.forward;
                        Vector3 floatDirection=-forward+root.right*(side==0?-.16f:.16f)-Vector3.up*.22f;
                        Vector3 floatingVector=root.TransformPoint(local)-joint.position;
                        if(floatingVector.sqrMagnitude>.000001f){
                            float flexible=Mathf.SmoothStep(0f,1f,Mathf.InverseLerp(2f,8f,i));
                            flexible*=Mathf.Lerp(1f,.25f,Mathf.InverseLerp(11f,16f,i));
                            Quaternion bend=Quaternion.RotateTowards(Quaternion.identity,Quaternion.FromToRotation(floatingVector,floatDirection),6f*flexible);
                            floatingVector=Quaternion.Slerp(Quaternion.identity,bend,waterFloat*.75f)*floatingVector;
                            local=root.InverseTransformPoint(joint.position+floatingVector);
                        }
                    }
                    float height = Mathf.InverseLerp(hip.y - .10f, upper.y + .14f, local.y);
                    Vector3 center = Vector3.Lerp(hip, upper, height);
                    // Keep each strand on its own side of the torso as it trails behind.
                    // The clearance increases gradually from the close-set roots.
                    float sideSign = side == 0 ? -1f : 1f;
                    float tipGap = Mathf.Lerp(.075f, .115f, flow);
                    float sideGap = Mathf.Lerp(.035f, tipGap, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1f, 8f, i)));
                    float signedX = (local.x - center.x) * sideSign;
                    if (signedX < sideGap) local.x += sideSign * (sideGap - signedX);
                    float lateral = Mathf.Abs(local.x - center.x);
                    if (i >= 2 && local.y > hip.y - .10f && local.y < upper.y + .14f && lateral < .20f)
                    {
                        // Taper the barrier at its outer edge to avoid a visible seam.
                        float blend = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.14f, .20f, lateral));
                        float back = center.z - .13f;
                        if (local.z > back && local.z < center.z + .25f)
                            local.z = Mathf.Lerp(local.z, back, blend);
                    }
                    if (flow > .001f && i >= 2) {
                        // Respect the segment length while aiming for its own side. Simply
                        // projecting the goal is insufficient when a short bone normalizes it.
                        Vector3 parentLocal = root.InverseTransformPoint(joint.position);
                        float segment = Vector3.Distance(parentLocal, animatedLocal);
                        float requiredX = center.x + sideSign * sideGap;
                        float x = sideSign * (local.x - requiredX) < 0f ? requiredX : local.x;
                        float dx = Mathf.Clamp(x - parentLocal.x, -segment * .94f, segment * .94f);
                        Vector2 yz = new Vector2(local.y - parentLocal.y, local.z - parentLocal.z);
                        if (yz.sqrMagnitude < .0000001f) yz = new Vector2(-1f, 0f);
                        yz = yz.normalized * Mathf.Sqrt(Mathf.Max(0f, segment*segment - dx*dx));
                        Vector3 separated = parentLocal + new Vector3(dx, yz.x, yz.y);
                        local = Vector3.Lerp(local, separated, flow);
                    }
                    goal = root.TransformPoint(local);
                    Vector3 current = animated - joint.position;
                    Vector3 wanted = goal - joint.position;
                    if (wanted.sqrMagnitude > .0000001f)
                        joint.rotation = Quaternion.FromToRotation(current, wanted) * joint.rotation;
                    // Ease the curled tips toward the preceding segment while running.
                    // Several existing skinned joints share the small change; rest shape returns at idle.
                    if (i >= 11 && chain[i - 1] && flow > .001f) {
                        Vector3 incoming = joint.position - chain[i - 1].position;
                        Vector3 outgoing = next.position - joint.position;
                        if (incoming.sqrMagnitude > .0000001f && outgoing.sqrMagnitude > .0000001f) {
                            Quaternion straight = Quaternion.FromToRotation(outgoing, incoming);
                            float angle = Quaternion.Angle(Quaternion.identity, straight);
                            float amount = flow * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(10f, 15f, i)) * .20f;
                            if (angle > .001f) amount = Mathf.Min(amount, 4f / angle);
                            joint.rotation = Quaternion.Slerp(Quaternion.identity, straight, amount) * joint.rotation;
                        }
                    }
                    previous[i + 1] = next.position;
                }
                previous[0] = chain[0].position;
            }
            braidRootPrevious = root.position;
            braidReady = true;
        }

        static void FitLeg(Leg leg)
        {
            if (!leg.hip || !leg.knee || !leg.foot || !leg.sourceFoot) return;
            var hip=leg.hip.position;var knee=leg.knee.position;var foot=leg.foot.position;
            var goal=leg.sourceFoot.position+leg.sourceFoot.rotation*leg.pivotOffset;
            var reach=goal-hip; if(reach.sqrMagnitude<.000001f)return;
            var axis=reach.normalized;float upper=(knee-hip).magnitude,lower=(foot-knee).magnitude;
            float dist=Mathf.Clamp(reach.magnitude,Mathf.Abs(upper-lower)+.0001f,upper+lower-.0001f);
            float along=(upper*upper-lower*lower+dist*dist)/(2f*dist);
            var pole=Vector3.ProjectOnPlane(knee-hip,axis);
            if(pole.sqrMagnitude<.0000001f)return;
            var newKnee=hip+along*axis+pole.normalized*Mathf.Sqrt(Mathf.Max(0,upper*upper-along*along));
            var footRotation=leg.foot.rotation;
            leg.hip.rotation=Quaternion.FromToRotation(knee-hip,newKnee-hip)*leg.hip.rotation;
            leg.knee.rotation=Quaternion.FromToRotation(leg.foot.position-leg.knee.position,hip+axis*dist-leg.knee.position)*leg.knee.rotation;
            leg.foot.rotation=footRotation;
        }
    }
}

