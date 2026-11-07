using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// The rock / stair / wall compositions are single meshes that also contain small low-poly plants (spiky blades,
// grass cards and tufts, reeds, bushes). WorldTraversalFix used each whole LOD0 mesh as a MeshCollider, so those
// plants blocked the player like walls. This generates a collision copy of every composition mesh without its
// plant pieces and lists the pairs in a CompositionPlantCollisionMap (Resources). At run time every collider is
// switched to its copy, so nothing has to be run by hand and the scene file is not modified. Rock, stairs, walls,
// pavements and flat grass ground keep their exact collision; rendering is untouched.
//
// A plant piece is a connected component (vertices welded by position) whose atlas colour is green/olive and that
// is plant sized. The colour rule was fitted on all 237 composition FBX files: the sage-grey stone walls
// (hue ~92-96 deg, saturation ~.26, mostly 3-25 m) stay solid, plant cards/blades/tufts (saturation >= .3, or
// the paler 60-85 deg greens) are removed. Flat green ground patches wider than 1.2 m are walkable and stay solid.
[InitializeOnLoad]
public static class CompositionPlantCollision
{
    /// <summary>Bump when the rule changes: the copies are regenerated automatically after the next compile.</summary>
    public const int RuleVersion = 2;
    const string Folder = "Assets/Game/Collision/CompositionNoPlants";
    const string MapFolder = "Assets/Game/Collision/Resources";
    const string MapPath = MapFolder + "/" + CompositionPlantCollisionMap.ResourcePath + ".asset";
    const string SourceFolder = "Assets/Art/Meshes/Compositions";
    public const float MaxPlantExtent = 3.2f;
    public const int MaxPlantTriangles = 400;
    const float FlatGroundWidth = 1.2f;

    static CompositionPlantCollision() => EditorApplication.delayCall += AutoGenerate;

