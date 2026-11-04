using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Game.ThirdPerson;

public static class CharacterDetailAuthoring
{
    [MenuItem("Tools/Characters/Set Maximum Character Detail")]
    public static void Apply()
    {
        QualitySettings.skinWeights=SkinWeights.FourBones;
        QualitySettings.globalTextureMipmapLimit=0;
        QualitySettings.anisotropicFiltering=AnisotropicFiltering.ForceEnable;
        int textures=0;
        foreach(var guid in AssetDatabase.FindAssets("t:Texture2D",new[]{"Assets/Game/Characters"})) {
            var importer=AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guid)) as TextureImporter;
            if(!importer)continue;
            importer.maxTextureSize=8192;
            importer.textureCompression=TextureImporterCompression.Uncompressed;
            importer.anisoLevel=16;importer.filterMode=FilterMode.Trilinear;
            importer.mipmapEnabled=true;importer.streamingMipmaps=false;
            importer.SaveAndReimport();textures++;
        }
        foreach(var guid in AssetDatabase.FindAssets("t:Model",new[]{"Assets/Game/Characters"})) {
            var importer=AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guid)) as ModelImporter;
            if(!importer)continue;
            importer.meshCompression=ModelImporterMeshCompression.Off;
            importer.animationCompression=ModelImporterAnimationCompression.Off;
            importer.SaveAndReimport();
        }
        var pipeline=GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if(pipeline) {
            var serialized=new SerializedObject(pipeline);
            serialized.FindProperty("m_RenderScale").floatValue=1.25f;
            serialized.FindProperty("m_AdditionalLightsRenderingMode").intValue=1;
            serialized.FindProperty("m_AdditionalLightsPerObjectLimit").intValue=8;
            serialized.FindProperty("m_AdditionalLightShadowsSupported").boolValue=true;
            serialized.FindProperty("m_AdditionalLightsShadowmapResolution").intValue=4096;
            serialized.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(pipeline);
        }
        var renderer=AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/URP/Settings/Desktop Renderer.asset");
        if(renderer){var so=new SerializedObject(renderer);so.FindProperty("m_AccurateGbufferNormals").boolValue=true;so.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(renderer);}
        var player=Object.FindAnyObjectByType<ThirdPersonMotor>();
        if(!player.GetComponent<CharacterDetailSettings>())player.gameObject.AddComponent<CharacterDetailSettings>();
        foreach(var skin in player.GetComponentsInChildren<SkinnedMeshRenderer>(true)){
            skin.quality=SkinQuality.Bone4;skin.skinnedMotionVectors=true;skin.receiveShadows=true;skin.shadowCastingMode=ShadowCastingMode.TwoSided;
            EditorUtility.SetDirty(skin);
        }
        var camera=player.cameraTransform.GetComponent<Camera>();camera.allowDynamicResolution=false;
        var data=camera.GetUniversalAdditionalCameraData();data.antialiasing=AntialiasingMode.SubpixelMorphologicalAntiAliasing;data.antialiasingQuality=AntialiasingQuality.High;
        EditorUtility.SetDirty(data);EditorUtility.SetDirty(camera);
        AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(player.gameObject.scene);EditorSceneManager.SaveScene(player.gameObject.scene);
        Debug.Log("MAX_CHARACTER_DETAIL textures="+textures+" renderScale=1.25 skinning=4 normals=accurate SMAA=high");
    }
}
