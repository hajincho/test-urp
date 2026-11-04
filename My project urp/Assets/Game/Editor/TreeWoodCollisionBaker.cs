using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// This atlas identifies foliage with its subsurface mask, while bark is black.
// Bake only the bark triangles; do not approximate the whole canopy with a hull.
public static class TreeWoodCollisionBaker
{
    const string Folder = "Assets/Game/Collision/TreeWood";
    static LODGroup[] Trees() => SceneManager.GetActiveScene().GetRootGameObjects()
        .SelectMany(r => r.GetComponentsInChildren<LODGroup>(true))
        .Where(g => g.name.IndexOf("tree", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
    static Texture2D ReadMask(Texture source)
    {
        var previous = RenderTexture.active;
        var rt = RenderTexture.GetTemporary(1024,1024,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
        var readable = new Texture2D(1024,1024,TextureFormat.RGBA32,false,true);
        try {
            Graphics.Blit(source,rt);
            RenderTexture.active=rt;
            readable.ReadPixels(new Rect(0,0,1024,1024),0,0);
            readable.Apply();
        } finally { RenderTexture.active=previous; RenderTexture.ReleaseTemporary(rt); }
        return readable;
    }
    static float Sample(Texture2D mask, Vector2 uv)
    {
        var c=mask.GetPixelBilinear(Mathf.Repeat(uv.x,1f),Mathf.Repeat(uv.y,1f));
        return Mathf.Max(c.r,Mathf.Max(c.g,c.b));
    }
    static bool Leaf(Texture2D mask, Vector2 a, Vector2 b, Vector2 c)
    {
        return Sample(mask,(a+b+c)/3f)>.04f || Sample(mask,a*.6f+b*.2f+c*.2f)>.04f
            || Sample(mask,a*.2f+b*.6f+c*.2f)>.04f || Sample(mask,a*.2f+b*.2f+c*.6f)>.04f;
    }
    static Mesh Bake(Mesh source, Material[] materials, Dictionary<Texture,Texture2D> masks, string path)
    {
        var vertices=source.vertices; var uv=source.uv;
        var kept=new List<int>(); int foliage=0;
        for(int sub=0;sub<source.subMeshCount;sub++) {
            var material=materials[Mathf.Min(sub,materials.Length-1)];
            Texture texture=material.GetTexturePropertyNames().Contains("_SubsurfaceTex")?material.GetTexture("_SubsurfaceTex"):null;
            if(!texture) throw new Exception("No foliage mask for tree material: "+material.name);
            if(!masks.TryGetValue(texture,out var mask)) {mask=ReadMask(texture);masks.Add(texture,mask);}
            var triangles=source.GetTriangles(sub);
            for(int i=0;i<triangles.Length;i+=3) {
                int a=triangles[i],b=triangles[i+1],c=triangles[i+2];
                if(Leaf(mask,uv[a],uv[b],uv[c])) {foliage++;continue;}
                kept.Add(a);kept.Add(b);kept.Add(c);
            }
        }
        if(kept.Count==0 || foliage==0) throw new Exception("Tree wood/foliage separation failed: "+path);
        var map=new Dictionary<int,int>();var compact=new List<Vector3>();var coords=new List<Vector2>();
        for(int i=0;i<kept.Count;i++) {
            int old=kept[i];
            if(!map.TryGetValue(old,out int index)) {index=compact.Count;map.Add(old,index);compact.Add(vertices[old]);coords.Add(uv[old]);}
            kept[i]=index;
        }
        var result=AssetDatabase.LoadAssetAtPath<Mesh>(path);
        bool create=!result;
        if(create) result=new Mesh(); else result.Clear();
        result.name=Path.GetFileNameWithoutExtension(path);
        result.indexFormat=compact.Count>65535?IndexFormat.UInt32:IndexFormat.UInt16;
        result.SetVertices(compact); result.SetUVs(0,coords); result.SetTriangles(kept,0);
        result.RecalculateBounds(); result.RecalculateNormals();
        if(create)AssetDatabase.CreateAsset(result,path); else EditorUtility.SetDirty(result);
        Debug.Log($"TREE_WOOD_MESH {result.name} woodTriangles={kept.Count/3} removedFoliageTriangles={foliage}");
        return result;
    }
    [MenuItem("Tools/World/Bake Tree Wood Only Collision")]
    public static void Apply()
    {
        if(EditorApplication.isPlaying)throw new Exception("Stop play mode before baking tree collision.");
        var scene=SceneManager.GetActiveScene();
        string backup=Application.dataPath+"/../Logs/WorldTraversalBackups";
        Directory.CreateDirectory(backup);
        File.Copy(scene.path,backup+"/"+scene.name+"_BeforeTreeWood_"+DateTime.UtcNow.ToString("yyyyMMdd_HHmmss")+".unity",true);
        Directory.CreateDirectory(Folder);AssetDatabase.Refresh();
        var cache=new Dictionary<Mesh,Mesh>();var masks=new Dictionary<Texture,Texture2D>();
        int count=0,disabled=0;
        try {
            // Prepare all assets before changing any scene collider.
            foreach(var group in Trees()) {
                var renderer=group.GetLODs()[0].renderers.First(r=>r&&r.shadowCastingMode!=ShadowCastingMode.ShadowsOnly);
                var source=renderer.GetComponent<MeshFilter>().sharedMesh;
                if(cache.ContainsKey(source))continue;
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source,out string guid,out long id);
                string label=Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(source));
                cache.Add(source,Bake(source,renderer.sharedMaterials,masks,Folder+"/"+label+"_"+guid.Substring(0,8)+"_Wood.asset"));
            }
            foreach(var group in Trees()) {
                var renderer=group.GetLODs()[0].renderers.First(r=>r&&r.shadowCastingMode!=ShadowCastingMode.ShadowsOnly);
                foreach(var c in group.GetComponentsInChildren<Collider>(true)) {
                    if(c.GetComponentInParent<LODGroup>()!=group)continue;
                    if(c.enabled)disabled++;
                    c.enabled=false;
                }
                var collider=renderer.GetComponent<MeshCollider>();
                if(!collider)collider=renderer.gameObject.AddComponent<MeshCollider>();
                collider.sharedMesh=cache[renderer.GetComponent<MeshFilter>().sharedMesh];
                collider.convex=false;collider.isTrigger=false;collider.enabled=true;
                count++;
            }
            Physics.SyncTransforms();
            AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);
            Debug.Log($"TREE_WOOD_BAKED trees={count} uniqueMeshes={cache.Count} oldCollidersDisabled={disabled}");
        } finally { foreach(var mask in masks.Values)UnityEngine.Object.DestroyImmediate(mask); }
    }
    [MenuItem("Tools/World/Validate Tree Wood Collision")]
    public static void Validate()
    {
        var masks=new Dictionary<Texture,Texture2D>();int count=0,triangles=0;
        try {
            foreach(var group in Trees()) {
                var renderer=group.GetLODs()[0].renderers.First(r=>r&&r.shadowCastingMode!=ShadowCastingMode.ShadowsOnly);
                var all=group.GetComponentsInChildren<Collider>(true).Where(c=>c.enabled).ToArray();
                if(all.Length!=1 || !(all[0] is MeshCollider collider) || collider.convex || !collider.sharedMesh.name.EndsWith("_Wood"))
                    throw new Exception("Tree retains a canopy collider: "+group.name);
                var mat=renderer.sharedMaterial;var texture=mat.GetTexture("_SubsurfaceTex");
                if(!masks.TryGetValue(texture,out var mask)){mask=ReadMask(texture);masks.Add(texture,mask);}
                var uv=collider.sharedMesh.uv;var t=collider.sharedMesh.triangles;
                for(int i=0;i<t.Length;i+=3)if(Leaf(mask,uv[t[i]],uv[t[i+1]],uv[t[i+2]]))throw new Exception("Leaf triangle still collides: "+group.name);
                triangles+=t.Length/3;count++;
            }
            Debug.Log($"TREE_WOOD_VALIDATED trees={count} woodTriangles={triangles} collidingLeafTriangles=0");
        } finally {foreach(var mask in masks.Values)UnityEngine.Object.DestroyImmediate(mask);}
    }
}
