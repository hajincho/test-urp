using System;
using System.IO;
using Game.ThirdPerson;
using Game.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ReferenceMotionAuthoring
{
    static void Configure(GameObject root)
    {
        var motor=root.GetComponent<ThirdPersonMotor>();if(!motor)return;
        motor.enableClimbing=true;motor.enableSwimming=true;motor.climbSpeed=1.25f;motor.swimSpeed=2.1f;motor.fastSwimSpeed=3.5f;
        var map=root.GetComponent<WorldMapController>();if(map){map.unrestrictedTeleport=true;EditorUtility.SetDirty(map);PrefabUtility.RecordPrefabInstancePropertyModifications(map);}
        foreach(var motion in root.GetComponentsInChildren<UntitledCharacterMotion>(true)){motion.referenceRunLean=25;motion.referenceDashLean=39;EditorUtility.SetDirty(motion);PrefabUtility.RecordPrefabInstancePropertyModifications(motion);}
        foreach(var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)){
            skin.updateWhenOffscreen=true;var scale=skin.transform.lossyScale;var box=skin.localBounds;var minimum=new Vector3(2.2f/Mathf.Max(.0001f,Mathf.Abs(scale.x)),2.2f/Mathf.Max(.0001f,Mathf.Abs(scale.y)),2.2f/Mathf.Max(.0001f,Mathf.Abs(scale.z)));box.extents=Vector3.Max(box.extents,minimum);skin.localBounds=box;EditorUtility.SetDirty(skin);PrefabUtility.RecordPrefabInstancePropertyModifications(skin);
        }
        EditorUtility.SetDirty(motor);PrefabUtility.RecordPrefabInstancePropertyModifications(motor);
    }
    [MenuItem("Tools/Characters/Apply New Reference Locomotion")]
    public static void Apply()
    {
        if(EditorApplication.isPlaying)throw new Exception("Stop Play Mode first");
        var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();string backup=Application.dataPath+"/../Logs/WorldTraversalBackups";Directory.CreateDirectory(backup);File.Copy(scene.path,backup+"/"+scene.name+"_BeforeReferenceMotion_"+DateTime.UtcNow.ToString("yyyyMMdd_HHmmss")+".unity",true);
        var motor=UnityEngine.Object.FindAnyObjectByType<ThirdPersonMotor>();if(!motor)throw new Exception("No active third-person motor");Configure(motor.gameObject);
        const string prefab="Assets/Game/Prefabs/FurinaPlayer.prefab";
        if(AssetDatabase.LoadAssetAtPath<GameObject>(prefab)){
            var root=PrefabUtility.LoadPrefabContents(prefab);try{Configure(root);PrefabUtility.SaveAsPrefabAsset(root,prefab);}finally{PrefabUtility.UnloadPrefabContents(root);}
        }
        AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);Debug.Log("REFERENCE_MOTION_APPLIED: shared locomotion, climbing, swimming and unrestricted map");
    }
}
