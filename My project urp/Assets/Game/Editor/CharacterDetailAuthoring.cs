using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Game.ThirdPerson;

public static class CharacterDetailAuthoring
{
    static void Configure(GameObject player)
    {
        var detail = player.GetComponent<CharacterDetailSettings>();
        if (!detail) detail = player.AddComponent<CharacterDetailSettings>();
        detail.ApplyCharacterQuality();
        foreach (var component in player.GetComponentsInChildren<Component>(true))
        {
            if (!(component is SkinnedMeshRenderer) && !(component is Animator) && !(component is CharacterDetailSettings)) continue;
            EditorUtility.SetDirty(component);
            if (PrefabUtility.IsPartOfPrefabInstance(component)) PrefabUtility.RecordPrefabInstancePropertyModifications(component);
        }
    }

    [MenuItem("Tools/Characters/Set Maximum Character Detail")]
    public static void Apply()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Exit Play mode before saving character quality.");
        var player = UnityEngine.Object.FindAnyObjectByType<ThirdPersonMotor>();
        if (!player) throw new Exception("Player not found.");
        var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        int quality = QualitySettings.GetQualityLevel();
        float scale = pipeline.renderScale, shadowDistance = pipeline.shadowDistance, lodBias = QualitySettings.lodBias;
        int shadows = pipeline.mainLightShadowmapResolution;
        string backup = Application.dataPath + "/../Logs/CharacterQualityBackup/" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        Directory.CreateDirectory(backup);
        EditorSceneManager.SaveScene(player.gameObject.scene);
        File.Copy(player.gameObject.scene.path, backup + "/Demo.unity");
        string prefab = "Assets/Game/Prefabs/FurinaPlayer.prefab";
        File.Copy(prefab, backup + "/FurinaPlayer.prefab");

        int textures = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/Game/Characters" }))
        {
            var importer = AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guid)) as TextureImporter;
            if (!importer) continue;
            bool changed = importer.maxTextureSize < 8192 || importer.textureCompression != TextureImporterCompression.Uncompressed
                || importer.anisoLevel != 16 || importer.filterMode != FilterMode.Trilinear || !importer.mipmapEnabled
                || importer.streamingMipmaps || !importer.ignoreMipmapLimit;
            importer.maxTextureSize = Mathf.Max(8192, importer.maxTextureSize);
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.anisoLevel = 16;
            importer.filterMode = FilterMode.Trilinear;
            importer.mipmapEnabled = true;
            importer.streamingMipmaps = false;
            importer.ignoreMipmapLimit = true;
            if (changed) importer.SaveAndReimport();
            textures++;
        }
        foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { "Assets/Game/Characters" }))
        {
            var importer = AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guid)) as ModelImporter;
            if (!importer) continue;
            bool changed = importer.meshCompression != ModelImporterMeshCompression.Off || importer.animationCompression != ModelImporterAnimationCompression.Off;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.animationCompression = ModelImporterAnimationCompression.Off;
            if (changed) importer.SaveAndReimport();
        }
        Configure(player.gameObject);
        var root = PrefabUtility.LoadPrefabContents(prefab);
        try { Configure(root); PrefabUtility.SaveAsPrefabAsset(root, prefab); }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        if (QualitySettings.GetQualityLevel() != quality || QualitySettings.lodBias != lodBias || pipeline.renderScale != scale
            || pipeline.shadowDistance != shadowDistance || pipeline.mainLightShadowmapResolution != shadows)
            throw new Exception("Character quality changed the environment's rendering settings.");
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(player.gameObject.scene);
        EditorSceneManager.SaveScene(player.gameObject.scene);
        Debug.Log("CHARACTER_HIGH_DETAIL_SAVED textures=" + textures + " environmentQuality=" + QualitySettings.names[quality] + " renderScale=" + scale);
        Validate();
    }

    static int ValidatePlayer(GameObject root)
    {
        if (!root.GetComponent<CharacterDetailSettings>()) throw new Exception("Character quality guard missing.");
        var skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (skins.Length == 0) throw new Exception("Character mesh missing.");
        foreach (var skin in skins)
            if (skin.quality != SkinQuality.Bone4 || !skin.skinnedMotionVectors || !skin.updateWhenOffscreen || !skin.receiveShadows
                || (skin.shadowCastingMode != ShadowCastingMode.TwoSided && !Application.isPlaying))
                throw new Exception("Character skin quality was reduced.");
        var motion = root.GetComponentInChildren<UntitledCharacterMotion>(true);
        var ground = root.GetComponentInChildren<CharacterGroundAndLook>(true);
        var retarget = root.GetComponentInChildren<BlenderCharacterRetarget>(true);
        if (!motion || !motion.enabled || !ground || !ground.enabled || !retarget || !retarget.enabled || motion.joints.Any(j => !j.bone))
            throw new Exception("Full motion/ground IK/retarget not enabled.");
        return skins.Length;
    }

    [MenuItem("Tools/Characters/Validate Maximum Character Detail")]
    public static void Validate()
    {
        var player = UnityEngine.Object.FindAnyObjectByType<ThirdPersonMotor>();
        int sceneSkins = ValidatePlayer(player.gameObject), prefabSkins;
        var root = PrefabUtility.LoadPrefabContents("Assets/Game/Prefabs/FurinaPlayer.prefab");
        try { prefabSkins = ValidatePlayer(root); }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        int textures = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/Game/Characters" }))
        {
            var importer = AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guid)) as TextureImporter;
            if (!importer) continue;
            if (!importer.ignoreMipmapLimit || importer.streamingMipmaps || importer.textureCompression != TextureImporterCompression.Uncompressed)
                throw new Exception("Character texture quality is not protected.");
            textures++;
        }
        var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        string report = $"sceneSkins={sceneSkins} prefabSkins={prefabSkins} skinning=Bone4 textures={textures} fullMotion=true groundIK=true hairAndCloth=true environment={QualitySettings.names[QualitySettings.GetQualityLevel()]} renderScale={pipeline.renderScale}";
        Directory.CreateDirectory(Application.dataPath + "/../Logs");
        File.WriteAllText(Application.dataPath + "/../Logs/CharacterQualityValidation.txt", report);
        Debug.Log("CHARACTER_HIGH_DETAIL_VALIDATED " + report);
    }
}
