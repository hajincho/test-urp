using System;
using System.Collections.Generic;
using System.IO;
using Game.ThirdPerson;
using Game.World;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class WorldMapAuthoring
{
    const string Folder="Assets/Game/Maps";
    static Mesh BakeWater()
    {
        var positions=new List<Vector3>();var triangles=new List<int>();
        foreach(var filter in UnityEngine.Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Include)){
            if(!filter.sharedMesh||!filter.transform.parent||filter.transform.parent.name!="Terrain_Rivers")continue;
            var renderer=filter.GetComponent<Renderer>();if(!renderer||!renderer.enabled)continue;
            using(var arrays=MeshUtility.AcquireReadOnlyMeshData(filter.sharedMesh)){
                var data=arrays[0];int offset=positions.Count;
                using(var vertices=new NativeArray<Vector3>(data.vertexCount,Allocator.Temp)){
                    data.GetVertices(vertices);for(int i=0;i<vertices.Length;i++)positions.Add(filter.transform.TransformPoint(vertices[i]));
                }
                for(int sub=0;sub<data.subMeshCount;sub++){
                    using(var indices=new NativeArray<int>(data.GetSubMesh(sub).indexCount,Allocator.Temp)){
                        data.GetIndices(indices,sub);for(int i=0;i<indices.Length;i++)triangles.Add(offset+indices[i]);
                    }
                }
            }
        }
        if(triangles.Count==0)throw new Exception("No river/lake surface geometry found");
        var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(Folder+"/MapWaterSurface.asset");
        if(!mesh){mesh=new Mesh();AssetDatabase.CreateAsset(mesh,Folder+"/MapWaterSurface.asset");}
        mesh.Clear();mesh.name="Map water surfaces in world coordinates";mesh.indexFormat=IndexFormat.UInt32;mesh.SetVertices(positions);mesh.SetTriangles(triangles,0);mesh.RecalculateBounds();EditorUtility.SetDirty(mesh);return mesh;
    }
    static void Configure(WorldMapController map,Texture2D texture,Mesh water)
    {
        map.mapTexture=texture;map.worldMin=new Vector2(-300,-160);map.worldSize=new Vector2(720,720);
        map.waterSurfaceMesh=water;map.useWaterLevel=true;map.waterLevel=-32.45f;map.rayHeight=600;map.maxDrop=1500;map.nearbySearchRadius=5;
        map.villages=new[]{
            new WorldMapController.VillageWarp{name="남부 마을",position=new Vector3(18,.15f,-7)},
            new WorldMapController.VillageWarp{name="동부 마을",position=new Vector3(215,-8.05f,250)},
            new WorldMapController.VillageWarp{name="북서 마을",position=new Vector3(-118,-8.00f,355)}
        };
    }
    [MenuItem("Tools/World/Install World Map")]
    public static void Install()
    {
        if(EditorApplication.isPlaying)throw new Exception("Stop Play Mode first");
        var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();string backup=Application.dataPath+"/../Logs/WorldTraversalBackups";Directory.CreateDirectory(backup);
        File.Copy(scene.path,backup+"/"+scene.name+"_BeforeWorldMap_"+DateTime.UtcNow.ToString("yyyyMMdd_HHmmss")+".unity",true);
        if(!AssetDatabase.IsValidFolder(Folder))AssetDatabase.CreateFolder("Assets/Game","Maps");
        AssetDatabase.Refresh();var importer=(TextureImporter)AssetImporter.GetAtPath(Folder+"/WorldMapAtlas.png");
        if(!importer)throw new Exception("World map atlas image missing");
        importer.textureType=TextureImporterType.Default;importer.sRGBTexture=true;importer.mipmapEnabled=false;importer.wrapMode=TextureWrapMode.Clamp;importer.filterMode=FilterMode.Bilinear;importer.maxTextureSize=2048;importer.textureCompression=TextureImporterCompression.CompressedHQ;importer.SaveAndReimport();
        var texture=AssetDatabase.LoadAssetAtPath<Texture2D>(Folder+"/WorldMapAtlas.png");var water=BakeWater();
        var motor=UnityEngine.Object.FindAnyObjectByType<ThirdPersonMotor>();if(!motor)throw new Exception("No third-person player");
        var map=motor.GetComponent<WorldMapController>();if(!map)map=Undo.AddComponent<WorldMapController>(motor.gameObject);Configure(map,texture,water);
        foreach(var village in map.villages){
            if(!map.TryResolveLanding(map.WorldToMap(village.position),out var landing))throw new Exception("Invalid plaza: "+village.name);
            village.position=landing;Debug.Log("MAP_PLAZA "+village.name+" "+landing);
        }
        PrefabUtility.RecordPrefabInstancePropertyModifications(map);EditorUtility.SetDirty(map);
        string prefab="Assets/Game/Prefabs/FurinaPlayer.prefab";
        if(AssetDatabase.LoadAssetAtPath<GameObject>(prefab)){
            var root=PrefabUtility.LoadPrefabContents(prefab);
            try{var shared=root.GetComponent<WorldMapController>();if(!shared)shared=root.AddComponent<WorldMapController>();Configure(shared,texture,water);for(int i=0;i<map.villages.Length;i++)shared.villages[i].position=map.villages[i].position;PrefabUtility.SaveAsPrefabAsset(root,prefab);}
            finally{PrefabUtility.UnloadPrefabContents(root);}
        }
        AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);
        Debug.Log("WORLD_MAP_INSTALLED villages="+map.villages.Length+" waterTriangles="+water.triangles.Length/3);
    }
}