    static void AutoGenerate()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || Application.isBatchMode) return;
        var map = AssetDatabase.LoadAssetAtPath<CompositionPlantCollisionMap>(MapPath);
        if (map && map.version == RuleVersion) return;
        Debug.Log("[CompositionPlantCollision] Generating plant-free collision for the compositions (one time)...");
        Generate();
    }

    public static bool PlantColour(Color colour)
    {
        Color.RGBToHSV(colour, out float h, out float s, out float v);
        h *= 360f;
        if (v < .1f || h < 40f || h > 150f) return false;
        return s >= .33f || (h >= 52f && s >= .29f) || (h >= 60f && h < 85f && s >= .18f);
    }

    static bool PlantShape(Vector3 size, int triangles)
    {
        if (triangles > MaxPlantTriangles || Mathf.Max(size.x, Mathf.Max(size.y, size.z)) > MaxPlantExtent) return false;
        float horizontal = Mathf.Max(size.x, size.z);
        return !(size.y < .25f * horizontal && horizontal > FlatGroundWidth); // flat ground patch: keep walkable
    }

    static Texture2D Readable(Texture source)
    {
        var previous = RenderTexture.active;
        var rt = RenderTexture.GetTemporary(1024, 1024, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var readable = new Texture2D(1024, 1024, TextureFormat.RGBA32, false, false);
        try
        {
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            readable.ReadPixels(new Rect(0, 0, 1024, 1024), 0, 0);
            readable.Apply();
        }
        finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt); }
        return readable;
    }

    static Texture Albedo(Material material, out Color tint)
    {
        tint = Color.white;
        if (!material) return null;
        if (material.HasProperty("_BaseColor")) tint = material.GetColor("_BaseColor");
        else if (material.HasProperty("_Color")) tint = material.GetColor("_Color");
        if (material.HasProperty("_BaseMap") && material.GetTexture("_BaseMap")) return material.GetTexture("_BaseMap");
        return material.mainTexture;
    }

    struct Piece { public int triangles, samples; public Vector3 min, max, colour; }

    /// <summary>Plant-free copy of <paramref name="source"/> at <paramref name="path"/>. Returns null (and writes
    /// nothing) when the mesh has no plant pieces and no earlier copy exists.</summary>
    static Mesh Bake(Mesh source, Material[] materials, Dictionary<Texture, Texture2D> textures, string path,
        out int removedTriangles, out int removedPieces)
    {
        removedTriangles = removedPieces = 0;
        var vertices = source.vertices;
        var uv = source.uv;
        var weld = new int[vertices.Length];
        var byPosition = new Dictionary<Vector3Int, int>();
        for (int i = 0; i < vertices.Length; i++)
        {
            var key = Vector3Int.RoundToInt(vertices[i] * 1000f);
            if (!byPosition.TryGetValue(key, out int first)) byPosition.Add(key, first = i);
            weld[i] = first;
        }
        var parent = new int[vertices.Length];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int a) { while (parent[a] != a) a = parent[a] = parent[parent[a]]; return a; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }

        var triangles = new List<int>();
        var triangleSub = new List<int>();
        for (int sub = 0; sub < source.subMeshCount; sub++)
        {
            var t = source.GetTriangles(sub);
            for (int i = 0; i < t.Length; i += 3)
            {
                triangles.Add(t[i]); triangles.Add(t[i + 1]); triangles.Add(t[i + 2]);
                triangleSub.Add(sub);
                Union(weld[t[i]], weld[t[i + 1]]);
                Union(weld[t[i]], weld[t[i + 2]]);
            }
        }

        var pieces = new Dictionary<int, Piece>();
        bool hasUv = uv != null && uv.Length == vertices.Length;
        for (int k = 0; k < triangleSub.Count; k++)
        {
            int a = triangles[3 * k], b = triangles[3 * k + 1], c = triangles[3 * k + 2];
            int root = Find(weld[a]);
            if (!pieces.TryGetValue(root, out var piece)) piece = new Piece { min = vertices[a], max = vertices[a] };
            piece.triangles++;
            piece.min = Vector3.Min(piece.min, Vector3.Min(vertices[a], Vector3.Min(vertices[b], vertices[c])));
            piece.max = Vector3.Max(piece.max, Vector3.Max(vertices[a], Vector3.Max(vertices[b], vertices[c])));
            if (hasUv && materials.Length > 0)
            {
                var texture = Albedo(materials[Mathf.Min(triangleSub[k], materials.Length - 1)], out var tint);
                if (texture)
                {
                    if (!textures.TryGetValue(texture, out var readable)) textures.Add(texture, readable = Readable(texture));
                    Vector2 centre = (uv[a] + uv[b] + uv[c]) / 3f;
                    var colour = readable.GetPixelBilinear(Mathf.Repeat(centre.x, 1f), Mathf.Repeat(centre.y, 1f)) * tint;
                    piece.colour += new Vector3(colour.r, colour.g, colour.b);
                    piece.samples++;
                }
            }
            pieces[root] = piece;
        }

        var plant = new HashSet<int>();
        foreach (var pair in pieces)
        {
            var piece = pair.Value;
            if (piece.samples == 0 || !PlantShape(piece.max - piece.min, piece.triangles)) continue;
            Vector3 mean = piece.colour / piece.samples;
            if (!PlantColour(new Color(mean.x, mean.y, mean.z))) continue;
            plant.Add(pair.Key);
            removedPieces++;
        }
        var result = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (plant.Count == 0 && !result) return null;

        var kept = new List<int>(triangles.Count);
        for (int k = 0; k < triangleSub.Count; k++)
        {
            int a = triangles[3 * k];
            if (plant.Contains(Find(weld[a]))) { removedTriangles++; continue; }
            kept.Add(a); kept.Add(triangles[3 * k + 1]); kept.Add(triangles[3 * k + 2]);
        }
        var map = new Dictionary<int, int>();
        var compact = new List<Vector3>();
        for (int i = 0; i < kept.Count; i++)
        {
            int old = kept[i];
            if (!map.TryGetValue(old, out int index)) { index = compact.Count; map.Add(old, index); compact.Add(vertices[old]); }
            kept[i] = index;
        }
        bool create = !result;
        if (create) result = new Mesh(); else result.Clear();
        result.name = Path.GetFileNameWithoutExtension(path);
        result.indexFormat = compact.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        result.SetVertices(compact);
        result.SetTriangles(kept, 0);
        result.RecalculateBounds();
        result.RecalculateNormals();
        if (create) AssetDatabase.CreateAsset(result, path); else EditorUtility.SetDirty(result);
        return result;
    }

    static string CopyPath(Mesh source)
    {
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long _);
        return Folder + "/" + source.name + "_" + guid.Substring(0, 8) + "_NoPlants.asset";
    }

    /// <summary>Bakes every composition mesh (all LODs and proxies) and rewrites the runtime map.</summary>
    [MenuItem("Tools/World/Regenerate Composition Plant Collision")]
    public static CompositionPlantCollisionMap Generate()
    {
        Directory.CreateDirectory(Folder);
        Directory.CreateDirectory(MapFolder);
        AssetDatabase.Refresh();
        var map = AssetDatabase.LoadAssetAtPath<CompositionPlantCollisionMap>(MapPath);
        if (!map)
        {
            map = ScriptableObject.CreateInstance<CompositionPlantCollisionMap>();
            AssetDatabase.CreateAsset(map, MapPath);
        }
        map.sources.Clear();
        map.plantFree.Clear();
        var textures = new Dictionary<Texture, Texture2D>();
        var done = new HashSet<Mesh>();
        var log = new List<string>();
        int meshes = 0, pieces = 0, removed = 0;
        string[] models = AssetDatabase.FindAssets("t:Model", new[] { SourceFolder });
        try
        {
            for (int m = 0; m < models.Length; m++)
            {
                string modelPath = AssetDatabase.GUIDToAssetPath(models[m]);
                if (EditorUtility.DisplayCancelableProgressBar("Composition plant collision", modelPath, (float)m / models.Length)) break;
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                if (!model) continue;
                foreach (var filter in model.GetComponentsInChildren<MeshFilter>(true))
                {
                    var source = filter.sharedMesh;
                    if (!source || !done.Add(source)) continue;
                    var renderer = filter.GetComponent<MeshRenderer>();
                    var copy = Bake(source, renderer ? renderer.sharedMaterials : new Material[0], textures, CopyPath(source),
                        out int triangles, out int count);
                    if (!copy) continue;
                    map.sources.Add(source);
                    map.plantFree.Add(copy);
                    if (count == 0) continue;
                    meshes++; pieces += count; removed += triangles;
                    log.Add($"{modelPath} {source.name}: plantPieces={count} plantTriangles={triangles}");
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            foreach (var t in textures.Values) UnityEngine.Object.DestroyImmediate(t);
        }
        map.version = RuleVersion;
        EditorUtility.SetDirty(map);
        CompositionPlantCollisionMap.ResetCache();
        AssetDatabase.SaveAssets();
        Directory.CreateDirectory(Application.dataPath + "/../Logs");
        File.WriteAllLines(Application.dataPath + "/../Logs/CompositionPlantCollision.txt", log);
        Debug.Log($"COMPOSITION_PLANTS_GENERATED rule=v{RuleVersion} meshesWithPlants={meshes} plantPieces={pieces} " +
                  $"plantTriangles={removed} mapped={map.sources.Count} (details: Logs/CompositionPlantCollision.txt)");
        return map;
    }

    /// <summary>Optional: also write the copies into the open scene, so the scene itself no longer needs the
    /// run-time switch. Not required for play mode or builds.</summary>
    [MenuItem("Tools/World/Remove Plant Collision From Compositions")]
    public static void Apply()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play mode before baking collision.");
        var scene = SceneManager.GetActiveScene();
        if (scene.path != "Assets/Scenes/Demo.unity") throw new InvalidOperationException("Open the Demo scene first.");
        string backup = Application.dataPath + "/../Logs/WorldTraversalBackups";
        Directory.CreateDirectory(backup);
        EditorSceneManager.SaveScene(scene);
        File.Copy(scene.path, backup + "/Demo_BeforeCompositionPlants_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + ".unity", true);
        var map = Generate();
        int switched = CompositionPlantCollisionMap.Apply(scene);
        foreach (var collider in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<MeshCollider>(true)))
            EditorUtility.SetDirty(collider);
        Physics.SyncTransforms();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"COMPOSITION_PLANTS_REMOVED collidersReassigned={switched} mapped={map.sources.Count} scene={scene.path}");
    }

    [MenuItem("Tools/World/Validate Composition Plant Collision")]
    public static void Validate()
    {
        var map = AssetDatabase.LoadAssetAtPath<CompositionPlantCollisionMap>(MapPath);
        var sources = map ? new HashSet<Mesh>(map.sources) : new HashSet<Mesh>();
        var colliders = SceneManager.GetActiveScene().GetRootGameObjects()
            .SelectMany(r => r.GetComponentsInChildren<MeshCollider>(true)).Where(c => c.sharedMesh).ToArray();
        int pending = colliders.Count(c => sources.Contains(c.sharedMesh));
        int plantFree = colliders.Count(c => AssetDatabase.GetAssetPath(c.sharedMesh).StartsWith(Folder, StringComparison.Ordinal));
        Debug.Log($"COMPOSITION_PLANTS_VALIDATE rule=v{(map ? map.version : 0)} mapped={sources.Count} " +
                  $"sceneCollidersAlreadyPlantFree={plantFree} sceneCollidersSwitchedAtRuntime={pending}");
    }
}
