using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// Bake static collision from the visible structural geometry, independently of visual LOD.
public static class WorldTraversalFix
{
    const string MaterialsPath = "Assets/Game/Materials/Architecture";
    static bool Structure(LODGroup group) => group.name.StartsWith("Composition_", StringComparison.Ordinal)
        || group.name.StartsWith("Preset_", StringComparison.Ordinal)
        || group.name.StartsWith("SM_Town_", StringComparison.Ordinal);
    static T[] All<T>() where T : Component => SceneManager.GetActiveScene().GetRootGameObjects()
        .SelectMany(g => g.GetComponentsInChildren<T>(true)).ToArray();
    static bool Plant(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("village_scatter") || n.Contains("grass") || n.Contains("fern") || n.Contains("weed") || n.Contains("bush") || n.Contains("leaves_cluster");
    }
    static bool PlantCollider(Collider c)
    {
        if (c is CharacterController || c is TerrainCollider) return false;
        if (c is MeshCollider m && m.sharedMesh && Plant(m.sharedMesh.name)) return true;
        for (var t = c.transform; t; t = t.parent) {
            if (Plant(t.name)) return true;
            if (t.GetComponent<LODGroup>()) break;
        }
        return false;
    }
    static Material DoubleSided(Material original, Dictionary<Material, Material> cache)
    {
        if (!original || !original.HasProperty("_Cull")) return original;
        if (original.GetFloat("_Cull") == 0f) return original;
        if (cache.TryGetValue(original, out var found)) return found;
        Directory.CreateDirectory(MaterialsPath);
        string path = MaterialsPath + "/" + original.name + "_TwoSided.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (!material) {
            material = new Material(original) { name = original.name + "_TwoSided" };
            AssetDatabase.CreateAsset(material, path);
        }
        material.SetFloat("_Cull", 0f);
        EditorUtility.SetDirty(material);
        cache.Add(original, material);
        return material;
    }

    [MenuItem("Tools/World/Fix Structure Traversal and Visibility")]
    public static void Apply()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play mode before baking collision.");
        var scene = SceneManager.GetActiveScene();
        var groups = All<LODGroup>().Where(Structure).ToArray();
        int removedPlants = 0, disabledBoxes = 0, disabledProxies = 0, meshes = 0;
        // A scene-sized Undo transaction can overflow the Editor's undo buffer.
        // Keep an on-disk scene backup instead of recording thousands of renderers.
        string backupFolder = Application.dataPath + "/../Logs/WorldTraversalBackups";
        Directory.CreateDirectory(backupFolder);
        File.Copy(scene.path, backupFolder + "/" + scene.name + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + ".unity", true);
        foreach (var c in All<Collider>()) {
            if (PlantCollider(c)) { UnityEngine.Object.DestroyImmediate(c); removedPlants++; continue; }
            if (c is BoxCollider && c.transform.root.name.StartsWith("CollisionHolderComp", StringComparison.Ordinal)) {
                if (c.enabled) disabledBoxes++;
                c.enabled = false;
            }
        }
        var materialCache = new Dictionary<Material, Material>();
        foreach (var group in groups) {
            var lods = group.GetLODs();
            if (lods.Length == 0) continue;
            // Old simplified/shadow meshes seal arches and the space under bridges.
            foreach (var c in group.GetComponentsInChildren<MeshCollider>(true)) {
                if (c.GetComponentInParent<LODGroup>() != group || c.name != "Static Collision") continue;
                if (c.enabled) disabledProxies++;
                c.enabled = false;
            }
            foreach (var renderer in lods[0].renderers) {
                if (!renderer || renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (!filter || !filter.sharedMesh || Plant(renderer.name)) continue;
                var collider = renderer.GetComponent<MeshCollider>();
                if (!collider) collider = renderer.gameObject.AddComponent<MeshCollider>();
                collider.convex = false;
                collider.isTrigger = false;
                collider.sharedMesh = filter.sharedMesh;
                collider.enabled = true;
                collider.contactOffset = .01f;
                meshes++;
            }
            foreach (var renderer in lods.SelectMany(l => l.renderers).Where(r => r).Distinct()) {
                if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter && filter.sharedMesh) renderer.localBounds = filter.sharedMesh.bounds;
                renderer.sharedMaterials = renderer.sharedMaterials.Select(m => DoubleSided(m, materialCache)).ToArray();
                EditorUtility.SetDirty(renderer);
            }
            lods[lods.Length - 1].screenRelativeTransitionHeight = 0f;
            group.SetLODs(lods);
            group.RecalculateBounds();
            EditorUtility.SetDirty(group);
        }
        Physics.SyncTransforms();
        EditorSceneManager.MarkSceneDirty(scene);
        AssetDatabase.SaveAssets();
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"WORLD_TRAVERSAL_FIXED groups={groups.Length} structuralMeshes={meshes} disabledBoxes={disabledBoxes} disabledProxies={disabledProxies} removedPlants={removedPlants} doubleSidedMaterials={materialCache.Count}");
    }

    [MenuItem("Tools/World/Validate Structure Traversal")]
    public static void Validate()
    {
        int geometry = 0, holes = 0, floors = 0;
        var lines = new List<string>();
        foreach (var group in All<LODGroup>().Where(Structure)) {
            var lods = group.GetLODs();
            if (lods.Length == 0) continue;
            if (lods.Last().screenRelativeTransitionHeight != 0f) throw new Exception("Structural LOD still culls: " + group.name);
            foreach (var r in lods[0].renderers.Where(r => r && r.shadowCastingMode != ShadowCastingMode.ShadowsOnly)) {
                var filter = r.GetComponent<MeshFilter>();
                if (!filter || !filter.sharedMesh) continue;
                var c = r.GetComponent<MeshCollider>();
                if (!c || !c.enabled || c.convex || c.sharedMesh != filter.sharedMesh) throw new Exception("Incorrect collision: " + r.name);
                geometry++;
                foreach (var m in r.sharedMaterials) if (m && m.HasProperty("_Cull") && m.GetFloat("_Cull") != 0f)
                    throw new Exception("Single sided architecture: " + r.name);
                var b = c.bounds;
                for (int x = 1; x < 5; x++) for (int z = 1; z < 5; z++) {
                    var origin = new Vector3(Mathf.Lerp(b.min.x,b.max.x,x/5f),b.max.y+1f,Mathf.Lerp(b.min.z,b.max.z,z/5f));
                    if (c.Raycast(new Ray(origin,Vector3.down),out var hit,b.size.y+2f) && hit.normal.y > .7f) floors++;
                }
            }
        }
        // Sample rays that used to collide with an envelope but now pass through actual openings.
        foreach (var box in All<BoxCollider>().Where(b => b.transform.root.name.StartsWith("CollisionHolderComp"))) {
            if (box.enabled) throw new Exception("Solid envelope remains: " + box.name);
            var b = new Bounds(box.transform.TransformPoint(box.center), Vector3.Scale(box.size, new Vector3(Mathf.Abs(box.transform.lossyScale.x),Mathf.Abs(box.transform.lossyScale.y),Mathf.Abs(box.transform.lossyScale.z))));
            for (int i=1;i<=7;i++) {
                Vector3 a = new Vector3(Mathf.Lerp(b.min.x,b.max.x,i/8f),b.min.y+1f,b.min.z-.5f);
                if (!Physics.Raycast(a,Vector3.forward,b.size.z+1f,~0,QueryTriggerInteraction.Ignore)) {
                    holes++;
                    if(lines.Count < 12) lines.Add(box.name+" passage="+a.ToString("F2"));
                }
            }
        }
        int plants = All<Collider>().Count(PlantCollider);
        if (plants != 0) throw new Exception("Ground plant colliders remain");
        lines.Insert(0,$"structuralMeshes={geometry} openEnvelopeRays={holes} walkableSurfaceSamples={floors} grassColliders={plants}");
        Directory.CreateDirectory(Application.dataPath + "/../Logs");
        File.WriteAllLines(Application.dataPath + "/../Logs/WorldTraversalValidation.txt",lines);
        Debug.Log("WORLD_TRAVERSAL_VALIDATED " + lines[0]);
    }
}
