using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Game.World;

public static class OriginalDoorAuthoring
{
    const string Folder="Assets/Game/OriginalDoors";
    class Data {public Vector3[] v,n;public Vector2[] uv;public int[][] triangles;}
    class Frame {public int sub;public HashSet<int> vertices;public Vector3 right,normal,center;public float minX,maxX,minY,maxY,depth,area;public float Width=>maxX-minX;public float Height=>maxY-minY;}
    class Door {public Frame frame,innerFrame;public Vector3 right,normal,hinge;public float minX,maxX,minY,maxY,minZ,maxZ;public List<int>[] selected;public Mesh leaf,mirrored;public List<Vector2> outline;public bool hingeAtMax;public float Width=>maxX-minX;public float Height=>maxY-minY;}
    class Vertex {public Vector3 p,n;public Vector2 uv;public Vertex(Vector3 p,Vector3 n,Vector2 uv){this.p=p;this.n=n;this.uv=uv;}public static Vertex Lerp(Vertex a,Vertex b,float t)=>new Vertex(Vector3.Lerp(a.p,b.p,t),Vector3.Lerp(a.n,b.n,t).normalized,Vector2.Lerp(a.uv,b.uv,t));}
    static Data Read(Mesh mesh)
    {
        using(var data=MeshUtility.AcquireReadOnlyMeshData(mesh)){
            var m=data[0];var d=new Data();
            using(var p=new NativeArray<Vector3>(m.vertexCount,Allocator.Temp))using(var n=new NativeArray<Vector3>(m.vertexCount,Allocator.Temp))using(var uv=new NativeArray<Vector2>(m.vertexCount,Allocator.Temp)){
                m.GetVertices(p);if(m.HasVertexAttribute(VertexAttribute.Normal))m.GetNormals(n);if(m.HasVertexAttribute(VertexAttribute.TexCoord0))m.GetUVs(0,uv);d.v=p.ToArray();d.n=n.ToArray();d.uv=uv.ToArray();
            }
            d.triangles=new int[m.subMeshCount][];
            for(int s=0;s<m.subMeshCount;s++)using(var t=new NativeArray<int>(m.GetSubMesh(s).indexCount,Allocator.Temp)){m.GetIndices(t,s);d.triangles[s]=t.ToArray();}
            return d;
        }
    }
    static int Root(int[] p,int a){while(p[a]!=a){p[a]=p[p[a]];a=p[a];}return a;}
    static bool DoorAtlas(Material m)
    {
        if(!m||!m.HasProperty("_BaseMap")||!m.GetTexture("_BaseMap"))return false;
        return AssetDatabase.GetAssetPath(m.GetTexture("_BaseMap")).IndexOf("Atlas",StringComparison.OrdinalIgnoreCase)>=0;
    }
    static List<Frame> Frames(Data d,Material[] materials)
    {
        var frames=new List<Frame>();
        for(int s=0;s<d.triangles.Length;s++){
            if(s>=materials.Length||!DoorAtlas(materials[s]))continue;
            int[] t=d.triangles[s],parent=Enumerable.Range(0,d.v.Length).ToArray();
            for(int i=0;i<t.Length;i+=3){parent[Root(parent,t[i+1])]=Root(parent,t[i]);parent[Root(parent,t[i+2])]=Root(parent,t[i]);}
            var components=new Dictionary<int,List<int>>();
            for(int i=0;i<t.Length;i+=3){int k=Root(parent,t[i]);if(!components.TryGetValue(k,out var list)){list=new List<int>();components.Add(k,list);}list.Add(t[i]);list.Add(t[i+1]);list.Add(t[i+2]);}
            foreach(var component in components.Values){
                if(component.Count<30||component.Count>108)continue;
                var ids=new HashSet<int>(component);
                
                Vector3 normal=Vector3.zero;float area=0;
                for(int i=0;i<component.Count;i+=3){var cross=Vector3.Cross(d.v[component[i+1]]-d.v[component[i]],d.v[component[i+2]]-d.v[component[i]]);normal+=cross;area+=cross.magnitude*.5f;}
                if(normal.sqrMagnitude<.001f)continue;normal.Normalize();if(Mathf.Abs(normal.y)>.2f)continue;normal=Vector3.ProjectOnPlane(normal,Vector3.up).normalized;
                var right=Vector3.Cross(Vector3.up,normal).normalized;
                var f=new Frame{sub=s,vertices=ids,right=right,normal=normal,area=area,minX=float.MaxValue,maxX=float.MinValue,minY=float.MaxValue,maxY=float.MinValue};
                float lo=float.MaxValue,hi=float.MinValue;
                foreach(int i in ids){var v=d.v[i];float x=Vector3.Dot(v,right),z=Vector3.Dot(v,normal);f.minX=Mathf.Min(f.minX,x);f.maxX=Mathf.Max(f.maxX,x);f.minY=Mathf.Min(f.minY,v.y);f.maxY=Mathf.Max(f.maxY,v.y);lo=Mathf.Min(lo,z);hi=Mathf.Max(hi,z);}
                if(f.Width<.75f||f.Width>2.8f||f.Height<1.9f||f.Height>3.1f||hi-lo>.12f)continue;
                float ratio=area/(f.Width*f.Height);if(ratio<.10f||ratio>.38f)continue;
                f.depth=(lo+hi)*.5f;f.center=right*((f.minX+f.maxX)*.5f)+Vector3.up*((f.minY+f.maxY)*.5f)+normal*f.depth;frames.Add(f);
            }
        }
        return frames;
    }
    static List<Door> Detect(Data d,Material[] materials)
    {
        var candidates=Frames(d,materials);var result=new List<Door>();
        foreach(var frame in candidates.OrderByDescending(f=>f.Width)){
            if(result.Any(x=>Vector3.Distance(x.frame.center,frame.center)<.35f&&Mathf.Abs(Vector3.Dot(x.normal,frame.normal))>.95f))continue;
            var inner=candidates.Where(f=>f!=frame&&f.Width<frame.Width-.06f&&Vector3.Distance(f.center,frame.center)<.25f&&Vector3.Dot(f.normal,frame.normal)>.95f).OrderBy(f=>f.Width).FirstOrDefault();
            if(inner==null)continue;
            var door=new Door{frame=frame,innerFrame=inner,right=frame.right,normal=frame.normal,minX=inner.minX-.018f,maxX=inner.maxX+.018f,minY=inner.minY-.012f,maxY=inner.maxY+.04f,minZ=inner.depth-.75f,maxZ=Mathf.Max(frame.depth,inner.depth)+.15f};
            door.outline=Outline(inner.vertices.Select(i=>new Vector2(Vector3.Dot(d.v[i],door.right),d.v[i].y)));
            door.hinge=door.right*door.minX+Vector3.up*door.minY+door.normal*inner.depth;
            door.selected=new List<int>[d.triangles.Length];
            for(int s=0;s<d.triangles.Length;s++){
                door.selected[s]=new List<int>();var t=d.triangles[s];
                for(int i=0;i<t.Length;i+=3){
                    if(s==frame.sub&&frame.vertices.Contains(t[i])&&frame.vertices.Contains(t[i+1])&&frame.vertices.Contains(t[i+2]))continue;
                    if(Inside(d.v[t[i]],door)&&Inside(d.v[t[i+1]],door)&&Inside(d.v[t[i+2]],door)){door.selected[s].Add(t[i]);door.selected[s].Add(t[i+1]);door.selected[s].Add(t[i+2]);}
                }
            }
            if(door.selected.Sum(t=>t.Count)<60)continue;
            result.Add(door);
        }
        // Several originals use a two-triangle, unornamented wooden door panel.
        // Its authored atlas rectangle distinguishes it from windows and structural boards.
        for(int sub=0;sub<d.triangles.Length;sub++){
            if(sub>=materials.Length||!DoorAtlas(materials[sub]))continue;
            var t=d.triangles[sub];var parents=Enumerable.Range(0,d.v.Length).ToArray();
            for(int i=0;i<t.Length;i+=3){parents[Root(parents,t[i+1])]=Root(parents,t[i]);parents[Root(parents,t[i+2])]=Root(parents,t[i]);}
            var parts=new Dictionary<int,List<int>>();for(int i=0;i<t.Length;i+=3){int k=Root(parents,t[i]);if(!parts.ContainsKey(k))parts[k]=new List<int>();parts[k].Add(t[i]);parts[k].Add(t[i+1]);parts[k].Add(t[i+2]);}
            foreach(var part in parts.Values){
                if(part.Count!=6)continue;var ids=new HashSet<int>(part);if(ids.Count!=4)continue;
                var lowUV=Vector2.one*999;var highUV=-lowUV;foreach(int i in ids){lowUV=Vector2.Min(lowUV,d.uv[i]);highUV=Vector2.Max(highUV,d.uv[i]);}
                if(Vector2.Distance(lowUV,new Vector2(.264f,.254f))>.009f||Vector2.Distance(highUV,new Vector2(.373f,.494f))>.009f)continue;
                var normal=Vector3.Cross(d.v[part[1]]-d.v[part[0]],d.v[part[2]]-d.v[part[0]]).normalized;if(Mathf.Abs(normal.y)>.1f)continue;
                normal=Vector3.ProjectOnPlane(normal,Vector3.up).normalized;var right=Vector3.Cross(Vector3.up,normal);
                var low=Vector3.one*99999;var high=-low;
                foreach(int i in ids){var v=d.v[i];var p=new Vector3(Vector3.Dot(v,right),v.y,Vector3.Dot(v,normal));low=Vector3.Min(low,p);high=Vector3.Max(high,p);}
                if(high.x-low.x<.7f||high.x-low.x>1.6f||high.y-low.y<1.8f||high.y-low.y>3.3f)continue;
                var center=right*((low.x+high.x)*.5f)+Vector3.up*((low.y+high.y)*.5f)+normal*((low.z+high.z)*.5f);
                if(result.Any(x=>Vector3.Distance(x.frame.center,center)<.5f))continue;
                var frame=new Frame{sub=sub,vertices=new HashSet<int>(),right=right,normal=normal,center=center,minX=low.x,maxX=high.x,minY=low.y,maxY=high.y,depth=(low.z+high.z)*.5f};
                var door=new Door{frame=frame,right=right,normal=normal,minX=low.x-.002f,maxX=high.x+.002f,minY=low.y-.002f,maxY=high.y+.002f,minZ=frame.depth-.15f,maxZ=frame.depth+.15f};
                door.outline=Outline(ids.Select(i=>new Vector2(Vector3.Dot(d.v[i],right),d.v[i].y)));
                door.hinge=right*door.minX+Vector3.up*door.minY+normal*frame.depth;door.selected=new List<int>[d.triangles.Length];
                for(int s=0;s<door.selected.Length;s++)door.selected[s]=s==sub?new List<int>(part):new List<int>();
                result.Add(door);
            }
        }
        foreach(var door in result)CompleteHardware(d,door);
        return result;
    }
    static float Cross2(Vector2 a,Vector2 b)=>a.x*b.y-a.y*b.x;
    static List<Vector2> Outline(IEnumerable<Vector2> vertices)
    {
        var points=vertices.Distinct().OrderBy(p=>p.x).ThenBy(p=>p.y).ToArray();var hull=new List<Vector2>();
        foreach(var p in points){while(hull.Count>=2&&Cross2(hull[hull.Count-1]-hull[hull.Count-2],p-hull[hull.Count-1])<=0)hull.RemoveAt(hull.Count-1);hull.Add(p);}
        int lower=hull.Count;
        for(int i=points.Length-2;i>=0;i--){var p=points[i];while(hull.Count>lower&&Cross2(hull[hull.Count-1]-hull[hull.Count-2],p-hull[hull.Count-1])<=0)hull.RemoveAt(hull.Count-1);hull.Add(p);}
        if(hull.Count>1)hull.RemoveAt(hull.Count-1);
        // Keep a tiny overlap behind the authored leaf edge, avoiding light leaks at the jamb/arch.
        var center=hull.Aggregate(Vector2.zero,(sum,p)=>sum+p)/hull.Count;
        for(int i=0;i<hull.Count;i++)hull[i]=Vector2.MoveTowards(hull[i],center,.0025f);
        return hull;
    }
    static List<List<int>> Components(Data data,int sub)
    {
        var triangles=data.triangles[sub];var parent=Enumerable.Range(0,data.v.Length).ToArray();
        for(int i=0;i<triangles.Length;i+=3){parent[Root(parent,triangles[i+1])]=Root(parent,triangles[i]);parent[Root(parent,triangles[i+2])]=Root(parent,triangles[i]);}
        var parts=new Dictionary<int,List<int>>();
        for(int i=0;i<triangles.Length;i+=3){int root=Root(parent,triangles[i]);if(!parts.TryGetValue(root,out var part)){part=new List<int>();parts.Add(root,part);}part.Add(triangles[i]);part.Add(triangles[i+1]);part.Add(triangles[i+2]);}
        return parts.Values.ToList();
    }
    static float OutlineArea(List<Vector2> points)
    {
        float area=0;for(int i=0;i<points.Count;i++)area+=Cross2(points[i],points[(i+1)%points.Count]);return Mathf.Abs(area)*.5f;
    }
    static List<Vector2> FrameOpening(Data data,Frame frame,Vector3 right)
    {
        var edges=new Dictionary<(int,int),int>();var t=data.triangles[frame.sub];
        for(int i=0;i<t.Length;i+=3)if(frame.vertices.Contains(t[i])&&frame.vertices.Contains(t[i+1])&&frame.vertices.Contains(t[i+2]))
            for(int k=0;k<3;k++){int a=t[i+k],b=t[i+(k+1)%3];var key=a<b?(a,b):(b,a);if(!edges.ContainsKey(key))edges[key]=0;edges[key]++;}
        var graph=new Dictionary<int,List<int>>();float bottom=frame.minY+frame.Height*.025f;
        foreach(var edge in edges){
            if(edge.Value!=1)continue;int a=edge.Key.Item1,b=edge.Key.Item2;
            // An open-bottom U-shaped ring joins its inner and outer borders at the sill.
            if(data.v[a].y<=bottom&&data.v[b].y<=bottom)continue;
            if(!graph.ContainsKey(a))graph[a]=new List<int>();if(!graph.ContainsKey(b))graph[b]=new List<int>();graph[a].Add(b);graph[b].Add(a);
        }
        var seen=new HashSet<int>();var loops=new List<List<Vector2>>();
        foreach(int start in graph.Keys){
            if(!seen.Add(start))continue;var queue=new Queue<int>();queue.Enqueue(start);var points=new List<Vector2>();
            while(queue.Count>0){int a=queue.Dequeue();points.Add(new Vector2(Vector3.Dot(data.v[a],right),data.v[a].y));foreach(int b in graph[a])if(seen.Add(b))queue.Enqueue(b);}
            if(points.Count>=4){var hull=Outline(points);if(OutlineArea(hull)>.5f)loops.Add(hull);}
        }
        if(loops.Count<2)throw new Exception("Door frame inner border could not be isolated at "+frame.center);
        return loops.OrderBy(OutlineArea).First();
    }
    static bool InOutline(Vector3 point,Door door,float margin)
    {
        var p=new Vector2(Vector3.Dot(point,door.right),point.y);
        for(int i=0;i<door.outline.Count;i++){var a=door.outline[i];var edge=door.outline[(i+1)%door.outline.Count]-a;if(Cross2(edge,p-a)<-margin*edge.magnitude)return false;}
        return true;
    }
    static bool HandleUV(Vector2 uv)=>(uv.x>.75f&&uv.x<.90f&&uv.y>.89f)||(uv.x>.85f&&uv.x<.89f&&uv.y>.53f&&uv.y<.69f);
    static void CompleteHardware(Data data,Door door)
    {
        if(door.innerFrame!=null){
            door.outline=FrameOpening(data,door.innerFrame,door.right);
            door.minX=door.outline.Min(p=>p.x);door.maxX=door.outline.Max(p=>p.x);door.minY=door.outline.Min(p=>p.y);door.maxY=door.outline.Max(p=>p.y);
        }
        var parts=Enumerable.Range(0,data.triangles.Length).Select(sub=>Components(data,sub)).ToArray();
        var protectedVertices=new HashSet<int>();
        protectedVertices.UnionWith(door.frame.vertices);if(door.innerFrame!=null)protectedVertices.UnionWith(door.innerFrame.vertices);
        float initialPlane=Vector3.Dot(door.hinge,door.normal),leafPlane=initialPlane;
        int panelSub=-1;float bestArea=0;
        // Find an actual filled, planar wood panel; a narrow frame ring cannot be a seed.
        for(int sub=0;sub<parts.Length;sub++)foreach(var part in parts[sub]){
            if(part.Any(protectedVertices.Contains)||!part.All(i=>InOutline(data.v[i],door,.075f)))continue;
            var low=Vector3.one*99999;var high=-low;float area=0;
            foreach(int i in part){var p=Coordinates(data.v[i],door);low=Vector3.Min(low,p);high=Vector3.Max(high,p);}
            var size=high-low;if(size.x<door.Width*.10f||size.y<door.Height*.25f||size.z>.018f||low.z<initialPlane-.45f||high.z>initialPlane+.12f)continue;
            for(int i=0;i<part.Count;i+=3)area+=Mathf.Abs(Vector3.Dot(Vector3.Cross(data.v[part[i+1]]-data.v[part[i]],data.v[part[i+2]]-data.v[part[i]]),door.normal))*.5f;
            if(area<.60f*size.x*size.y||area<=bestArea)continue;
            bestArea=area;leafPlane=(low.z+high.z)*.5f;panelSub=sub;
        }
        if(panelSub<0)throw new Exception("No filled original door panel at "+door.frame.center);
        float leftHandle=0,rightHandle=0;
        for(int sub=0;sub<parts.Length;sub++){
            var complete=new List<int>();
            foreach(var part in parts[sub]){
                if(part.Any(protectedVertices.Contains))continue;
                // Stone/reveal atlas and the separate prop-door atlas must never be combined.
                if(panelSub!=door.frame.sub&&sub==door.frame.sub)continue;
                if(!part.All(i=>InOutline(data.v[i],door,.075f)))continue;
                var ids=new HashSet<int>(part);var low=Vector3.one*99999;var high=-low;
                foreach(int i in ids){var p=Coordinates(data.v[i],door);low=Vector3.Min(low,p);high=Vector3.Max(high,p);}
                var size=high-low;var center=(low+high)*.5f;bool metal=ids.Any(i=>HandleUV(data.uv[i]));
                if(size.z>.28f||low.z<leafPlane-(metal?.14f:.085f)||high.z>leafPlane+.20f)continue;
                complete.AddRange(part);
                float y=(center.y-door.minY)/door.Height;float side=(center.x-(door.minX+door.maxX)*.5f)/door.Width;
                if(!metal||y<.30f||y>.62f||Mathf.Abs(side)<.18f||size.x>door.Width*.48f||size.y>door.Height*.35f)continue;
                float area=0;for(int i=0;i<part.Count;i+=3)area+=Vector3.Cross(data.v[part[i+1]]-data.v[part[i]],data.v[part[i+2]]-data.v[part[i]]).magnitude*.5f;
                float score=area*(1+Mathf.Max(0,high.z-leafPlane)*8)*Mathf.Exp(-Mathf.Pow((y-.45f)/.14f,2));
                if(side<0)leftHandle+=score;else rightHandle+=score;
            }
            door.selected[sub]=complete;
        }
        // The latch/handle is the free edge; the hinge belongs on its opposite edge.
        door.hingeAtMax=leftHandle>rightHandle&&leftHandle>.00001f;
        door.hinge=door.right*(door.hingeAtMax?door.maxX:door.minX)+Vector3.up*door.minY+door.normal*leafPlane;
    }
    static Vector3 Coordinates(Vector3 p,Door door)=>new Vector3(Vector3.Dot(p,door.right),p.y,Vector3.Dot(p,door.normal));
    static bool Inside(Vector3 p,Door door){var q=Coordinates(p,door);return q.x>=door.minX-.001f&&q.x<=door.maxX+.001f&&q.y>=door.minY-.001f&&q.y<=door.maxY+.001f&&q.z>=door.minZ&&q.z<=door.maxZ;}
    static bool Eligible(LODGroup g)=>g.name.StartsWith("Preset_",StringComparison.Ordinal)||g.name.StartsWith("Composition_",StringComparison.Ordinal);
    static Mesh Store(Mesh mesh,string name)
    {
        mesh.name=name;string path=Folder+"/"+name+".asset";var old=AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if(old){
            // CopySerialized alone can leave the renderer using the old native vertex buffer.
            // Reassign geometry through Mesh APIs while retaining scene asset references.
            old.Clear();old.indexFormat=mesh.indexFormat;old.name=name;
            old.vertices=mesh.vertices;old.normals=mesh.normals;old.uv=mesh.uv;old.tangents=mesh.tangents;
            old.subMeshCount=mesh.subMeshCount;
            for(int s=0;s<mesh.subMeshCount;s++)old.SetTriangles(mesh.GetTriangles(s),s,false);
            old.bounds=mesh.bounds;old.UploadMeshData(false);
            UnityEngine.Object.DestroyImmediate(mesh);EditorUtility.SetDirty(old);return old;
        }
        AssetDatabase.CreateAsset(mesh,path);return mesh;
    }
    static Mesh LeafMesh(Data data,Door door,string name,bool mirror)
    {
        var map=new Dictionary<int,int>();var positions=new List<Vector3>();var normals=new List<Vector3>();var uv=new List<Vector2>();var subsets=new List<int[]>();
        for(int sub=0;sub<door.selected.Length;sub++){
            var output=new List<int>();
            foreach(int source in door.selected[sub]){
                if(!map.TryGetValue(source,out int index)){
                    index=positions.Count;map.Add(source,index);var p=data.v[source]-door.hinge;var n=data.n[source];
                    var local=new Vector3(Vector3.Dot(p,door.right),p.y,Vector3.Dot(p,door.normal));
                    var normal=new Vector3(Vector3.Dot(n,door.right),n.y,Vector3.Dot(n,door.normal));
                    if(mirror){local.x=-local.x;normal.x=-normal.x;}
                    positions.Add(local);normals.Add(normal);uv.Add(data.uv[source]);
                }
                output.Add(index);
            }
            if(mirror)for(int i=0;i<output.Count;i+=3){int a=output[i];output[i]=output[i+2];output[i+2]=a;}
            subsets.Add(output.ToArray());
        }
        var mesh=new Mesh{indexFormat=IndexFormat.UInt32};mesh.SetVertices(positions);mesh.SetNormals(normals);mesh.SetUVs(0,uv);mesh.subMeshCount=subsets.Count;
        for(int s=0;s<subsets.Count;s++)mesh.SetTriangles(subsets[s],s);mesh.RecalculateBounds();mesh.RecalculateTangents();
        return Store(mesh,name);
    }
    static float Plane(Vertex v,Door d,int plane)
    {
        var p=Coordinates(v.p,d);
        if(d.outline!=null&&d.outline.Count>=3){
            int n=d.outline.Count;
            if(plane<n){var a=d.outline[plane];var b=d.outline[(plane+1)%n];return Cross2(b-a,new Vector2(p.x,p.y)-a);}
            return plane==n?p.z-d.minZ:d.maxZ-p.z;
        }
        switch(plane){case 0:return p.x-d.minX;case 1:return d.maxX-p.x;case 2:return p.y-d.minY;case 3:return d.maxY-p.y;case 4:return p.z-d.minZ;default:return d.maxZ-p.z;}
    }
    static List<Vertex> Clip(List<Vertex> polygon,Door d,int plane,bool inside)
    {
        var output=new List<Vertex>();if(polygon.Count==0)return output;
        var a=polygon[polygon.Count-1];float da=Plane(a,d,plane);bool ina=inside?da>=0:da<=0;
        foreach(var b in polygon){float db=Plane(b,d,plane);bool inb=inside?db>=0:db<=0;
            if(ina!=inb){float t=da/(da-db);output.Add(Vertex.Lerp(a,b,t));}
            if(inb)output.Add(b);a=b;da=db;ina=inb;
        }
        return output;
    }
    static List<List<Vertex>> Subtract(List<Vertex> polygon,Door door)
    {
        var output=new List<List<Vertex>>();
        // Fast reject for geometry fully on one outside side of the doorway prism.
        int planes=door.outline!=null&&door.outline.Count>=3?door.outline.Count+2:6;
        for(int p=0;p<planes;p++)if(polygon.All(v=>Plane(v,door,p)<0)){output.Add(polygon);return output;}
        var remaining=polygon;
        for(int p=0;p<planes&&remaining.Count>=3;p++){
            var outside=Clip(remaining,door,p,false);if(outside.Count>=3)output.Add(outside);
            remaining=Clip(remaining,door,p,true);
        }
        return output;
    }
    static Mesh Cut(Data data,List<Door> doors,Material[] materials,string name,bool primary)
    {
        var positions=new List<Vector3>();var normals=new List<Vector3>();var uv=new List<Vector2>();var subsets=new List<int[]>();
        var frameVertices=Frames(data,materials).Where(f=>doors.Any(d=>Vector3.Distance(f.center,d.frame.center)<.08f&&f.Width>d.Width+.08f)).ToArray();
        for(int sub=0;sub<data.triangles.Length;sub++){
            var output=new List<int>();var indices=data.triangles[sub];
            var moving=new HashSet<(int,int,int)>();
            if(primary)foreach(var door in doors){var t=door.selected[sub];for(int k=0;k<t.Count;k+=3)moving.Add((t[k],t[k+1],t[k+2]));}
            for(int i=0;i<indices.Length;i+=3){
                int a=indices[i],b=indices[i+1],c=indices[i+2];
                if(moving.Contains((a,b,c)))continue;
                var polygon=new List<Vertex>{new Vertex(data.v[a],data.n[a],data.uv[a]),new Vertex(data.v[b],data.n[b],data.uv[b]),new Vertex(data.v[c],data.n[c],data.uv[c])};
                var pieces=new List<List<Vertex>>{polygon};
                bool frame=frameVertices.Any(f=>f.sub==sub&&f.vertices.Contains(a)&&f.vertices.Contains(b)&&f.vertices.Contains(c));
                bool horizontal=Mathf.Abs(Vector3.Cross(data.v[b]-data.v[a],data.v[c]-data.v[a]).normalized.y)>.65f;
                if(!frame)foreach(var door in doors){
                    // Metal strap caps are horizontal too. Only a surface at the sill can
                    // be supporting floor; upper caps must be removed from every static LOD.
                    bool supportingFloor=horizontal&&polygon.All(v=>v.p.y<=door.minY+.05f);
                    if(supportingFloor)continue;
                    var next=new List<List<Vertex>>();foreach(var piece in pieces)next.AddRange(Subtract(piece,door));pieces=next;if(pieces.Count==0)break;
                }
                foreach(var piece in pieces)for(int k=1;k<piece.Count-1;k++)foreach(var v in new[]{piece[0],piece[k],piece[k+1]}){
                    output.Add(positions.Count);positions.Add(v.p);normals.Add(v.n);uv.Add(v.uv);
                }
            }
            subsets.Add(output.ToArray());
        }
        var mesh=new Mesh{indexFormat=IndexFormat.UInt32};mesh.SetVertices(positions);mesh.SetNormals(normals);mesh.SetUVs(0,uv);mesh.subMeshCount=subsets.Count;
        for(int s=0;s<subsets.Count;s++)mesh.SetTriangles(subsets[s],s);mesh.RecalculateBounds();mesh.RecalculateTangents();
        return Store(mesh,name);
    }
    static string Key(Mesh mesh)
    {
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh,out string guid,out long id);return guid.Substring(0,8)+"_"+id.ToString().Replace("-","n");
    }
    [MenuItem("Tools/World/Rebuild Original Doors From Source")]
    public static void Rebuild()
    {
        if(EditorApplication.isPlaying)throw new Exception("Stop play mode first.");
        var replacements=new List<(MeshFilter filter,Mesh source)>();
        foreach(var group in UnityEngine.Object.FindObjectsByType<LODGroup>(FindObjectsInactive.Include).Where(Eligible))
            foreach(var renderer in group.GetLODs().SelectMany(l=>l.renderers).Where(r=>r).Distinct()){
                var filter=renderer.GetComponent<MeshFilter>();if(!filter||!filter.sharedMesh||!AssetDatabase.GetAssetPath(filter.sharedMesh).StartsWith(Folder,StringComparison.Ordinal))continue;
                var original=PrefabUtility.GetCorrespondingObjectFromSource(filter);
                if(!original||!original.sharedMesh||AssetDatabase.GetAssetPath(original.sharedMesh).StartsWith(Folder,StringComparison.Ordinal))throw new Exception("Missing original facade source: "+filter.name);
                replacements.Add((filter,original.sharedMesh));
            }
        var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();EditorSceneManager.SaveScene(scene);
        string backup=Application.dataPath+"/../Logs/WorldTraversalBackups";Directory.CreateDirectory(backup);
        File.Copy(scene.path,backup+"/"+scene.name+"_BeforeDoorRebuild_"+DateTime.UtcNow.ToString("yyyyMMdd_HHmmss")+".unity",true);
        foreach(var item in replacements){
            item.filter.sharedMesh=item.source;PrefabUtility.RecordPrefabInstancePropertyModifications(item.filter);
            var collider=item.filter.GetComponent<MeshCollider>();if(collider&&collider.enabled){collider.sharedMesh=item.source;PrefabUtility.RecordPrefabInstancePropertyModifications(collider);}
        }
        var old=GameObject.Find("Original Door Interactions");if(old)UnityEngine.Object.DestroyImmediate(old);
        EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);
        Apply();SaveFacadeOverrides();
    }
    [MenuItem("Tools/World/Enable Original Physical House Doors")]
    public static void Apply()
    {
        if(EditorApplication.isPlaying)throw new Exception("Stop play mode first.");
        if(GameObject.Find("Original Door Interactions"))throw new Exception("Original door bindings already exist; restore the scene backup before rebuilding.");
        var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();string backup=Application.dataPath+"/../Logs/WorldTraversalBackups";Directory.CreateDirectory(backup);
        File.Copy(scene.path,backup+"/"+scene.name+"_BeforeOriginalDoors_"+DateTime.UtcNow.ToString("yyyyMMdd_HHmmss")+".unity",true);
        Directory.CreateDirectory(Folder);AssetDatabase.Refresh();
        var groups=UnityEngine.Object.FindObjectsByType<LODGroup>(FindObjectsInactive.Include).Where(Eligible).ToArray();
        var definitions=new Dictionary<Mesh,List<Door>>();var variants=new Dictionary<Mesh,Mesh>();var root=new GameObject("Original Door Interactions");int count=0,buildings=0;var report=new List<string>();
        foreach(var group in groups){
            var sourceRenderer=group.GetLODs()[0].renderers.FirstOrDefault(r=>r&&r.shadowCastingMode!=ShadowCastingMode.ShadowsOnly);if(!sourceRenderer)continue;
            var source=sourceRenderer.GetComponent<MeshFilter>().sharedMesh;
            if(!definitions.TryGetValue(source,out var doors)){
                var data=Read(source);doors=Detect(data,sourceRenderer.sharedMaterials);definitions.Add(source,doors);
                for(int n=0;n<doors.Count;n++){
                    doors[n].leaf=LeafMesh(data,doors[n],"OriginalLeaf_"+Key(source)+"_"+n,false);
                    doors[n].mirrored=LeafMesh(data,doors[n],"OriginalLeaf_"+Key(source)+"_"+n+"_Mirrored",true);
                }
            }
            if(doors.Count==0){if(group.name.StartsWith("Preset_"))report.Add("NO_ORIGINAL_DOOR_IDENTIFIED "+group.name);continue;}
            foreach(var renderer in group.GetLODs().SelectMany(l=>l.renderers).Where(r=>r).Distinct()){
                var filter=renderer.GetComponent<MeshFilter>();if(!filter||!filter.sharedMesh)continue;var mesh=filter.sharedMesh;
                if(!variants.TryGetValue(mesh,out var cut)){cut=Cut(Read(mesh),doors,renderer.sharedMaterials,"OpenedFacade_"+Key(mesh),mesh==source);variants.Add(mesh,cut);}
                filter.sharedMesh=cut;
                var collider=renderer.GetComponent<MeshCollider>();if(collider&&collider.enabled)collider.sharedMesh=cut;
                PrefabUtility.RecordPrefabInstancePropertyModifications(filter);
                if(collider)PrefabUtility.RecordPrefabInstancePropertyModifications(collider);
                EditorUtility.SetDirty(filter);
            }
            int number=0;
            foreach(var door in doors){
                var obj=new GameObject(group.name+" Original Door "+(++number));obj.transform.SetParent(root.transform,false);
                var worldNormal=sourceRenderer.transform.TransformVector(door.normal).normalized;
                obj.transform.SetPositionAndRotation(sourceRenderer.transform.TransformPoint(door.hinge),Quaternion.LookRotation(worldNormal,Vector3.up));
                var tangent=sourceRenderer.transform.TransformVector(door.right);bool mirror=Vector3.Dot(tangent,obj.transform.right)<0;
                obj.transform.localScale=new Vector3(tangent.magnitude,sourceRenderer.transform.TransformVector(Vector3.up).magnitude,sourceRenderer.transform.TransformVector(door.normal).magnitude);
                var leaf=new GameObject("Original door leaf and hardware",typeof(MeshFilter),typeof(MeshRenderer));leaf.transform.SetParent(obj.transform,false);
                var mesh=mirror?door.mirrored:door.leaf;leaf.GetComponent<MeshFilter>().sharedMesh=mesh;leaf.GetComponent<MeshRenderer>().sharedMaterials=sourceRenderer.sharedMaterials;leaf.GetComponent<MeshRenderer>().shadowCastingMode=ShadowCastingMode.TwoSided;
                Collider collider;
                if(mesh.bounds.size.z<.02f){var box=leaf.AddComponent<BoxCollider>();box.center=mesh.bounds.center;box.size=new Vector3(mesh.bounds.size.x,mesh.bounds.size.y,.06f);collider=box;}
                else{var mc=leaf.AddComponent<MeshCollider>();mc.sharedMesh=mesh;mc.convex=true;collider=mc;}
                var body=leaf.AddComponent<Rigidbody>();body.isKinematic=true;body.useGravity=false;body.interpolation=RigidbodyInterpolation.Interpolate;
                var interaction=obj.AddComponent<HouseDoor>();interaction.houseName=group.name;interaction.leaf=leaf.transform;interaction.doorCollider=collider;interaction.width=door.Width*(mirror?-1:1)*(door.hingeAtMax?-1:1);interaction.height=door.Height;
                count++;report.Add("ORIGINAL_DOOR "+group.name+" "+obj.transform.position+" triangles="+mesh.triangles.Length/3);
            }
            group.RecalculateBounds();PrefabUtility.RecordPrefabInstancePropertyModifications(group);EditorUtility.SetDirty(group);buildings++;
        }
        var player=UnityEngine.Object.FindAnyObjectByType<Game.ThirdPerson.ThirdPersonMotor>();if(!player.GetComponent<DoorInteractor>())player.gameObject.AddComponent<DoorInteractor>();
        Physics.SyncTransforms();AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);
        File.WriteAllLines(Application.dataPath+"/../Logs/OriginalDoorsApplied.txt",report);Debug.Log($"ORIGINAL_PHYSICAL_DOORS doors={count} buildings={buildings} facadeVariants={variants.Count}");
    }
    [MenuItem("Tools/World/Save Original Door Facade Overrides")]
    public static void SaveFacadeOverrides()
    {
        if(EditorApplication.isPlaying)throw new Exception("Stop play mode first.");
        int count=0;
        foreach(var group in UnityEngine.Object.FindObjectsByType<LODGroup>(FindObjectsInactive.Include).Where(Eligible))
            foreach(var renderer in group.GetLODs().SelectMany(l=>l.renderers).Where(r=>r).Distinct()){
                var filter=renderer.GetComponent<MeshFilter>();if(!filter||!filter.sharedMesh)continue;
                var mesh=filter.sharedMesh;
                if(!AssetDatabase.GetAssetPath(mesh).StartsWith(Folder,StringComparison.Ordinal)){
                    var saved=AssetDatabase.LoadAssetAtPath<Mesh>(Folder+"/OpenedFacade_"+Key(mesh)+".asset");if(!saved)continue;
                    filter.sharedMesh=saved;
                }
                var collider=renderer.GetComponent<MeshCollider>();if(collider&&collider.enabled){collider.sharedMesh=filter.sharedMesh;PrefabUtility.RecordPrefabInstancePropertyModifications(collider);}
                PrefabUtility.RecordPrefabInstancePropertyModifications(filter);EditorUtility.SetDirty(filter);count++;
            }
        var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);Debug.Log("ORIGINAL_DOOR_FACADE_OVERRIDES_SAVED "+count);
    }
    [MenuItem("Tools/World/Audit Original House Doors")]
    public static void Audit()
    {
        var seen=new HashSet<Mesh>();var lines=new List<string>();int instances=0;
        foreach(var group in UnityEngine.Object.FindObjectsByType<LODGroup>(FindObjectsInactive.Include).Where(Eligible)){
            var renderer=group.GetLODs()[0].renderers.FirstOrDefault(r=>r&&r.shadowCastingMode!=ShadowCastingMode.ShadowsOnly);if(!renderer)continue;
            var mesh=renderer.GetComponent<MeshFilter>().sharedMesh;
            if(!seen.Add(mesh))continue;
            var d=Read(mesh);var frames=Frames(d,renderer.sharedMaterials);var doors=Detect(d,renderer.sharedMaterials);instances+=doors.Count;
            lines.Add(group.name+" frames="+frames.Count+" doors="+doors.Count+string.Concat(doors.Select(x=>" [center="+x.frame.center.ToString("F3")+" normal="+x.normal.ToString("F2")+" size="+x.Width.ToString("F2")+","+x.Height.ToString("F2")+" triangles="+x.selected.Sum(t=>t.Count)/3+"]")));
        }
        Directory.CreateDirectory(Application.dataPath+"/../Logs");File.WriteAllLines(Application.dataPath+"/../Logs/OriginalDoorsAudit.txt",lines);Debug.Log("ORIGINAL_DOOR_AUDIT uniqueMeshes="+seen.Count+" doorDefinitions="+instances);
    }
}
