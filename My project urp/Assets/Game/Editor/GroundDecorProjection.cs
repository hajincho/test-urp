using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

public static class GroundDecorProjection
{
    enum Kind { None, Grass, Leaves, Bush }
    const string Folder = "Assets/Game/Geometry/GroundedDecor";
    static string Logs => Application.dataPath + "/../Logs/";
    static readonly RaycastHit[] hits = new RaycastHit[128];
    static readonly Dictionary<Collider,bool> supports = new Dictionary<Collider,bool>();
    static readonly Dictionary<Mesh,WorldSurfaceRepairs.Geometry> data = new Dictionary<Mesh,WorldSurfaceRepairs.Geometry>();
    struct WaterTriangle {public Vector3 a,b,c;}
    static readonly Dictionary<Vector2Int,List<WaterTriangle>> water = new Dictionary<Vector2Int,List<WaterTriangle>>();
    static readonly List<WaterTriangle> largeWater = new List<WaterTriangle>();
    static bool WaterMaterial(Material m){if(!m)return false;string s=(m.name+"/"+m.shader.name).ToLowerInvariant();return s.Contains("water")||s.Contains("pond")||s.Contains("river")||s.Contains("lake");}
    static void BuildWater()
    {
        water.Clear();largeWater.Clear();
        foreach(var f in All<MeshFilter>().Where(f=>f.sharedMesh&&f.gameObject.activeInHierarchy))
        {
            var renderer=f.GetComponent<Renderer>();if(!renderer||!renderer.enabled)continue;
            var materials=renderer.sharedMaterials;if(!materials.Any(WaterMaterial))continue;
            var vertices=Geometry(f.sharedMesh).vertices;
            using(var arrays=MeshUtility.AcquireReadOnlyMeshData(f.sharedMesh))
            {
                var mesh=arrays[0];
                for(int sub=0;sub<mesh.subMeshCount&&sub<materials.Length;sub++)
                {
                    if(!WaterMaterial(materials[sub])||mesh.GetSubMesh(sub).topology!=MeshTopology.Triangles)continue;
                    using(var indices=new Unity.Collections.NativeArray<int>(mesh.GetSubMesh(sub).indexCount,Unity.Collections.Allocator.Temp))
                    {
                        mesh.GetIndices(indices,sub);
                        for(int i=0;i<indices.Length;i+=3)
                        {
                            var tri=new WaterTriangle{a=f.transform.TransformPoint(vertices[indices[i]]),b=f.transform.TransformPoint(vertices[indices[i+1]]),c=f.transform.TransformPoint(vertices[indices[i+2]])};
                            var face=Vector3.Cross(tri.b-tri.a,tri.c-tri.a);float det=face.y;if(Mathf.Abs(det)<.00001f||Mathf.Abs(face.y)<face.magnitude*.3f)continue;
                            int x0=Mathf.FloorToInt(Mathf.Min(tri.a.x,Mathf.Min(tri.b.x,tri.c.x))/8),x1=Mathf.FloorToInt(Mathf.Max(tri.a.x,Mathf.Max(tri.b.x,tri.c.x))/8);
                            int z0=Mathf.FloorToInt(Mathf.Min(tri.a.z,Mathf.Min(tri.b.z,tri.c.z))/8),z1=Mathf.FloorToInt(Mathf.Max(tri.a.z,Mathf.Max(tri.b.z,tri.c.z))/8);
                            if((x1-x0+1)*(z1-z0+1)>10000){largeWater.Add(tri);continue;}
                            for(int x=x0;x<=x1;x++)for(int z=z0;z<=z1;z++){var key=new Vector2Int(x,z);if(!water.TryGetValue(key,out var bucket)){bucket=new List<WaterTriangle>();water.Add(key,bucket);}bucket.Add(tri);}
                        }
                    }
                }
            }
        }
    }
    static bool WaterAt(Vector3 p,out float y)
    {
        y=float.NegativeInfinity;var key=new Vector2Int(Mathf.FloorToInt(p.x/8),Mathf.FloorToInt(p.z/8));
        var candidates=water.TryGetValue(key,out var bucket)?bucket:largeWater;
        foreach(var tri in candidates.Concat(largeWater.Count>0&&candidates!=largeWater?largeWater:Enumerable.Empty<WaterTriangle>()))
        {
            float abx=tri.b.x-tri.a.x,abz=tri.b.z-tri.a.z,acx=tri.c.x-tri.a.x,acz=tri.c.z-tri.a.z,det=abx*acz-abz*acx;
            float dx=p.x-tri.a.x,dz=p.z-tri.a.z,u=(dx*acz-dz*acx)/det,v=(abx*dz-abz*dx)/det;
            if(u<-.0001f||v<-.0001f||u+v>1.0001f)continue;
            float height=tri.a.y+u*(tri.b.y-tri.a.y)+v*(tri.c.y-tri.a.y);y=Mathf.Max(y,height);
        }
        return !float.IsNegativeInfinity(y);
    }
    static readonly Dictionary<MeshCollider,bool[]> waterSubmeshes=new Dictionary<MeshCollider,bool[]>();
    static bool WaterHit(RaycastHit hit)
    {
        if(!(hit.collider is MeshCollider c)||!c.sharedMesh||hit.triangleIndex<0)return false;
        if(!waterSubmeshes.TryGetValue(c,out var flags))
        {
            var f=c.GetComponent<MeshFilter>();var r=c.GetComponent<Renderer>();
            if(!f||!r||f.sharedMesh!=c.sharedMesh){var lod=c.GetComponentInParent<LODGroup>();r=lod?lod.GetLODs().SelectMany(l=>l.renderers).FirstOrDefault(x=>x&&x.GetComponent<MeshFilter>()&&x.GetComponent<MeshFilter>().sharedMesh==c.sharedMesh):null;}
            flags=new bool[c.sharedMesh.subMeshCount];if(r){var mats=r.sharedMaterials;for(int i=0;i<flags.Length&&i<mats.Length;i++)flags[i]=WaterMaterial(mats[i]);}waterSubmeshes.Add(c,flags);
        }
        int first=0;for(int i=0;i<flags.Length;i++){int count=(int)c.sharedMesh.GetIndexCount(i)/3;if(hit.triangleIndex<first+count)return flags[i];first+=count;}return false;
    }
    static bool OnWater(Vector3 p)=>WaterAt(p,out float y)&&(!Ground(p,out var h)||y>h.point.y+.025f);
    static bool BushGround(Vector3 p,out RaycastHit hit)
    {
        bool found=Ground(p,out hit);if(found&&p.y-hit.point.y<4)return true;
        var method=typeof(HandleUtility).GetMethod("IntersectRayMesh",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
        if(method==null)return found;
        var ray=new Ray(p+Vector3.up*.5f,Vector3.down);float best=found?hit.point.y:float.NegativeInfinity;
        foreach(var f in All<MeshFilter>().Where(f=>f.sharedMesh&&f.gameObject.activeInHierarchy))
        {
            var r=f.GetComponent<Renderer>();if(!r||!r.enabled||r.shadowCastingMode==UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly||Category(f)!=Kind.None||f.GetComponentInParent<Game.ThirdPerson.ThirdPersonMotor>())continue;
            string name=(f.name+"/"+AssetDatabase.GetAssetPath(f.sharedMesh)).ToLowerInvariant();if(name.Contains("tree")||name.Contains("water")||name.Contains("river")||name.Contains("lake")||name.Contains("_sp"))continue;
            var lod=f.GetComponentInParent<LODGroup>();if(lod&&lod.GetLODs().Length>0&&!lod.GetLODs()[0].renderers.Contains(r))continue;
            if(!r.bounds.IntersectRay(ray,out float distance)||distance>21)continue;
            object[] args={ray,f.sharedMesh,f.transform.localToWorldMatrix,null};
            if(!(bool)method.Invoke(null,args))continue;var h=(RaycastHit)args[3];
            if(Mathf.Abs(h.normal.y)<.3f||h.point.y>p.y+.4f||h.point.y<=best)continue;
            int first=0;bool liquid=false;var mats=r.sharedMaterials;
            for(int i=0;i<f.sharedMesh.subMeshCount;i++){int count=(int)f.sharedMesh.GetIndexCount(i)/3;if(h.triangleIndex<first+count){liquid=i<mats.Length&&WaterMaterial(mats[i]);break;}first+=count;}
            if(liquid)continue;if(h.normal.y<0)h.normal=-h.normal;hit=h;best=h.point.y;found=true;
        }
        return found;
    }
    static T[] All<T>() where T:Component => SceneManager.GetActiveScene().GetRootGameObjects().SelectMany(r=>r.GetComponentsInChildren<T>(true)).ToArray();
    static Kind Category(string label)
    {
        string s=label.ToLowerInvariant();
        if(s.Contains("waterlily")||s.Contains("water plants")||s.Contains("cattail")||s.Contains("reed")||s.Contains("tree")||s.Contains("ivy")||s.Contains("vine"))return Kind.None;
        if(s.Contains("leaves_cluster")||s.Contains("leaves_setdressing"))return Kind.Leaves;
        if(s.Contains("grass")||s.Contains("village_scatter")||s.Contains("fern")||s.Contains("weed"))return Kind.Grass;
        if(s.Contains("bush"))return Kind.Bush;
        return Kind.None;
    }
    static Kind Category(MeshFilter f) => Category(f.name+"/"+f.sharedMesh.name+"/"+Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(f.sharedMesh)));
    static MeshFilter[] Filters() => All<MeshFilter>().Where(f=>f.sharedMesh&&f.gameObject.activeInHierarchy&&Category(f)!=Kind.None&&!f.GetComponentInParent<Game.ThirdPerson.ThirdPersonMotor>()).ToArray();
    static WorldSurfaceRepairs.Geometry Geometry(Mesh mesh)
    {
        if(!data.TryGetValue(mesh,out var geometry)){geometry=WorldSurfaceRepairs.Read(mesh);data.Add(mesh,geometry);}return geometry;
    }
    static Mesh Original(MeshFilter f)
    {
        var path=AssetDatabase.GetAssetPath(f.sharedMesh);
        if(!path.Contains("/GroundedGrass/")&&!path.Contains("/GroundedDecor/"))return f.sharedMesh;
        var source=PrefabUtility.GetCorrespondingObjectFromOriginalSource(f);
        return source&&source.sharedMesh?source.sharedMesh:f.sharedMesh;
    }
    static bool Support(Collider c)
    {
        if(supports.TryGetValue(c,out bool result))return result;
        result=c.enabled&&!c.isTrigger&&!(c is CharacterController)&&!c.GetComponentInParent<Game.ThirdPerson.ThirdPersonMotor>()&&!c.GetComponentInParent<Game.World.HouseDoor>();
        if(c.attachedRigidbody&&!c.attachedRigidbody.isKinematic)result=false;
        string label=c.name;
        var lod=c.GetComponentInParent<LODGroup>();if(lod)label+="/"+lod.name;
        if(c is MeshCollider mc&&mc.sharedMesh)label+="/"+mc.sharedMesh.name+"/"+AssetDatabase.GetAssetPath(mc.sharedMesh);
        string s=label.ToLowerInvariant();
        if(Category(label)!=Kind.None||s.Contains("treewood")||s.Contains("tree")||s.Contains("water")||s.Contains("river")||s.Contains("lake")||s.Contains("_sp")||s.Contains("shadowproxy"))result=false;
        if(c is TerrainCollider)result=c.enabled;
        supports[c]=result;return result;
    }
    static bool Ground(Vector3 p,out RaycastHit best)
    {
        best=default;float high=float.NegativeInfinity;
        int count;bool backfaces=Physics.queriesHitBackfaces;
        try{Physics.queriesHitBackfaces=true;count=Physics.RaycastNonAlloc(p+Vector3.up*.6f,Vector3.down,hits,20.6f,~0,QueryTriggerInteraction.Ignore);}
        finally{Physics.queriesHitBackfaces=backfaces;}
        for(int i=0;i<count;i++)
        {
            var h=hits[i];if(h.collider&&Mathf.Abs(h.normal.y)>.3f&&h.point.y<=p.y+.04f&&h.point.y>high&&Support(h.collider)&&!WaterHit(h)){if(h.normal.y<0)h.normal=-h.normal;high=h.point.y;best=h;}
        }
        return best.collider;
    }
    static Vector3 Base(MeshFilter f,Vector3[] vertices,int[] card)
    {
        float y=card.Min(i=>f.transform.TransformPoint(vertices[i]).y);Vector3 sum=Vector3.zero;int count=0;
        foreach(int i in card){var p=f.transform.TransformPoint(vertices[i]);if(p.y<=y+.02f){sum+=p;count++;}}
        var anchor=sum/Mathf.Max(1,count);anchor.y=y;return anchor;
    }
    static Transform BushRoot(MeshFilter f)
    {
        var lod=f.GetComponentInParent<LODGroup>();
        if(lod)
        {
            var owned=lod.GetComponentsInChildren<MeshFilter>(true).Where(m=>m.sharedMesh&&m.GetComponentInParent<LODGroup>()==lod).ToArray();
            if(Category(lod.name)==Kind.Bush||(owned.Length>0&&owned.All(m=>Category(m)==Kind.Bush)))return lod.transform;
        }
        var prefab=PrefabUtility.GetNearestPrefabInstanceRoot(f.gameObject);
        return prefab&&Category(prefab.name)==Kind.Bush?prefab.transform:f.transform;
    }
    static bool BushAnchor(Transform root,MeshFilter f,out Vector3 p)
    {
        var geometry=Geometry(f.sharedMesh);var world=geometry.vertices.Select(f.transform.TransformPoint).ToArray();
        var min=world.Aggregate(Vector3.one*float.PositiveInfinity,Vector3.Min);var max=world.Aggregate(Vector3.one*float.NegativeInfinity,Vector3.Max);
        if(max.x-min.x>14||max.z-min.z>14||max.y-min.y>6){p=default;return false;}
        p=root.position;
        if(p.x<min.x-.5f||p.x>max.x+.5f||p.z<min.z-.5f||p.z>max.z+.5f||p.y>min.y+.5f)
        {
            var bottom=world.Where(v=>v.y<=min.y+.03f).ToArray();p=bottom.Aggregate(Vector3.zero,(a,b)=>a+b)/bottom.Length;p.y=min.y;
        }
        return true;
    }
    static void Record(UnityEngine.Object obj){EditorUtility.SetDirty(obj);if(PrefabUtility.IsPartOfPrefabInstance(obj))PrefabUtility.RecordPrefabInstancePropertyModifications(obj);}
    static void Reset(){data.Clear();supports.Clear();waterSubmeshes.Clear();Physics.SyncTransforms();BuildWater();}
    static Mesh Baseline(MeshFilter f)
    {
        if(Category(f)==Kind.Grass){string id=Hash128.Compute(GlobalObjectId.GetGlobalObjectIdSlow(f).ToString()).ToString();var old=AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Game/Collision/GroundedGrass/Grass_"+id+".asset");if(old)return old;}
        return Original(f);
    }
    static string HierarchyKey(Transform t)=>t.parent?HierarchyKey(t.parent)+"/"+t.name+"#"+t.GetSiblingIndex():t.name+"#"+t.GetSiblingIndex();
    static Dictionary<string,Vector3> BushBaseline()
    {
        var result=new Dictionary<string,Vector3>();var folders=Directory.GetDirectories(Logs+"GroundDecorBackups").OrderBy(d=>d).ToArray();if(folders.Length==0)return result;
        string source=folders[0]+"/Demo.unity",temporary="Assets/Game/Editor/GroundDecorBaselinePreview.unity";
        File.Copy(source,temporary,true);AssetDatabase.ImportAsset(temporary,ImportAssetOptions.ForceSynchronousImport);
        var preview=EditorSceneManager.OpenPreviewScene(temporary);
        try{foreach(var root in preview.GetRootGameObjects())foreach(var t in root.GetComponentsInChildren<Transform>(true))result[HierarchyKey(t)]=t.position;}
        finally{EditorSceneManager.ClosePreviewScene(preview);AssetDatabase.DeleteAsset(temporary);}
        return result;
    }
    static bool ProjectLeaf(MeshFilter f,Vector3[] vertices,int[] card)
    {
        var world=card.Select(i=>f.transform.TransformPoint(vertices[i])).ToArray();
        var center=world.Aggregate(Vector3.zero,(a,b)=>a+b)/world.Length;
        if(!Ground(center,out var support))return false;
        Vector3 normal=Vector3.zero;float area=0;
        for(int i=0;i<world.Length;i++)for(int j=i+1;j<world.Length;j++)
        {
            var cross=Vector3.Cross(world[i]-center,world[j]-center);
            if(cross.sqrMagnitude>area){area=cross.sqrMagnitude;normal=cross;}
        }
        if(area<1e-12f)return false;
        normal.Normalize();if(normal.y<0)normal=-normal;
        var rotation=Quaternion.FromToRotation(normal,support.normal);
        var rotated=world.Select(v=>support.point+rotation*(v-center)).ToArray();
        var mean=rotated.Aggregate(Vector3.zero,(a,b)=>a+b)/rotated.Length;
        var relief=rotated.Select(v=>v.y-(mean.y-(support.normal.x*(v.x-mean.x)+support.normal.z*(v.z-mean.z))/support.normal.y)).ToArray();
        float low=relief.Min();var heights=new float[world.Length];
        for(int i=0;i<rotated.Length;i++)
        {
            // The source card's height is only used to find its original support.
            var probe=rotated[i]+Vector3.up*.03f;
            heights[i]=Ground(probe,out var h)?h.point.y:support.point.y;
        }
        bool ledge=heights.Max()-heights.Min()>.6f;
        for(int i=0;i<card.Length;i++)
        {
            var point=rotated[i];
            point.y=ledge?point.y-low+.008f:heights[i]+relief[i]-low+.008f;
            vertices[card[i]]=f.transform.InverseTransformPoint(point);
        }
        return true;
    }
    [MenuItem("Tools/World/Fix Floating Ground Decor")]
    public static void Apply()
    {
        if(EditorApplication.isPlaying)throw new InvalidOperationException("Exit Play mode to save grounding.");
        var scene=SceneManager.GetActiveScene();if(scene.path!="Assets/Scenes/Demo.unity")throw new Exception("Open Demo scene");
        Reset();var bushBaseline=BushBaseline();Directory.CreateDirectory(Logs);string backup=Logs+"GroundDecorBackups/"+DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");Directory.CreateDirectory(backup);
        EditorSceneManager.SaveScene(scene);File.Copy(scene.path,backup+"/Demo.unity");
        Directory.CreateDirectory(Folder);AssetDatabase.Refresh();
        var filters=Filters();var touched=new HashSet<LODGroup>();var report=new List<string>();
        int meshes=0,grassParts=0,leafParts=0,bushes=0,unsupported=0,protectedWater=0,residualContacts=0;
        AssetDatabase.StartAssetEditing();
        try
        {
            foreach(var f in filters.Where(f=>Category(f)==Kind.Grass||Category(f)==Kind.Leaves))
            {
                var kind=Category(f);var original=Original(f);var geometry=Geometry(original);var current=Geometry(f.sharedMesh);var baseline=Geometry(Baseline(f));
                var vertices=(Vector3[])geometry.vertices.Clone();int changed=0;
                foreach(var card in geometry.cards.Where(c=>c.Length>=3))
                {
                    if(kind==Kind.Grass)
                    {
                        var p=Base(f,vertices,card);
                        if(OnWater(p)){if(baseline.vertices.Length==vertices.Length)foreach(int i in card)vertices[i]=baseline.vertices[i];protectedWater++;}
                        else if(!Ground(p,out var h)){if(baseline.vertices.Length==vertices.Length)foreach(int i in card)vertices[i]=baseline.vertices[i];unsupported++;}
                        else
                        {
                            float gap=p.y-h.point.y;if(gap>.035f&&gap<18f){var delta=f.transform.InverseTransformVector(Vector3.up*(-gap-.012f));foreach(int i in card)vertices[i]+=delta;}
                        }
                    }
                    else
                    {
                        var center=card.Select(i=>f.transform.TransformPoint(vertices[i])).Aggregate(Vector3.zero,(a,b)=>a+b)/card.Length;
                        if(OnWater(center)){if(baseline.vertices.Length==vertices.Length)foreach(int i in card)vertices[i]=baseline.vertices[i];protectedWater++;}
                        else if(!ProjectLeaf(f,vertices,card)){if(baseline.vertices.Length==vertices.Length)foreach(int i in card)vertices[i]=baseline.vertices[i];unsupported++;}
                    }
                    if(current.vertices.Length!=vertices.Length||card.Any(i=>f.transform.TransformVector(vertices[i]-current.vertices[i]).sqrMagnitude>.000625f))changed++;
                }
                if(changed==0)continue;
                string id=Hash128.Compute(GlobalObjectId.GetGlobalObjectIdSlow(f).ToString()).ToString();string path=Folder+"/"+kind+"_"+id+".asset";
                var mesh=WorldSurfaceRepairs.Clone(original,vertices);mesh.name=original.name+"_GroundedDecor";
                if(kind==Kind.Leaves){mesh.RecalculateNormals();if(mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0))mesh.RecalculateTangents();}
                var existing=AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if(existing){File.Copy(path,backup+"/"+Path.GetFileName(path),true);EditorUtility.CopySerialized(mesh,existing);UnityEngine.Object.DestroyImmediate(mesh);mesh=existing;Record(mesh);}
                else AssetDatabase.CreateAsset(mesh,path);
                f.sharedMesh=mesh;data.Remove(mesh);Record(f);var renderer=f.GetComponent<Renderer>();if(renderer){renderer.localBounds=mesh.bounds;Record(renderer);}
                var lod=f.GetComponentInParent<LODGroup>();if(lod)touched.Add(lod);
                meshes++;if(kind==Kind.Grass)grassParts+=changed;else leafParts+=changed;
                report.Add($"{kind} {f.name} changedParts={changed} source={AssetDatabase.GetAssetPath(original)}");
            }
            foreach(var group in filters.Where(f=>Category(f)==Kind.Bush).GroupBy(BushRoot))
            {
                if(bushBaseline.TryGetValue(HierarchyKey(group.Key),out var oldPosition))group.Key.position=oldPosition;
                Record(group.Key);
                var f=group.First();var lod=group.Key.GetComponent<LODGroup>();
                if(lod&&lod.GetLODs().Length>0){var primary=lod.GetLODs()[0].renderers.Where(r=>r).Select(r=>r.GetComponent<MeshFilter>()).FirstOrDefault(m=>m&&m.sharedMesh);if(primary)f=primary;}
                if(!BushAnchor(group.Key,f,out var p)||OnWater(p)||!BushGround(p,out var h))continue;
                float gap=p.y-h.point.y;if(gap<=.06f||gap>18)continue;
                group.Key.position-=Vector3.up*(gap+.012f);Record(group.Key);
                foreach(var rb in group.Key.GetComponentsInChildren<Rigidbody>(true)){rb.isKinematic=true;rb.useGravity=false;Record(rb);}
                if(lod)touched.Add(lod);bushes++;report.Add($"Bush {group.Key.name} lowered={gap:F3}");
            }
            foreach(var f in filters.Where(f=>Category(f)==Kind.Leaves))
            {
                var geometry=Geometry(f.sharedMesh);var vertices=(Vector3[])geometry.vertices.Clone();int fixedContacts=0;
                foreach(var card in geometry.cards.Where(c=>c.Length>=3))
                {
                    var center=card.Select(i=>f.transform.TransformPoint(vertices[i])).Aggregate(Vector3.zero,(a,b)=>a+b)/card.Length;if(OnWater(center))continue;
                    float gap=float.PositiveInfinity;
                    foreach(int i in card){var p=f.transform.TransformPoint(vertices[i]);if(Ground(p,out var h))gap=Mathf.Min(gap,p.y-h.point.y);}
                    if(float.IsPositiveInfinity(gap)||gap<=.035f||gap>6)continue;
                    var delta=f.transform.InverseTransformVector(Vector3.up*(-gap+.008f));foreach(int i in card)vertices[i]+=delta;fixedContacts++;
                }
                if(fixedContacts==0)continue;
                var mesh=f.sharedMesh;
                if(!AssetDatabase.GetAssetPath(mesh).StartsWith(Folder,StringComparison.Ordinal))
                {
                    string id=Hash128.Compute(GlobalObjectId.GetGlobalObjectIdSlow(f).ToString()).ToString();mesh=WorldSurfaceRepairs.Clone(mesh,vertices);AssetDatabase.CreateAsset(mesh,Folder+"/Leaves_"+id+".asset");f.sharedMesh=mesh;Record(f);
                }
                else mesh.vertices=vertices;
                mesh.RecalculateBounds();mesh.RecalculateNormals();if(mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0))mesh.RecalculateTangents();Record(mesh);data.Remove(mesh);
                var renderer=f.GetComponent<Renderer>();if(renderer){renderer.localBounds=mesh.bounds;Record(renderer);}residualContacts+=fixedContacts;
                var lod=f.GetComponentInParent<LODGroup>();if(lod)touched.Add(lod);
            }
        }
        finally {AssetDatabase.StopAssetEditing();}
        foreach(var lod in touched){lod.RecalculateBounds();Record(lod);}
        Physics.SyncTransforms();AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);
        report.Insert(0,$"changedMeshes={meshes} correctedGrassParts={grassParts} correctedLeafParts={leafParts} correctedBushRoots={bushes} residualLeafContacts={residualContacts} unsupportedSamples={unsupported} protectedWaterParts={protectedWater}");
        File.WriteAllLines(Logs+"GroundDecorRepairResults.txt",report);Debug.Log("GROUND_DECOR_REPAIRED "+report[0]);
        Reset();
    }
    [Serializable] sealed class Pose {public Vector3 position,target;}
    [MenuItem("Tools/World/Capture Floating Leaves Before")]
    public static void CaptureBefore()
    {
        Reset();var f=Filters().First(m=>Category(m)==Kind.Leaves&&m.name.Contains("_02"));var geometry=Geometry(Original(f));float largest=0;Vector3 center=default;
        foreach(var v in geometry.vertices.Select(f.transform.TransformPoint))if(Ground(v,out var h)&&v.y-h.point.y>largest){largest=v.y-h.point.y;center=(v+h.point)*.5f;}
        var pose=new Pose{position=center+new Vector3(1.2f,1.1f,-1.8f),target=center};
        File.WriteAllText(Logs+"GroundDecorInspectionPose.json",JsonUtility.ToJson(pose));var prior=f.sharedMesh;try{f.sharedMesh=Original(f);Capture(pose,"before");}finally{f.sharedMesh=prior;}
    }
    [MenuItem("Tools/World/Capture Floating Leaves After")]
    public static void CaptureAfter()=>Capture(JsonUtility.FromJson<Pose>(File.ReadAllText(Logs+"GroundDecorInspectionPose.json")),"after");
    static void Capture(Pose pose,string label)
    {
        var obj=new GameObject("Ground decoration inspection camera"){hideFlags=HideFlags.HideAndDontSave};var camera=obj.AddComponent<Camera>();
        if(Camera.main)camera.CopyFrom(Camera.main);
        camera.enabled=false;camera.nearClipPlane=.03f;camera.farClipPlane=120;camera.fieldOfView=55;camera.useOcclusionCulling=false;camera.transform.position=pose.position;camera.transform.LookAt(pose.target);
        var rt=new RenderTexture(900,650,24);var image=new Texture2D(900,650,TextureFormat.RGB24,false);var previous=RenderTexture.active;
        try{rt.Create();UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera,new UnityEngine.Rendering.Universal.UniversalRenderPipeline.SingleCameraRequest{destination=rt});RenderTexture.active=rt;image.ReadPixels(new Rect(0,0,900,650),0,0);image.Apply();File.WriteAllBytes(Logs+"GroundDecor_"+label+".png",image.EncodeToPNG());}
        finally{RenderTexture.active=previous;rt.Release();UnityEngine.Object.DestroyImmediate(rt);UnityEngine.Object.DestroyImmediate(image);UnityEngine.Object.DestroyImmediate(obj);}
    }

    [MenuItem("Tools/World/Analyze Floating Ground Decor")]
    public static void Analyze()
    {
        Reset();var lines=new List<string>();int grass=0,leaves=0,bushes=0,unsupported=0,leafContactsMissing=0;float maxGap=0;
        foreach(var f in Filters())
        {
            var kind=Category(f);if(kind==Kind.Bush)continue;
            var geometry=Geometry(f.sharedMesh);int floating=0,noGround=0;float largest=0;Vector3 example=default;string support="";
            foreach(var card in geometry.cards.Where(c=>c.Length>=3))
            {
                var center=card.Select(i=>f.transform.TransformPoint(geometry.vertices[i])).Aggregate(Vector3.zero,(a,b)=>a+b)/card.Length;
                if(OnWater(kind==Kind.Grass?Base(f,geometry.vertices,card):center))continue;
                float gap=0,minContact=float.PositiveInfinity;bool grounded=false;
                if(kind==Kind.Grass){var p=Base(f,geometry.vertices,card);if(Ground(p,out var h)){gap=p.y-h.point.y;grounded=true;if(gap>largest){example=p;support=h.collider.name;}}}
                else foreach(int i in card){var p=f.transform.TransformPoint(geometry.vertices[i]);if(Ground(p,out var h)){grounded=true;minContact=Mathf.Min(minContact,p.y-h.point.y);if(p.y-h.point.y>gap){gap=p.y-h.point.y;example=p;support=h.collider.name;}}}
                if(!grounded){noGround++;continue;}
                if(kind==Kind.Leaves&&minContact>.06f){leafContactsMissing++;lines.Add($"NO_LEAF_CONTACT {f.name} gap={minContact:F3} point={center:F3}");}
                if(gap>.08f){floating++;largest=Mathf.Max(largest,gap);}
            }
            unsupported+=noGround;if(kind==Kind.Grass)grass+=floating;else leaves+=floating;maxGap=Mathf.Max(maxGap,largest);
            if(floating>0)lines.Add($"{kind} {f.name} floatingParts={floating} maxGap={largest:F3} example={example:F3} support={support} mesh={AssetDatabase.GetAssetPath(f.sharedMesh)}");
        }
        foreach(var group in Filters().Where(f=>Category(f)==Kind.Bush).GroupBy(BushRoot))
        {
            var f=group.First();if(!BushAnchor(group.Key,f,out var p))continue;
            if(!OnWater(p)&&BushGround(p,out var h)&&p.y-h.point.y>.08f){bushes++;lines.Add($"Bush {group.Key.name} gap={p.y-h.point.y:F3} anchor={p:F3}");}
            foreach(var rb in group.Key.GetComponentsInChildren<Rigidbody>(true))lines.Add($"BODY {group.Key.name} kinematic={rb.isKinematic} gravity={rb.useGravity}");
        }
        lines.Insert(0,$"floatingGrassParts={grass} raisedLeafEdges={leaves} floatingLeafPartsWithoutContact={leafContactsMissing} floatingBushRoots={bushes} unsupportedParts={unsupported} maxEdgeGap={maxGap:F3}");
        Directory.CreateDirectory(Logs);File.WriteAllLines(Logs+"FloatingDecorAnalysis.txt",lines);Debug.Log("GROUND_DECOR_ANALYZED "+lines[0]);
    }
    [MenuItem("Tools/World/Validate Ground Decor Contact")]
    public static void Validate()
    {
        Analyze();string summary=File.ReadLines(Logs+"FloatingDecorAnalysis.txt").First();
        var values=summary.Split(' ').Select(s=>s.Split('=')).ToDictionary(p=>p[0],p=>p[1]);
        foreach(var key in new[]{"floatingGrassParts","floatingLeafPartsWithoutContact","floatingBushRoots"})if(int.Parse(values[key])!=0)throw new Exception("Unattached ground decoration remains: "+summary);
        File.WriteAllText(Logs+"GroundDecorContactValidation.txt",summary);Debug.Log("GROUND_DECOR_CONTACT_VALIDATED "+summary);
    }
}
