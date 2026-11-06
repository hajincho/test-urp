using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Unity.Profiling;
using UnityEditor.SceneManagement;

public static class RenderRepairTools
{
    static T[] All<T>() where T:Component => SceneManager.GetActiveScene().GetRootGameObjects().SelectMany(r=>r.GetComponentsInChildren<T>(true)).ToArray();
    static string Root => Application.dataPath+"/../Logs/";
    static bool Architecture(string name)
    {
        string n=name.ToLowerInvariant();
        return n.StartsWith("composition_") || n.StartsWith("preset_") || n.StartsWith("sm_town_") || new[]{"house","church","castle","tower","bridge","wall","building","windmill","barn","stables","roof","fort"}.Any(n.Contains);
    }
    static void Record(UnityEngine.Object obj){EditorUtility.SetDirty(obj);if(PrefabUtility.IsPartOfPrefabInstance(obj))PrefabUtility.RecordPrefabInstancePropertyModifications(obj);}
    static void Set(SerializedObject so,string path,int value){var p=so.FindProperty(path);if(p!=null)p.intValue=value;}
    [MenuItem("Tools/World/Apply Render Repairs")]
    public static void Apply()
    {
        if(EditorApplication.isPlaying)throw new Exception("Exit Play mode");
        var scene=SceneManager.GetActiveScene();if(scene.path!="Assets/Scenes/Demo.unity")throw new Exception("Open Demo");
        string backup=Root+"RenderRepairBackup/"+DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");Directory.CreateDirectory(backup);
        EditorSceneManager.SaveScene(scene);File.Copy(scene.path,backup+"/Demo.unity");
        File.Copy("ProjectSettings/QualitySettings.asset",backup+"/QualitySettings.asset");
        bool integrated=SystemInfo.graphicsDeviceName.IndexOf("Iris",StringComparison.OrdinalIgnoreCase)>=0;
        float lodBias=integrated?1f:1.25f;
        var quality=new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset")[0]);
        var levels=quality.FindProperty("m_QualitySettings");
        var pipelines=new HashSet<UniversalRenderPipelineAsset>();
        for(int i=0;i<QualitySettings.names.Length;i++)
        {
            if(!QualitySettings.names[i].Contains("Desktop"))continue;
            var q=levels.GetArrayElementAtIndex(i);q.FindPropertyRelative("lodBias").floatValue=lodBias;q.FindPropertyRelative("maximumLODLevel").intValue=0;q.FindPropertyRelative("terrainPixelError").floatValue=8;
            var rp=QualitySettings.GetRenderPipelineAssetAt(i) as UniversalRenderPipelineAsset;if(rp)pipelines.Add(rp);
        }
        quality.ApplyModifiedPropertiesWithoutUndo();QualitySettings.lodBias=lodBias;QualitySettings.maximumLODLevel=0;
        foreach(var rp in pipelines)
        {
            string path=AssetDatabase.GetAssetPath(rp);File.Copy(path,backup+"/"+Path.GetFileName(path),true);
            rp.renderScale=integrated?.85f:1f;rp.mainLightShadowmapResolution=2048;rp.shadowDistance=100f;rp.shadowCascadeCount=2;rp.additionalLightsShadowmapResolution=1024;rp.maxAdditionalLightsCount=4;rp.gpuResidentDrawerEnableOcclusionCullingInCameras=false;
            var so=new SerializedObject(rp);Set(so,"m_SoftShadowQuality",2);so.ApplyModifiedPropertiesWithoutUndo();Record(rp);
            var data=so.FindProperty("m_RendererDataList");
            for(int j=0;j<data.arraySize;j++)
            {
                var renderer=data.GetArrayElementAtIndex(j).objectReferenceValue as ScriptableRendererData;if(!renderer)continue;
                foreach(var feature in renderer.rendererFeatures)
                {
                    if(!feature||feature.GetType().Name!="ScreenSpaceAmbientOcclusion")continue;
                    var serialized=new SerializedObject(feature);var half=serialized.FindProperty("m_Settings.Downsample");if(half!=null){half.boolValue=true;serialized.ApplyModifiedPropertiesWithoutUndo();Record(feature);}
                }
            }
        }
        var materialCache=new Dictionary<Material,Material>();int groups=0,emptyLevels=0,renderers=0;
        foreach(var group in All<LODGroup>().Where(g=>Architecture(g.name)))
        {
            var source=group.GetLODs();var valid=new List<LOD>();
            foreach(var lod in source)
            {
                if(!lod.renderers.Any(r=>r&&r.GetComponent<MeshFilter>()&&r.shadowCastingMode!=ShadowCastingMode.ShadowsOnly)){emptyLevels++;continue;}
                valid.Add(lod);
                foreach(var r in lod.renderers.Where(r=>r&&r.shadowCastingMode!=ShadowCastingMode.ShadowsOnly))
                {
                    var mf=r.GetComponent<MeshFilter>();if(!mf||!mf.sharedMesh)continue;
                    r.localBounds=mf.sharedMesh.bounds;r.allowOcclusionWhenDynamic=false;
                    var materials=r.sharedMaterials;
                    for(int m=0;m<materials.Length;m++)
                    {
                        var mat=materials[m];if(!mat||!mat.HasProperty("_Cull"))continue;
                        if(!materialCache.TryGetValue(mat,out var two))
                        {
                            two=mat;
                            if(mat.GetFloat("_Cull")!=0)
                            {
                                string dest="Assets/Game/Materials/Architecture/"+mat.name+"_TwoSided.mat";
                                two=AssetDatabase.LoadAssetAtPath<Material>(dest);if(!two){two=new Material(mat){name=mat.name+"_TwoSided"};AssetDatabase.CreateAsset(two,dest);}
                                two.SetFloat("_Cull",0);
                            }
                            two.enableInstancing=true;Record(two);materialCache[mat]=two;
                        }
                        materials[m]=two;
                    }
                    r.sharedMaterials=materials;Record(r);renderers++;
                }
            }
            if(valid.Count==0)continue;
            var last=valid[valid.Count-1];last.screenRelativeTransitionHeight=0;last.fadeTransitionWidth=0;valid[valid.Count-1]=last;
            group.SetLODs(valid.ToArray());group.fadeMode=LODFadeMode.None;group.ForceLOD(-1);group.RecalculateBounds();Record(group);groups++;
        }
        foreach(var renderer in All<MeshRenderer>())
        {
            var mesh=renderer.GetComponent<MeshFilter>();if(!mesh||!mesh.sharedMesh)continue;
            string label=(renderer.name+"/"+AssetDatabase.GetAssetPath(mesh.sharedMesh)).ToLowerInvariant();
            if(!(label.Contains("grass")||label.Contains("village_scatter")||label.Contains("fern")))continue;
            renderer.shadowCastingMode=ShadowCastingMode.Off;Record(renderer);
            foreach(var mat in renderer.sharedMaterials)if(mat){mat.enableInstancing=true;Record(mat);}
        }
        foreach(var lod in All<LODGroup>().Where(g=>g.name.IndexOf("grass",StringComparison.OrdinalIgnoreCase)>=0||g.name.IndexOf("village_scatter",StringComparison.OrdinalIgnoreCase)>=0))
        {
            var ls=lod.GetLODs();if(ls.Length==0)continue;var last=ls[ls.Length-1];last.screenRelativeTransitionHeight=.08f;ls[ls.Length-1]=last;lod.SetLODs(ls);Record(lod);
        }
        AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);
        File.WriteAllText(Root+"RenderRepairApplied.txt",$"pipelineAssets={pipelines.Count} buildingGroups={groups} rendererAssignments={renderers} invalidLevelsRemoved={emptyLevels} materials={materialCache.Count}");
        Debug.Log("RENDER_REPAIRS_APPLIED "+File.ReadAllText(Root+"RenderRepairApplied.txt"));
    }
    [MenuItem("Tools/World/Audit Rendering")]
    public static void Audit()
    {
        var lines=new List<string>();
        var rp=QualitySettings.renderPipeline?QualitySettings.renderPipeline:GraphicsSettings.defaultRenderPipeline;
        lines.Add($"quality={QualitySettings.names[QualitySettings.GetQualityLevel()]} lodBias={QualitySettings.lodBias} maximumLOD={QualitySettings.maximumLODLevel} pipeline={AssetDatabase.GetAssetPath(rp)} device={SystemInfo.graphicsDeviceName} pixels={Screen.width}x{Screen.height}");
        foreach(var cam in All<Camera>())lines.Add($"CAMERA {cam.name} enabled={cam.enabled} active={cam.gameObject.activeInHierarchy} near={cam.nearClipPlane} far={cam.farClipPlane} occlusion={cam.useOcclusionCulling} pos={cam.transform.position}");
        foreach(var b in All<MonoBehaviour>().Where(b=>b && (b.GetType().Name.Contains("Fog") || b.GetType().Name.Contains("Quality"))))lines.Add($"BEHAVIOUR {b.GetType().Name} {b.name} enabled={b.enabled} active={b.gameObject.activeInHierarchy}");
        foreach(var g in All<LODGroup>())
        {
            var levels=g.GetLODs(); if(levels.Length==0)continue;
            string[] desc=levels.Select((l,i)=>i+":"+l.screenRelativeTransitionHeight.ToString("F4")+" visual="+l.renderers.Count(r=>r&&r.enabled&&r.gameObject.activeInHierarchy&&r.shadowCastingMode!=ShadowCastingMode.ShadowsOnly)+" shadow="+l.renderers.Count(r=>r&&r.shadowCastingMode==ShadowCastingMode.ShadowsOnly)).ToArray();
            if(g.name.StartsWith("Preset_") || levels.Any(l=>!l.renderers.Any(r=>r&&r.shadowCastingMode!=ShadowCastingMode.ShadowsOnly)))lines.Add("LOD "+g.name+" size="+g.size+" point="+g.localReferencePoint+" "+string.Join(" | ",desc));
        }
        foreach(var m in All<MeshRenderer>().Where(r=>r.enabled&&r.gameObject.activeInHierarchy).SelectMany(r=>r.sharedMaterials).Where(m=>m).Distinct())
            lines.Add($"MAT {m.name} shader={m.shader.name} instancing={m.enableInstancing} cull={(m.HasProperty("_Cull")?m.GetFloat("_Cull"):-99)} keywords={string.Join(",",m.shaderKeywords)}");
        foreach(var vfx in All<UnityEngine.VFX.VisualEffect>())lines.Add($"VFX {vfx.name} enabled={vfx.enabled} active={vfx.gameObject.activeInHierarchy} asset={vfx.visualEffectAsset?.name}");
        Directory.CreateDirectory(Root);File.WriteAllLines(Root+"RenderAudit.txt",lines);Debug.Log("RENDER_AUDIT saved lines="+lines.Count);
    }
    [MenuItem("Tools/World/Measure Rendering Before")]
    public static void Before()=>Measure("before");
    [MenuItem("Tools/World/Measure Rendering After")]
    public static void After()=>Measure("after");
    static void Measure(string label)
    {
        if(!EditorApplication.isPlaying)throw new Exception("Enter Play mode");
        var camera=Camera.main;var follow=camera.GetComponent<Game.ThirdPerson.ThirdPersonFollowCamera>();var motor=UnityEngine.Object.FindAnyObjectByType<Game.ThirdPerson.ThirdPersonMotor>();
        bool followOn=follow&&follow.enabled,motorOn=motor.enabled;if(follow)follow.enabled=false;motor.enabled=false;
        var main=ProfilerRecorder.StartNew(ProfilerCategory.Internal,"Main Thread",256);
        var render=ProfilerRecorder.StartNew(ProfilerCategory.Internal,"Render Thread",256);
        var batches=ProfilerRecorder.StartNew(ProfilerCategory.Render,"Batches Count",256);
        var tris=ProfilerRecorder.StartNew(ProfilerCategory.Render,"Triangles Count",256);
        var alloc=ProfilerRecorder.StartNew(ProfilerCategory.Memory,"GC Allocated In Frame",256);
        var frames=new List<double>();var mainTimes=new List<double>();var renderTimes=new List<double>();var counts=new List<double>();var triangles=new List<double>();var allocations=new List<double>();
        double start=EditorApplication.timeSinceStartup;int previous=-1;var pos=camera.transform.position;var rotation=camera.transform.rotation;
        EditorApplication.CallbackFunction update=null;
        update=()=>{
            if(!EditorApplication.isPlaying){EditorApplication.update-=update;main.Dispose();render.Dispose();batches.Dispose();tris.Dispose();alloc.Dispose();return;}
            camera.transform.SetPositionAndRotation(pos,rotation);
            if(previous==Time.frameCount)return;previous=Time.frameCount;
            double elapsed=EditorApplication.timeSinceStartup-start;
            if(elapsed>2){frames.Add(Time.unscaledDeltaTime*1000);if(main.Valid)mainTimes.Add(main.LastValue/1e6);if(render.Valid)renderTimes.Add(render.LastValue/1e6);if(batches.Valid)counts.Add(batches.LastValue);if(tris.Valid)triangles.Add(tris.LastValue);if(alloc.Valid)allocations.Add(alloc.LastValue);}
            if(elapsed<10)return;
            EditorApplication.update-=update;main.Dispose();render.Dispose();batches.Dispose();tris.Dispose();alloc.Dispose();
            Func<List<double>,double> average=a=>a.Count>0?a.Average():0;
            string report=$"samples={frames.Count} frameMs={average(frames):F2} mainThreadMs={average(mainTimes):F2} renderThreadMs={average(renderTimes):F2} batches={average(counts):F0} triangles={average(triangles):F0} gcBytes={average(allocations):F0} camera={pos} rotation={rotation.eulerAngles}";
            File.WriteAllText(Root+"Rendering_"+label+".txt",report);Debug.Log("RENDER_MEASURE_"+label+" "+report);if(follow)follow.enabled=followOn;motor.enabled=motorOn;
        };EditorApplication.update+=update;
    }
    [MenuItem("Tools/World/Validate Render Repairs")]
    public static void Validate()
    {
        var rp=(QualitySettings.renderPipeline?QualitySettings.renderPipeline:GraphicsSettings.defaultRenderPipeline) as UniversalRenderPipelineAsset;
        if(!rp || rp.renderScale>1.01f || rp.shadowDistance>120 || rp.mainLightShadowmapResolution>2048 || rp.gpuResidentDrawerEnableOcclusionCullingInCameras || QualitySettings.lodBias>1.5 || QualitySettings.maximumLODLevel!=0)throw new Exception("Optimized rendering settings were overridden.");
        int groups=0,renderers=0;
        foreach(var g in All<LODGroup>().Where(g=>Architecture(g.name)))
        {
            var levels=g.GetLODs();if(levels.Length==0)continue;
            if(levels.Last().screenRelativeTransitionHeight!=0 || g.fadeMode!=LODFadeMode.None)throw new Exception("Unstable building LOD: "+g.name);
            foreach(var l in levels)
            {
                if(!l.renderers.Any(r=>r&&r.shadowCastingMode!=ShadowCastingMode.ShadowsOnly))throw new Exception("Blank building LOD: "+g.name);
                foreach(var r in l.renderers.Where(r=>r&&r.shadowCastingMode!=ShadowCastingMode.ShadowsOnly))
                {
                    var mf=r.GetComponent<MeshFilter>();if(!mf||!mf.sharedMesh)continue;
                    if(Vector3.Distance(r.localBounds.center,mf.sharedMesh.bounds.center)>.01f || Vector3.Distance(r.localBounds.size,mf.sharedMesh.bounds.size)>.01f)throw new Exception("Building bounds mismatch: "+r.name);
                    foreach(var m in r.sharedMaterials)if(m&&m.HasProperty("_Cull")&&m.GetFloat("_Cull")!=0)throw new Exception("Backface culled building: "+r.name);
                    renderers++;
                }
            }
            groups++;
        }
        if(Application.isPlaying)
        {
            foreach(var fog in All<MonoBehaviour>().Where(b=>b&&b.GetType().Name=="FogController"))
                if((bool)fog.GetType().GetMethod("ShouldChangeLODSettings",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(fog,null))throw new Exception("RTS LOD override remains active.");
        }
        string report=$"buildingGroups={groups} visibleLODRenderers={renderers} renderScale={rp.renderScale} shadows={rp.mainLightShadowmapResolution} shadowDistance={rp.shadowDistance} gpuOcclusion={rp.gpuResidentDrawerEnableOcclusionCullingInCameras} lodBias={QualitySettings.lodBias}";
        File.WriteAllText(Root+"RenderRepairValidation.txt",report);Debug.Log("RENDER_REPAIRS_VALIDATED "+report);
    }
    [MenuItem("Tools/World/Verify Building Angles")]
    public static void Angles()
    {
        if(EditorApplication.isPlaying)throw new InvalidOperationException("Run angle validation in Edit mode; temporary render layers are cached until the next player frame.");
        var groups=All<LODGroup>().Where(g=>g.gameObject.activeInHierarchy&&(g.name=="Preset_House_01_A" || g.name=="Preset_Church_01_A")).GroupBy(g=>g.name).Select(g=>g.First()).ToArray();
        var rp=(QualitySettings.renderPipeline?QualitySettings.renderPipeline:GraphicsSettings.defaultRenderPipeline) as UniversalRenderPipelineAsset;
        var pipeline=new SerializedObject(rp);var list=pipeline.FindProperty("m_RendererDataList");
        var features=new Dictionary<ScriptableRendererFeature,bool>();
        for(int i=0;i<list.arraySize;i++){var data=list.GetArrayElementAtIndex(i).objectReferenceValue as ScriptableRendererData;if(data)foreach(var f in data.rendererFeatures)if(f&&!features.ContainsKey(f))features.Add(f,f.isActive);}
        var cameraObject=new GameObject("Building angle validation camera");var camera=cameraObject.AddComponent<Camera>();camera.enabled=false;camera.orthographic=true;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.magenta;camera.cullingMask=1<<31;camera.allowHDR=false;camera.useOcclusionCulling=false;
        var extra=camera.GetUniversalAdditionalCameraData();extra.renderShadows=false;extra.renderPostProcessing=false;extra.volumeLayerMask=0;
        var rt=new RenderTexture(128,128,24,RenderTextureFormat.ARGB32);rt.Create();var pixels=new Texture2D(128,128,TextureFormat.RGBA32,false);var previous=RenderTexture.active;
        var layers=new Dictionary<GameObject,int>();int samples=0;var report=new List<string>();
        try
        {
            foreach(var f in features.Keys)f.SetActive(false);
            foreach(var group in groups)
            {
                var lods=group.GetLODs();var renderers=lods.SelectMany(l=>l.renderers).Where(r=>r&&r.shadowCastingMode!=ShadowCastingMode.ShadowsOnly).Distinct().ToArray();
                foreach(var r in renderers){if(!layers.ContainsKey(r.gameObject))layers.Add(r.gameObject,r.gameObject.layer);r.gameObject.layer=31;}
                var bounds=renderers[0].bounds;foreach(var r in renderers)bounds.Encapsulate(r.bounds);
                float size=Mathf.Max(bounds.size.x,Mathf.Max(bounds.size.y,bounds.size.z));camera.orthographicSize=size*.8f;camera.nearClipPlane=.1f;camera.farClipPlane=size*8+100;
                try
                {
                    for(int lod=0;lod<lods.Length;lod++)for(int a=0;a<8;a++)
                    {
                        group.ForceLOD(lod);camera.transform.position=bounds.center+Quaternion.Euler(12,a*45,0)*Vector3.back*(size*3+5);camera.transform.LookAt(bounds.center);
                        RenderPipeline.SubmitRenderRequest(camera,new UniversalRenderPipeline.SingleCameraRequest{destination=rt});
                        RenderTexture.active=rt;pixels.ReadPixels(new Rect(0,0,128,128),0,0);pixels.Apply();
                        int background=pixels.GetPixels32().Count(p=>p.r>230&&p.g<20&&p.b>230);int covered=128*128-background;
                        if(covered<100 || background<500)throw new Exception($"Building image invalid: {group.name} lod={lod} yaw={a*45} pixels={covered} background={background}");
                        samples++;report.Add($"{group.name} LOD{lod} yaw={a*45} visiblePixels={covered}");
                        if(lod==0&&a%2==0)File.WriteAllBytes(Root+$"Building_{group.name}_{a*45}.png",pixels.EncodeToPNG());
                    }
                }
                finally{group.ForceLOD(-1);foreach(var r in renderers)r.gameObject.layer=layers[r.gameObject];}
            }
        }
        finally
        {
            foreach(var pair in layers)if(pair.Key)pair.Key.layer=pair.Value;
            foreach(var pair in features)if(pair.Key)pair.Key.SetActive(pair.Value);
            RenderTexture.active=previous;rt.Release();UnityEngine.Object.DestroyImmediate(rt);UnityEngine.Object.DestroyImmediate(pixels);UnityEngine.Object.DestroyImmediate(cameraObject);
        }
        if(samples==0)throw new Exception("No buildings tested");
        File.WriteAllLines(Root+"BuildingAngleValidation.txt",report);Debug.Log("BUILDING_ANGLES_VERIFIED samples="+samples);
    }
    [MenuItem("Tools/World/Profile Remaining Frame Cost")]
    public static void ProfileFrame()
    {
        if(!EditorApplication.isPlaying)throw new Exception("Enter Play mode");
        bool wasEnabled=UnityEditorInternal.ProfilerDriver.enabled;
        UnityEditorInternal.ProfilerDriver.enabled=true;
        int start=Time.frameCount;
        EditorApplication.CallbackFunction update=null;
        update=()=>{
            if(!EditorApplication.isPlaying){EditorApplication.update-=update;UnityEditorInternal.ProfilerDriver.enabled=wasEnabled;return;}
            if(Time.frameCount-start<20)return;
            EditorApplication.update-=update;
            try
            {
                using(var frame=UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(UnityEditorInternal.ProfilerDriver.lastFrameIndex-1,0))
                {
                    var samples=new List<KeyValuePair<string,float>>();
                    if(frame.valid)for(int i=0;i<frame.sampleCount;i++)samples.Add(new KeyValuePair<string,float>(frame.GetSampleName(i),frame.GetSampleTimeMs(i)));
                    File.WriteAllLines(Root+"RemainingFrameCost.txt",samples.OrderByDescending(s=>s.Value).Take(65).Select(s=>s.Value.ToString("F3")+"ms "+s.Key));
                }
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=wasEnabled;}
            Debug.Log("REMAINING_FRAME_COST_CAPTURED");
        };EditorApplication.update+=update;
    }
    [MenuItem("Tools/World/Use Integrated GPU Profile")]
    public static void IntegratedProfile()
    {
        if(EditorApplication.isPlaying)throw new Exception("Exit Play mode");
        int level=Array.FindIndex(QualitySettings.names,n=>n=="Desktop Low");if(level<0)throw new Exception("Desktop Low quality missing");
        QualitySettings.SetQualityLevel(level,true);QualitySettings.lodBias=.75f;QualitySettings.terrainPixelError=12;
        var rp=QualitySettings.renderPipeline as UniversalRenderPipelineAsset;if(!rp)throw new Exception("Quality pipeline missing");
        rp.renderScale=.75f;rp.mainLightShadowmapResolution=1024;rp.shadowDistance=70;rp.shadowCascadeCount=2;rp.additionalLightsShadowmapResolution=512;rp.maxAdditionalLightsCount=2;rp.gpuResidentDrawerEnableOcclusionCullingInCameras=false;Record(rp);
        AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
        Debug.Log("INTEGRATED_GPU_PROFILE_APPLIED quality="+QualitySettings.names[level]+" pipeline="+AssetDatabase.GetAssetPath(rp));
    }
}
