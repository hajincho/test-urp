using System;
using System.IO;
using System.Linq;
using Game.ThirdPerson;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class SilkClothAuthoring
{
    const string ReportRoot = @"C:\Users\fromj\Documents\Codex\2026-11-01\rl\work\silk-physics";

    static SilkClothDynamics Configure(GameObject player)
    {
        var retarget = player.GetComponentInChildren<BlenderCharacterRetarget>(true);
        if (!retarget) throw new Exception("No Blender character retarget");
        var solver = retarget.GetComponent<SilkClothDynamics>();
        if (!solver) solver = Undo.AddComponent<SilkClothDynamics>(retarget.gameObject);
        solver.ConfigureRig();
        var audit = JObject.Parse(File.ReadAllText(Path.Combine(ReportRoot, "rig-fbx.json")));
        var lengths = audit["bones"].ToDictionary(b => (string)b["name"], b => (float)b["length"]);
        foreach (var settings in solver.bones)
        {
            bool terminal = !solver.bones.Any(other => other.bone.parent == settings.bone);
            if (terminal) settings.tipLocal = Vector3.up * lengths[settings.bone.name];
        }
        if (solver.bones.Count != 113 || solver.bones.Any(b => b.weightedVertices == 0))
            throw new Exception("Cloth coverage differs from audited 113 weighted bones");
        solver.InitializeRig();
        EditorUtility.SetDirty(solver);
        PrefabUtility.RecordPrefabInstancePropertyModifications(solver);
        return solver;
    }

    [MenuItem("Tools/Characters/Install Audited Silk Cloth Physics")]
    public static void Apply()
    {
        if (EditorApplication.isPlaying) throw new Exception("Exit play mode before authoring");
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        Directory.CreateDirectory(Path.Combine(ReportRoot, "before"));
        EditorSceneManager.SaveScene(scene, Path.Combine(ReportRoot, "before", "Demo-before-physics.unity"), true);
        var player = UnityEngine.Object.FindAnyObjectByType<ThirdPersonMotor>();
        var solver = Configure(player.gameObject);
        const string prefabPath = "Assets/Game/Prefabs/FurinaPlayer.prefab";
        var contents = PrefabUtility.LoadPrefabContents(prefabPath);
        try { Configure(contents); PrefabUtility.SaveAsPrefabAsset(contents, prefabPath); }
        finally { PrefabUtility.UnloadPrefabContents(contents); }
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Export(solver);
    }

    public static void Export(SilkClothDynamics solver)
    {
        var rows = solver.bones.Select(b => new {
            name=b.bone.name, parent=b.bone.parent.name, b.chain, b.role, b.depth, b.weightedVertices,
            tipLocal=new[]{b.tipLocal.x,b.tipLocal.y,b.tipLocal.z}, b.stiffness,b.damping,b.mass,
            b.gravityInfluence,b.rotationLimit,b.parentMotionInfluence,b.velocityResponse,
            b.accelerationResponse,b.angularVelocityResponse,b.collisionRadius
        }).ToArray();
        File.WriteAllText(Path.Combine(ReportRoot,"applied-bones.json"),JsonConvert.SerializeObject(rows,Formatting.Indented));
        File.WriteAllText(Path.Combine(ReportRoot,"connection.json"),JsonConvert.SerializeObject(new {
            scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene().path,
            prefab="Assets/Game/Prefabs/FurinaPlayer.prefab", gameObject=solver.name,
            solver=solver.GetType().FullName, executionOrder=198, fixedHz=60,
            weightedClothBones=rows.Length, solver.ParticleCount,solver.LayerConstraintCount,
            solver.constraintIterations,solver.aerodynamicScale,solver.bodyCollisionScale,solver.layerCollisionScale
        },Formatting.Indented));
    }
}
