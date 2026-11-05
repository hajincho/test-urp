using System;
using System.Linq;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

public static class GroundPlantCollisions
{
    static bool PlantName(string name)
    {
        string n = name.ToLowerInvariant();
        return n.Contains("waterlily") || n.Contains("water plants") || n.Contains("village_scatter") || n.Contains("grass") || n.Contains("fern") || n.Contains("weed") || n.Contains("bush") || n.Contains("leaves_cluster");
    }
    static bool IsGroundPlant(Collider collider)
    {
        if (collider is TerrainCollider || collider is CharacterController || collider.CompareTag("Player")) return false;
        if (collider is MeshCollider mesh && mesh.sharedMesh && (PlantName(mesh.sharedMesh.name) || PlantName(Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(mesh.sharedMesh))))) return true;
        for (var t = collider.transform; t; t = t.parent)
        {
            if (PlantName(t.name)) return true;
            if (t.GetComponent<LODGroup>()) break;
        }
        return false;
    }
    static Collider[] SceneColliders() => SceneManager.GetActiveScene().GetRootGameObjects()
        .SelectMany(r => r.GetComponentsInChildren<Collider>(true)).ToArray();

    [MenuItem("Tools/Characters/Audit Ground Plant Colliders")]
    public static void Audit()
    {
        var all = SceneColliders();
        var plants = all.Where(IsGroundPlant).ToArray();
        var lines = plants.Select(c => c.GetType().Name + " " + c.transform.parent?.name + "/" + c.name
            + (c is MeshCollider mesh && mesh.sharedMesh ? " mesh=" + mesh.sharedMesh.name : ""));
        File.WriteAllLines(Application.dataPath + "/../Logs/GroundPlantColliders.txt", lines);
        Debug.Log("GROUND_PLANTS_AUDIT total=" + all.Length + " plantColliders=" + plants.Length);
    }
    [MenuItem("Tools/Characters/Remove Ground Plant Colliders")]
    public static void Remove()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play mode before saving scene changes.");
        var all = SceneColliders();
        var plants = all.Where(IsGroundPlant).ToArray();
        int protectedCount = all.Length - plants.Length;
        Undo.SetCurrentGroupName("Remove ground plant collisions");
        foreach (var collider in plants) Undo.DestroyObjectImmediate(collider);
        Physics.SyncTransforms();
        var scene = SceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        var remaining = SceneColliders();
        if (remaining.Any(IsGroundPlant) || remaining.Length != protectedCount)
            throw new InvalidOperationException("Ground plant collider verification failed.");
        Debug.Log("GROUND_PLANTS_REMOVED removed=" + plants.Length + " remainingPlantColliders=0 protectedColliders=" + protectedCount + " scene=" + scene.path);
    }
}


