using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// The rock / stair / wall compositions are single meshes that also contain small low-poly plants (spiky grass
// blades, tufts, bushes). WorldTraversalFix uses the whole LOD0 mesh as a MeshCollider, so those plants blocked
// the player like walls. This bakes a collision copy of every such mesh without the plant pieces and assigns it
// to the scene colliders. Rock, stairs, walls and ground keep their exact collision.
//
// A plant piece is a connected component (vertices welded by position) whose albedo is green/olive in the atlas
// (hue 45-155 deg, saturation >= .25) and that is small (largest extent <= 1.8 m, <= 160 triangles), so hedges,
// grassy slopes and other large green structures stay solid.
public static class CompositionPlantCollision
{
    const string Folder = "Assets/Game/Collision/CompositionNoPlants";
    const string SourceFolder = "Assets/Art/Meshes/Compositions/";
    public const float MaxPlantExtent = 1.8f;
    public const int MaxPlantTriangles = 160;
    const float HueMin = 45f / 360f, HueMax = 155f / 360f, MinSaturation = .25f, MinValue = .1f;

    static T[] All<T>() where T : Component => SceneManager.GetActiveScene().GetRootGameObjects()
        .SelectMany(g => g.GetComponentsInChildren<T>(true)).ToArray();

    static bool FromCompositions(Mesh mesh) =>
        mesh && AssetDatabase.GetAssetPath(mesh).StartsWith(SourceFolder, StringComparison.Ordinal);

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

    struct Component
    {
        public int triangles;
        public Vector3 min, max;
        public Vector3 color;
        public int samples;
    }

    /// <summary>Returns null when the mesh contains no plant pieces (the original mesh is kept).</summary>
    static Mesh Bake(Mesh source, Material[] materials, Dictionary<Texture, Texture2D> textures, string path,
        out int removedTriangles, out int removedPieces)
    {
        removedTriangles = removedPieces = 0;
        var vertices = source.vertices;
        var uv = source.uv;
        // Weld by position (1 mm) so UV / normal seams do not split one piece into several.
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

        var components = new Dictionary<int, Component>();
        for (int k = 0; k < triangleSub.Count; k++)
        {
            int a = triangles[3 * k], b = triangles[3 * k + 1], c = triangles[3 * k + 2];
            int root = Find(weld[a]);
            if (!components.TryGetValue(root, out var comp))
                comp = new Component { min = vertices[a], max = vertices[a] };
            comp.triangles++;
            comp.min = Vector3.Min(comp.min, Vector3.Min(vertices[a], Vector3.Min(vertices[b], vertices[c])));
            comp.max = Vector3.Max(comp.max, Vector3.Max(vertices[a], Vector3.Max(vertices[b], vertices[c])));
            if (uv != null && uv.Length == vertices.Length)
            {
                var material = materials.Length > 0 ? materials[Mathf.Min(triangleSub[k], materials.Length - 1)] : null;
                var texture = Albedo(material, out var tint);
                if (texture)
                {
                    if (!textures.TryGetValue(texture, out var readable)) textures.Add(texture, readable = Readable(texture));
                    Vector2 centre = (uv[a] + uv[b] + uv[c]) / 3f;
                    var color = readable.GetPixelBilinear(Mathf.Repeat(centre.x, 1f), Mathf.Repeat(centre.y, 1f)) * tint;
                    comp.color += new Vector3(color.r, color.g, color.b);
                    comp.samples++;
                }
            }
            components[root] = comp;
        }

        var plant = new HashSet<int>();
        foreach (var pair in components)
        {
            var comp = pair.Value;
            if (comp.samples == 0 || comp.triangles > MaxPlantTriangles) continue;
            Vector3 size = comp.max - comp.min;
            if (Mathf.Max(size.x, Mathf.Max(size.y, size.z)) > MaxPlantExtent) continue;
            Vector3 mean = comp.color / comp.samples;
            Color.RGBToHSV(new Color(mean.x, mean.y, mean.z), out float h, out float s, out float v);
            if (h < HueMin || h > HueMax || s < MinSaturation || v < MinValue) continue;
            plant.Add(pair.Key);
            removedPieces++;
        }
        if (plant.Count == 0) return null;

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
        var result = AssetDatabase.LoadAssetAtPath<Mesh>(path);
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
        Directory.CreateDirectory(Folder);
        AssetDatabase.Refresh();

        // Materials per source mesh, taken from the scene renderers that draw it.
        var materials = new Dictionary<Mesh, Material[]>();
        foreach (var filter in All<MeshFilter>())
        {
            if (!FromCompositions(filter.sharedMesh) || materials.ContainsKey(filter.sharedMesh)) continue;
            var renderer = filter.GetComponent<MeshRenderer>();
            if (renderer) materials.Add(filter.sharedMesh, renderer.sharedMaterials);
        }

        var colliders = All<MeshCollider>().Where(c => FromCompositions(c.sharedMesh)).ToArray();
        var baked = new Dictionary<Mesh, Mesh>();
        var textures = new Dictionary<Texture, Texture2D>();
        int meshes = 0, pieces = 0, removed = 0, assigned = 0;
        var log = new List<string>();
        try
        {
            foreach (var source in colliders.Select(c => c.sharedMesh).Distinct())
            {
                if (!materials.TryGetValue(source, out var mats))
                {
                    var renderer = colliders.First(c => c.sharedMesh == source).GetComponent<MeshRenderer>();
                    mats = renderer ? renderer.sharedMaterials : new Material[0];
                }
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long _);
                string path = Folder + "/" + source.name + "_" + guid.Substring(0, 8) + "_NoPlants.asset";
                var mesh = Bake(source, mats, textures, path, out int triangles, out int count);
                baked.Add(source, mesh);
                if (!mesh) continue;
                meshes++; pieces += count; removed += triangles;
                log.Add($"{source.name}: plantPieces={count} plantTriangles={triangles}");
            }
            foreach (var collider in colliders)
            {
                if (!baked.TryGetValue(collider.sharedMesh, out var mesh) || !mesh) continue;
                collider.sharedMesh = mesh;
                EditorUtility.SetDirty(collider);
                assigned++;
            }
        }
        finally { foreach (var t in textures.Values) UnityEngine.Object.DestroyImmediate(t); }

        Physics.SyncTransforms();
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Directory.CreateDirectory(Application.dataPath + "/../Logs");
        File.WriteAllLines(Application.dataPath + "/../Logs/CompositionPlantCollision.txt", log);
        Debug.Log($"COMPOSITION_PLANTS_REMOVED meshes={meshes} plantPieces={pieces} plantTriangles={removed} " +
                  $"collidersReassigned={assigned} (details: Logs/CompositionPlantCollision.txt)");
    }

    [MenuItem("Tools/World/Validate Composition Plant Collision")]
    public static void Validate()
    {
        var colliders = All<MeshCollider>().Where(c => c.sharedMesh).ToArray();
        int baked = colliders.Count(c => AssetDatabase.GetAssetPath(c.sharedMesh).StartsWith(Folder, StringComparison.Ordinal));
        int original = colliders.Count(c => FromCompositions(c.sharedMesh));
        Debug.Log($"COMPOSITION_PLANTS_VALIDATE plantFreeColliders={baked} originalCompositionColliders={original} " +
                  "(original = meshes without plant pieces, or the bake has not run yet)");
    }
}
