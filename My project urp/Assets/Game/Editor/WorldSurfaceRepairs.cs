using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using Unity.Collections;
using UnityEngine.Rendering;

public static class WorldSurfaceRepairs
{
    static T[] All<T>() where T : Component => SceneManager.GetActiveScene().GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<T>(true)).ToArray();
    static string Label(MeshFilter f) => (f.name + "/" + Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(f.sharedMesh))).ToLowerInvariant();
    static bool Aquatic(string s) => s.Contains("waterlily") || s.Contains("water plants") || s.Contains("cattail") || s.Contains("reed");
    static bool Grass(string s) => !Aquatic(s) && (s.Contains("grass") || s.Contains("village_scatter") || s.Contains("fern") || s.Contains("weed"));
    static bool Rock(string s) => s.Contains("rock") || s.Contains("cliff") || s.Contains("boulder");
    static bool Primary(MeshFilter f)
    {
        var r = f.GetComponent<Renderer>();
        if (!r || !r.enabled || !r.gameObject.activeInHierarchy || r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly) return false;
        var lod = f.GetComponentInParent<LODGroup>();
        return !lod || lod.GetLODs().Length == 0 || lod.GetLODs()[0].renderers.Contains(r);
    }
    static Transform PatchRoot(MeshFilter f)
    {
        var lod = f.GetComponentInParent<LODGroup>();
        return lod && (Aquatic(lod.name.ToLowerInvariant()) || lod.GetComponentsInChildren<MeshFilter>(true).Where(m => m.sharedMesh).All(m => Aquatic(Label(m)))) ? lod.transform : f.transform;
    }
    static bool SolidGround(RaycastHit h)
    {
        if (!h.collider || h.collider.isTrigger || h.normal.y < .5f || h.collider is CharacterController) return false;
        string name = h.collider.name.ToLowerInvariant();
        if (h.collider is MeshCollider m && m.sharedMesh) name += "/" + Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(m.sharedMesh)).ToLowerInvariant();
        return !Aquatic(name) && !Grass(name) && !name.Contains("tree") && !name.Contains("bush") && !name.Contains("water") && !name.Contains("river") && !name.Contains("lake");
    }
    static void Record(UnityEngine.Object obj)
    {
        EditorUtility.SetDirty(obj);
        if (PrefabUtility.IsPartOfPrefabInstance(obj)) PrefabUtility.RecordPrefabInstancePropertyModifications(obj);
    }
    static string MeshFolder = "Assets/Game/Collision/GroundedGrass";
    internal sealed class Geometry
    {
        public Vector3[] vertices;
        public int[][] cards;
    }
    internal static Geometry Read(Mesh mesh)
    {
        using (var arrays = MeshUtility.AcquireReadOnlyMeshData(mesh))
        {
            var data = arrays[0];
            var parent = Enumerable.Range(0, data.vertexCount).ToArray();
            Func<int,int> find = null;
            find = a => parent[a] == a ? a : parent[a] = find(parent[a]);
            for (int sub = 0; sub < data.subMeshCount; sub++)
            {
                if (data.GetSubMesh(sub).topology != MeshTopology.Triangles) continue;
                using (var ids = new NativeArray<int>(data.GetSubMesh(sub).indexCount, Allocator.Temp))
                {
                    data.GetIndices(ids, sub);
                    for (int i = 0; i < ids.Length; i += 3) { parent[find(ids[i + 1])] = find(ids[i]); parent[find(ids[i + 2])] = find(ids[i]); }
                }
            }
            using (var vertices = new NativeArray<Vector3>(data.vertexCount, Allocator.Temp))
            {
                data.GetVertices(vertices);
                return new Geometry { vertices = vertices.ToArray(), cards = Enumerable.Range(0, data.vertexCount).GroupBy(find).Select(g => g.ToArray()).ToArray() };
            }
        }
    }
    internal static Mesh Clone(Mesh source, Vector3[] vertices)
    {
        var result = new Mesh { name = source.name + "_Grounded", indexFormat = source.indexFormat };
        using (var arrays = MeshUtility.AcquireReadOnlyMeshData(source))
        {
            var data = arrays[0];
            var writable = Mesh.AllocateWritableMeshData(1);
            var dst = writable[0];
            dst.SetVertexBufferParams(data.vertexCount, source.GetVertexAttributes());
            for (int stream = 0; stream < data.vertexBufferCount; stream++) dst.GetVertexData<byte>(stream).CopyFrom(data.GetVertexData<byte>(stream));
            dst.SetIndexBufferParams(data.GetIndexData<byte>().Length / (source.indexFormat == IndexFormat.UInt32 ? 4 : 2), source.indexFormat);
            dst.GetIndexData<byte>().CopyFrom(data.GetIndexData<byte>());
            dst.subMeshCount = data.subMeshCount;
            for (int i = 0; i < data.subMeshCount; i++) dst.SetSubMesh(i, data.GetSubMesh(i), MeshUpdateFlags.DontRecalculateBounds);
            Mesh.ApplyAndDisposeWritableMeshData(writable, result, MeshUpdateFlags.DontRecalculateBounds);
        }
        result.vertices = vertices;
        result.RecalculateBounds();
        return result;
    }
    static readonly RaycastHit[] hits = new RaycastHit[64];
    static bool GroundAt(Vector3 point, out float height)
    {
        height = float.NegativeInfinity;
        int count = Physics.RaycastNonAlloc(point + Vector3.up * 2f, Vector3.down, hits, 10f, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            var h = hits[i];
            if (!SolidGround(h) || h.point.y > point.y + .3f) continue;
            if (!(h.collider is TerrainCollider))
            {
                string name = h.collider.name.ToLowerInvariant();
                if (h.collider is MeshCollider mc && mc.sharedMesh) name += "/" + mc.sharedMesh.name.ToLowerInvariant();
                if (!(name.Contains("ground") || name.Contains("terrain") || name.Contains("paving") || name.Contains("pavement") || name.Contains("road") || name.Contains("path"))) continue;
            }
            height = Mathf.Max(height, h.point.y);
        }
        return !float.IsNegativeInfinity(height);
    }
    [MenuItem("Tools/World/Apply Surface Repairs")]
    public static void Apply()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play mode before baking.");
        var scene = SceneManager.GetActiveScene();
        if (scene.path != "Assets/Scenes/Demo.unity") throw new Exception("Open Demo scene first.");
        string backup = Application.dataPath + "/../Logs/WorldTraversalBackups";
        Directory.CreateDirectory(backup);
        EditorSceneManager.SaveScene(scene);
        File.Copy(scene.path, backup + "/Demo_BeforeSurfaceRepairs_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + ".unity", true);
        var filters = All<MeshFilter>().Where(f => f.sharedMesh).ToArray();
        int bodies = 0, plantColliders = 0, rocks = 0, baked = 0, cards = 0, noSupport = 0;
        foreach (var root in filters.Where(f => Aquatic(Label(f))).Select(PatchRoot).Distinct())
        {
            foreach (var c in root.GetComponentsInChildren<Collider>(true)) { UnityEngine.Object.DestroyImmediate(c); plantColliders++; }
            foreach (var rb in root.GetComponentsInChildren<Rigidbody>(true)) { UnityEngine.Object.DestroyImmediate(rb); bodies++; }
        }
        foreach (var filter in filters.Where(f => Rock(Label(f)) && Primary(f)))
        {
            var c = filter.GetComponent<MeshCollider>();
            if (!c) c = filter.gameObject.AddComponent<MeshCollider>();
            c.convex = false; c.isTrigger = false; c.sharedMesh = filter.sharedMesh; c.enabled = true; c.contactOffset = .01f;
            Record(c); rocks++;
        }
        Physics.SyncTransforms();
        Directory.CreateDirectory(MeshFolder);
        AssetDatabase.Refresh();
        var geometries = new Dictionary<Mesh, Geometry>();
        var log = new List<string>();
        foreach (var filter in filters.Where(f => Grass(Label(f))))
        {
            if (!filter.gameObject.activeInHierarchy || AssetDatabase.GetAssetPath(filter.sharedMesh).StartsWith(MeshFolder, StringComparison.Ordinal)) continue;
            var source = filter.sharedMesh;
            if (!geometries.TryGetValue(source, out var geometry)) { geometry = Read(source); geometries.Add(source, geometry); }
            var vertices = (Vector3[])geometry.vertices.Clone();
            int changed = 0;
            foreach (var card in geometry.cards)
            {
                if (card.Length < 3) continue;
                float minY = card.Min(i => filter.transform.TransformPoint(vertices[i]).y);
                var bottom = card.Select(i => filter.transform.TransformPoint(vertices[i])).Where(v => v.y <= minY + .025f).ToArray();
                var point = bottom.Aggregate(Vector3.zero, (a,b) => a + b) / bottom.Length;
                if (!GroundAt(point, out float ground)) { noSupport++; continue; }
                float gap = minY - ground;
                if (gap <= .035f || gap > 6f) continue;
                var delta = filter.transform.InverseTransformVector(Vector3.up * (-gap - .015f));
                foreach (int i in card) vertices[i] += delta;
                changed++;
            }
            if (changed == 0) continue;
            string id = Hash128.Compute(GlobalObjectId.GetGlobalObjectIdSlow(filter).ToString()).ToString();
            string path = MeshFolder + "/Grass_" + id + ".asset";
            var mesh = Clone(source, vertices);
            AssetDatabase.CreateAsset(mesh, path);
            filter.sharedMesh = mesh; Record(filter);
            var renderer = filter.GetComponent<Renderer>();
            if (renderer) { renderer.localBounds = mesh.bounds; Record(renderer); }
            baked++; cards += changed;
            log.Add(filter.name + " groundedCards=" + changed + " mesh=" + path);
        }
        foreach (var lod in All<LODGroup>()) { lod.RecalculateBounds(); Record(lod); }
        Physics.SyncTransforms();
        EditorSceneManager.MarkSceneDirty(scene);
        AssetDatabase.SaveAssets(); EditorSceneManager.SaveScene(scene);
        log.Insert(0, $"removedPlantColliders={plantColliders} removedRigidbodies={bodies} rockMeshes={rocks} groundedMeshes={baked} groundedCards={cards} unsupportedSamples={noSupport}");
        File.WriteAllLines(Application.dataPath + "/../Logs/SurfaceRepairResults.txt", log);
        Debug.Log("SURFACE_REPAIRS_APPLIED " + log[0]);
    }
    [MenuItem("Tools/World/Audit Surface Repairs")]
    public static void Audit()
    {
        Physics.SyncTransforms();
        var lines = new List<string>();
        foreach (var group in All<MeshFilter>().Where(f => f.sharedMesh && Primary(f)).Where(f => Aquatic(Label(f)) || Grass(Label(f)) || Rock(Label(f))).GroupBy(Label))
        {
            var fs = group.ToArray();
            int cols = fs.Sum(f => PatchRoot(f).GetComponentsInChildren<Collider>(true).Count(c => c.enabled && !c.isTrigger));
            lines.Add($"{group.Key} instances={fs.Length} colliders={cols} first={fs[0].transform.position:F2} bounds={fs[0].GetComponent<Renderer>().bounds}");
        }
        Directory.CreateDirectory(Application.dataPath + "/../Logs");
        File.WriteAllLines(Application.dataPath + "/../Logs/SurfaceRepairAudit.txt", lines);
        Debug.Log("SURFACE_REPAIR_AUDIT groups=" + lines.Count);
    }
    [MenuItem("Tools/World/Validate Surface Repairs")]
    public static void Validate()
    {
        Physics.SyncTransforms();
        var filters = All<MeshFilter>().Where(f => f.sharedMesh).ToArray();
        int plantColliders = 0, bodies = 0, rocks = 0, rockRays = 0, cards = 0, floating = 0;
        foreach (var root in filters.Where(f => Aquatic(Label(f))).Select(PatchRoot).Distinct())
        {
            plantColliders += root.GetComponentsInChildren<Collider>(true).Length;
            bodies += root.GetComponentsInChildren<Rigidbody>(true).Length;
        }
        foreach (var f in filters.Where(f => Rock(Label(f)) && Primary(f)))
        {
            var c = f.GetComponent<MeshCollider>();
            if (!c || !c.enabled || c.isTrigger || c.convex || c.sharedMesh != f.sharedMesh) throw new Exception("Missing rock collision: " + f.name);
            rocks++;
            var b = c.bounds;
            bool found = false;
            for (int x = 1; x < 5 && !found; x++) for (int z = 1; z < 5 && !found; z++)
                if (c.Raycast(new Ray(new Vector3(Mathf.Lerp(b.min.x,b.max.x,x/5f),b.max.y+1,Mathf.Lerp(b.min.z,b.max.z,z/5f)),Vector3.down),out var hit,b.size.y+2)) { rockRays++; found=true; }
        }
        foreach (var f in filters.Where(f => AssetDatabase.GetAssetPath(f.sharedMesh).StartsWith(MeshFolder, StringComparison.Ordinal)))
        {
            var geometry = Read(f.sharedMesh);
            foreach (var card in geometry.cards)
            {
                if (card.Length < 3) continue;
                float minY = card.Min(i => f.transform.TransformPoint(geometry.vertices[i]).y);
                var basePoints = card.Select(i => f.transform.TransformPoint(geometry.vertices[i])).Where(v => v.y <= minY + .025f).ToArray();
                var p = basePoints.Aggregate(Vector3.zero,(a,b) => a+b) / basePoints.Length;
                if (!GroundAt(p,out float y)) continue;
                cards++; if (minY-y>.04f && minY-y<6) floating++;
            }
        }
        var report = $"aquaticColliders={plantColliders} aquaticRigidbodies={bodies} rockMeshes={rocks} rayHittableRocks={rockRays} groundedCardSamples={cards} floatingCardSamples={floating}";
        File.WriteAllText(Application.dataPath + "/../Logs/SurfaceRepairValidation.txt",report);
        if (plantColliders != 0 || bodies != 0 || rocks == 0 || rockRays == 0 || floating != 0) throw new Exception(report);
        Debug.Log("SURFACE_REPAIRS_VALIDATED " + report);
    }
    [MenuItem("Tools/World/Validate Rooftop Teleport")]
    public static void ValidateRoof()
    {
        var map = UnityEngine.Object.FindAnyObjectByType<Game.World.WorldMapController>();
        if (!map) throw new Exception("Map controller missing.");
        var lines = new List<string>();
        foreach (var c in All<MeshCollider>().Where(c => c.enabled && !c.isTrigger))
        {
            string label = c.name.ToLowerInvariant();
            for (var t=c.transform.parent;t;t=t.parent) label += "/" + t.name.ToLowerInvariant();
            if (!(label.Contains("house") || label.Contains("castle") || label.Contains("tower") || label.Contains("church") || label.Contains("roof"))) continue;
            var b = c.bounds;
            if (b.size.y < 2) continue;
            for (int x=1;x<7 && lines.Count<6;x++) for (int z=1;z<7 && lines.Count<6;z++)
            {
                var ray = new Ray(new Vector3(Mathf.Lerp(b.min.x,b.max.x,x/7f),b.max.y+1,Mathf.Lerp(b.min.z,b.max.z,z/7f)),Vector3.down);
                if (!c.Raycast(ray,out var h,b.size.y+2) || h.normal.y < .71f || h.point.y < b.min.y+2) continue;
                if (!map.TryResolveLanding(map.WorldToMap(h.point),out var landing) || Mathf.Abs(landing.y - h.point.y)>.15f || Vector2.Distance(new Vector2(landing.x,landing.z),new Vector2(h.point.x,h.point.z))>.1f) continue;
                lines.Add("roof=" + c.name + " point=" + landing.ToString("F3"));
            }
            if (lines.Count>=6) break;
        }
        if (lines.Count == 0) throw new Exception("No safe rooftop teleport found.");
        File.WriteAllLines(Application.dataPath+"/../Logs/RooftopTeleportValidation.txt",lines);
        Debug.Log("ROOFTOP_TELEPORT_VALIDATED samples="+lines.Count+" first="+lines[0]);
    }
    [MenuItem("Tools/World/Play Verify Surface Repairs")]
    public static void PlayVerify()
    {
        if (!EditorApplication.isPlaying) throw new Exception("Enter Play mode first.");
        var motor=UnityEngine.Object.FindAnyObjectByType<Game.ThirdPerson.ThirdPersonMotor>();
        var map=motor.GetComponent<Game.World.WorldMapController>();
        var cc=motor.GetComponent<CharacterController>();
        var follow=motor.cameraTransform.GetComponent<Game.ThirdPerson.ThirdPersonFollowCamera>();
        Vector3 saved=motor.transform.position; bool motorOn=motor.enabled, camOn=follow.enabled;
        motor.enabled=false; follow.enabled=false;
        int blockedRocks=0, villages=0; bool roofGrounded=false;
        try
        {
            string roofLine=File.ReadAllLines(Application.dataPath+"/../Logs/RooftopTeleportValidation.txt")[0];
            string[] xyz=roofLine.Substring(roofLine.IndexOf("point=(",StringComparison.Ordinal)+7).TrimEnd(')').Split(',');
            var roof=new Vector3(float.Parse(xyz[0],System.Globalization.CultureInfo.InvariantCulture),float.Parse(xyz[1],System.Globalization.CultureInfo.InvariantCulture),float.Parse(xyz[2],System.Globalization.CultureInfo.InvariantCulture));
            if (!map.TryTeleport(map.WorldToMap(roof))) throw new Exception("Rooftop map click failed.");
            float landingY=motor.transform.position.y;
            for (int i=0;i<90;i++) motor.Tick(Vector2.zero,false,false,1f/60f);
            roofGrounded=motor.Grounded && Mathf.Abs(motor.transform.position.y-landingY)<.2f;
            if (!roofGrounded) throw new Exception("Player did not stay on rooftop.");
            for (int i=0;i<map.villages.Length;i++)
            {
                if (!map.WarpToVillage(i) || Mathf.Abs(motor.transform.position.y-map.villages[i].position.y)>2f) throw new Exception("Village warp changed: "+i);
                villages++;
            }
            foreach (var f in All<MeshFilter>().Where(f=>f.sharedMesh && Rock(Label(f)) && Primary(f)))
            {
                var c=f.GetComponent<MeshCollider>(); var b=c.bounds;
                foreach (var dir in new[]{Vector3.forward,Vector3.back,Vector3.left,Vector3.right})
                {
                    if (!c.Raycast(new Ray(b.center+dir*(b.extents.magnitude+3),-dir),out var hit,b.extents.magnitude*2+6) || Mathf.Abs(hit.normal.y)>.15f) continue;
                    var n=Vector3.ProjectOnPlane(hit.normal,Vector3.up).normalized;
                    var center=hit.point+n*1.5f; float half=cc.height*.5f-cc.radius;
                    if(Physics.OverlapCapsule(center+Vector3.up*half,center-Vector3.up*half,cc.radius+.04f,~0,QueryTriggerInteraction.Ignore).Any(o=>o!=cc)) continue;
                    motor.TeleportTo(center-cc.center); Physics.SyncTransforms();
                    CollisionFlags flags=CollisionFlags.None;
                    for(int i=0;i<120;i++) flags|=cc.Move(-n*.03f);
                    float separation=Vector3.Dot(motor.transform.position+cc.center-hit.point,n);
                    if ((flags&CollisionFlags.Sides)!=0 && separation>cc.radius-.06f && separation<1.5f) { blockedRocks++; break; }
                }
                if(blockedRocks>=3)break;
            }
            if(blockedRocks<3)throw new Exception("Insufficient solid rock collision samples: "+blockedRocks);
        }
        finally {motor.TeleportTo(saved);motor.enabled=motorOn;follow.enabled=camOn;follow.UpdateView(1);}
        Debug.Log($"SURFACE_PLAY_VERIFIED roofGrounded={roofGrounded} villageWarps={villages} blockingRockSamples={blockedRocks}");
    }
}

