using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;
using System;
using System.Linq;
public static class ImportFurinaURP {
[MenuItem("Tools/Characters/Setup Furina Only")]
public static void Build(){
 if(EditorApplication.isPlaying)throw new Exception("Exit Play Mode first");var scene=SceneManager.GetActiveScene();if(GameObject.Find("Furina Player"))throw new Exception("Player already exists");
 var path="Assets/Game/Characters/BlenderAlternate";var shader=Shader.Find("Universal Render Pipeline/Lit");if(!shader)throw new Exception("URP shader missing");
 foreach(var guid in AssetDatabase.FindAssets("t:Material",new[]{path})){var mat=AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));var texture=mat.HasProperty("_BaseColorMap")?mat.GetTexture("_BaseColorMap"):null;bool transparent=mat.HasProperty("_SurfaceType")&&mat.GetFloat("_SurfaceType")>0;mat.shader=shader;mat.shaderKeywords=new string[0];mat.SetTexture("_BaseMap",texture);mat.SetColor("_BaseColor",Color.white);mat.SetFloat("_Smoothness",.23f);mat.SetFloat("_Metallic",0);mat.SetFloat("_Cull",0);mat.SetFloat("_Surface",transparent?1:0);mat.SetFloat("_ZWrite",transparent?0:1);mat.SetFloat("_SrcBlend",transparent?(float)UnityEngine.Rendering.BlendMode.SrcAlpha:1);mat.SetFloat("_DstBlend",transparent?(float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha:0);if(transparent){mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");mat.SetOverrideTag("RenderType","Transparent");mat.renderQueue=3000;}else{mat.SetOverrideTag("RenderType","Opaque");mat.renderQueue=2000;}mat.enableInstancing=true;EditorUtility.SetDirty(mat);}
 var camera=Camera.main;if(!camera)camera=UnityEngine.Object.FindAnyObjectByType<Camera>();Vector3 spawn=Vector3.zero;bool found=false;
 if(camera){var direction=Vector3.ProjectOnPlane(camera.transform.forward,Vector3.up).normalized;for(int i=0;i<8;i++){var point=camera.transform.position+direction*(5+i*3);RaycastHit hit;if(Physics.Raycast(point+Vector3.up*100,Vector3.down,out hit,2000,~0,QueryTriggerInteraction.Ignore)&&hit.normal.y>.7f){spawn=hit.point+Vector3.up*.05f;found=true;break;}}}
 if(!found)foreach(var t in Terrain.activeTerrains){spawn=t.transform.position+t.terrainData.size*.5f;spawn.y=t.SampleHeight(spawn)+t.transform.position.y+.05f;found=true;break;}
 if(!found)throw new Exception("No ground found to place player");
 var player=new GameObject("Furina Player");player.tag="Player";player.transform.position=spawn;var controller=player.AddComponent<CharacterController>();controller.height=1.65f;controller.center=new Vector3(0,.84f,0);controller.radius=.23f;controller.stepOffset=.3f;controller.slopeLimit=48;var motor=player.AddComponent<Game.ThirdPerson.ThirdPersonMotor>();motor.walkSpeed=1.2f;motor.sprintSpeed=4;motor.jumpHeight=.8f;
 var visual=new GameObject("ModelRoot");visual.transform.SetParent(player.transform,false);motor.visualRoot=visual.transform;
 var model=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path+"/Furina.prefab"));model.transform.SetParent(visual.transform,false);PrefabUtility.UnpackPrefabInstance(model,PrefabUnpackMode.Completely,InteractionMode.AutomatedAction);foreach(var t in model.GetComponentsInChildren<Transform>(true))GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);foreach(var animator in model.GetComponentsInChildren<Animator>(true))UnityEngine.Object.DestroyImmediate(animator);
 var renderers=model.GetComponentsInChildren<SkinnedMeshRenderer>(true);var bounds=renderers[0].bounds;foreach(var r in renderers)bounds.Encapsulate(r.bounds);float scale=1.65f/bounds.size.y;model.transform.localScale*=scale;bounds=renderers[0].bounds;foreach(var r in renderers)bounds.Encapsulate(r.bounds);model.transform.position+=new Vector3(spawn.x-bounds.center.x,spawn.y-bounds.min.y,spawn.z-bounds.center.z);
 var motion=model.AddComponent<Game.ThirdPerson.FurinaMotion>();motion.motor=motor;motion.LowerArms();
 if(!camera){var o=new GameObject("Main Camera");camera=o.AddComponent<Camera>();o.tag="MainCamera";o.AddComponent<AudioListener>();}
 foreach(var root in scene.GetRootGameObjects())if(root.name=="VirtualCameras"||root.name=="Timelines"||root.name=="UI")root.SetActive(false);
 foreach(var component in camera.GetComponents<MonoBehaviour>())if(component&&!(component is UnityEngine.Rendering.Universal.UniversalAdditionalCameraData))component.enabled=false;
 var target=new GameObject("CameraTarget");target.transform.SetParent(player.transform,false);target.transform.localPosition=new Vector3(0,1.5f,0);var follow=camera.gameObject.AddComponent<Game.ThirdPerson.ThirdPersonFollowCamera>();follow.target=target.transform;follow.playerRoot=player.transform;follow.characterRoot=model.transform;follow.zoomStep=12;follow.distance=4;follow.pitch=15;follow.yaw=camera.transform.eulerAngles.y;motor.cameraTransform=camera.transform;follow.UpdateView(1);camera.nearClipPlane=.05f;
 if(!AssetDatabase.IsValidFolder("Assets/Game/Prefabs"))AssetDatabase.CreateFolder("Assets/Game","Prefabs");PrefabUtility.SaveAsPrefabAsset(player,"Assets/Game/Prefabs/FurinaPlayer.prefab");EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);AssetDatabase.SaveAssets();Selection.activeGameObject=player;SceneView.lastActiveSceneView?.Frame(new Bounds(spawn+Vector3.up,Vector3.one*3),false);Debug.Log("FURINA_URP_READY scene="+scene.path+" spawn="+spawn+" height="+bounds.size.y+" renderers="+renderers.Length);
}
}

