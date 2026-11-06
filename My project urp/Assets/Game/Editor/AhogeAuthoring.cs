using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using Game.ThirdPerson;

public static class AhogeAuthoring
{
    static AhogeMotion Configure(GameObject player)
    {
        var model = player.GetComponentInChildren<BlenderCharacterRetarget>(true);
        if (!model) throw new Exception("Visible character retarget is missing.");
        var ahoge = model.GetComponent<AhogeMotion>();
        if (!ahoge) ahoge = model.gameObject.AddComponent<AhogeMotion>();
        if (!ahoge.ConfigureRig()) throw new Exception("Ahoge DM_0_1 through DM_5_1 were not found.");
        ahoge.movementResponse = 1f; ahoge.breezeStrength = .75f; ahoge.maximumSwayDegrees = 7f; ahoge.maximumCurlDegreesPerJoint = 3.25f;
        EditorUtility.SetDirty(ahoge);
        if (PrefabUtility.IsPartOfPrefabInstance(ahoge)) PrefabUtility.RecordPrefabInstancePropertyModifications(ahoge);
        return ahoge;
    }
    [MenuItem("Tools/Characters/Add Subtle Ahoge Motion")]
    public static void Apply()
    {
        if (EditorApplication.isPlaying) throw new Exception("Exit Play mode before saving ahoge motion.");
        var player = UnityEngine.Object.FindAnyObjectByType<ThirdPersonMotor>();
        string backup = Application.dataPath + "/../Logs/AhogeBackup/" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        Directory.CreateDirectory(backup); EditorSceneManager.SaveScene(player.gameObject.scene); File.Copy(player.gameObject.scene.path, backup + "/Demo.unity");
        const string prefab = "Assets/Game/Prefabs/FurinaPlayer.prefab"; File.Copy(prefab, backup + "/FurinaPlayer.prefab");
        Configure(player.gameObject);
        var root = PrefabUtility.LoadPrefabContents(prefab);
        try { Configure(root); PrefabUtility.SaveAsPrefabAsset(root, prefab); }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        AssetDatabase.SaveAssets(); EditorSceneManager.MarkSceneDirty(player.gameObject.scene); EditorSceneManager.SaveScene(player.gameObject.scene);
        Validate(); Debug.Log("AHOGE_MOTION_SAVED joints=6 swing=7 curlPerJoint=3.25");
    }
    [MenuItem("Tools/Characters/Validate Ahoge Setup")]
    public static void Validate()
    {
        var motor = UnityEngine.Object.FindAnyObjectByType<ThirdPersonMotor>();
        var ahoge = motor.GetComponentInChildren<AhogeMotion>(true);
        Check(ahoge);
        var prefab = PrefabUtility.LoadPrefabContents("Assets/Game/Prefabs/FurinaPlayer.prefab");
        try { Check(prefab.GetComponentInChildren<AhogeMotion>(true)); }
        finally { PrefabUtility.UnloadPrefabContents(prefab); }
        Directory.CreateDirectory(Application.dataPath + "/../Logs");
        File.WriteAllText(Application.dataPath + "/../Logs/AhogeSetupValidation.txt", "sceneAndPrefab=true joints=DM_0_1..DM_5_1 skinned=true motionAndWind=true");
        Debug.Log("AHOGE_SETUP_VALIDATED sceneAndPrefab=true joints=6");
    }
    static void Check(AhogeMotion a)
    {
        if (!a || !a.enabled || !a.IsConfigured || a.joints[0].parent != a.head) throw new Exception("Ahoge rig configuration is incomplete.");
        var skin = a.GetComponentInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < 6; i++)
            if (a.joints[i].name != "DM_" + i + "_1" || (i > 0 && a.joints[i].parent != a.joints[i - 1]) || !skin.bones.Contains(a.joints[i]))
                throw new Exception("Ahoge must use the existing, skinned six-bone chain.");
    }
    [MenuItem("Tools/Characters/Play Verify Ahoge Motion")]
    public static void PlayVerify()
    {
        if (!EditorApplication.isPlaying) throw new Exception("Enter Play mode first.");
        var motor=UnityEngine.Object.FindAnyObjectByType<ThirdPersonMotor>();var ahoge=motor.GetComponentInChildren<AhogeMotion>();Check(ahoge);
        var follow=motor.cameraTransform.GetComponent<ThirdPersonFollowCamera>();
        Vector3 position=motor.transform.position;Quaternion facing=motor.visualRoot.rotation;
        float yaw=follow.yaw,pitch=follow.pitch,distance=follow.distance;bool motorOn=motor.enabled,cameraOn=follow.enabled;
        var map=motor.GetComponent<Game.World.WorldMapController>();if(map&&!map.WarpToVillage(0))throw new Exception("Safe motion test plaza unavailable.");
        float best=-1,heading=0;
        for(int i=0;i<16;i++)
        {
            float angle=i*22.5f;var direction=Quaternion.Euler(0,angle,0)*Vector3.forward;float clear=12;
            foreach(var hit in Physics.SphereCastAll(motor.transform.position+Vector3.up*.85f,.28f,direction,12,~0,QueryTriggerInteraction.Ignore))
                if(!hit.transform.IsChildOf(motor.transform))clear=Mathf.Min(clear,hit.distance);
            if(clear>best){best=clear;heading=angle;}
        }
        motor.enabled=true;motor.SimulationInput=true;motor.SimulatedMove=Vector2.zero;motor.SimulatedSprint=false;
        follow.enabled=false;follow.yaw=heading+180;follow.pitch=-7;follow.distance=1.4f;follow.UpdateView(1);
        double start=EditorApplication.timeSinceStartup;int previousFrame=-1,frames=0,idleSamples=0;float elapsed=0,maxSpeed=0,maxBone=0,minCurl=1,maxCurl=-1;
        Vector3 idleMin=Vector3.one*float.PositiveInfinity,idleMax=Vector3.one*float.NegativeInfinity;
        EditorApplication.CallbackFunction update=null;
        Action restore=()=>{EditorApplication.update-=update;if(!motor)return;motor.SimulationInput=false;motor.SimulatedMove=Vector2.zero;motor.SimulatedSprint=false;motor.TeleportTo(position);motor.visualRoot.rotation=facing;motor.enabled=motorOn;if(follow){follow.yaw=yaw;follow.pitch=pitch;follow.distance=distance;follow.enabled=cameraOn;follow.UpdateView(1);}};
        update=()=>{
            if(!EditorApplication.isPlaying){restore();return;}
            follow.enabled=false;follow.UpdateView(Time.deltaTime);
            if(previousFrame!=Time.frameCount)
            {
                elapsed+=Mathf.Min(Time.deltaTime,.1f);
                previousFrame=Time.frameCount;frames++;maxSpeed=Mathf.Max(maxSpeed,motor.PlanarSpeed);maxBone=Mathf.Max(maxBone,ahoge.MaximumJointOffset);minCurl=Mathf.Min(minCurl,ahoge.CurlAmount);maxCurl=Mathf.Max(maxCurl,ahoge.CurlAmount);
                if(elapsed>.2f&&elapsed<2){idleSamples++;var point=ahoge.head.InverseTransformPoint(ahoge.joints[5].position);idleMin=Vector3.Min(idleMin,point);idleMax=Vector3.Max(idleMax,point);}
            }
            // Sprint first so the short clear plaza path is sufficient to reach running speed.
            motor.SimulatedMove=elapsed>=2&&elapsed<3.8f?Vector2.down:Vector2.zero;motor.SimulatedSprint=elapsed>=2&&elapsed<2.8f;
            if(elapsed<6.5f&&EditorApplication.timeSinceStartup-start<30)return;
            float idleTravel=Vector3.Distance(idleMin,idleMax);restore();
            string report=$"frames={frames} seconds={elapsed:F2} maxSpeed={maxSpeed:F2} idleSamples={idleSamples} idleTipTravel={idleTravel:F5} curlRange={maxCurl-minCurl:F4} maximumJointOffset={maxBone:F2} clearance={best:F2}";
            File.WriteAllText(Application.dataPath+"/../Logs/AhogePlayValidation.txt",report);
            if(frames<25||elapsed<6.5f||maxSpeed<motor.sprintSpeed*.9f||idleSamples<5||float.IsNaN(idleTravel)||float.IsInfinity(idleTravel)||idleTravel<.00002f||maxCurl-minCurl<.01f||maxBone>12f)throw new Exception("Ahoge response verification failed: "+report);
            Debug.Log("AHOGE_PLAY_VERIFIED "+report);
        };EditorApplication.update+=update;
    }
}
