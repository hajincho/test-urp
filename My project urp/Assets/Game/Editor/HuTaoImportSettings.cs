using UnityEditor;
using UnityEngine;

/// <summary>
/// Applies the import settings the Hu Tao runtime setup needs, only to assets under Characters/HuTao/:
/// readable meshes (so joint utilization can count skin-weighted vertices), no embedded materials
/// (CharacterSwitcher assigns URP materials), no animation clips, and a preserved bone hierarchy.
/// If the FBX was imported before this script compiled, it is re-imported once automatically.
/// </summary>
public sealed class HuTaoImportSettings : AssetPostprocessor
{
    const string ModelPath = "Assets/Game/Characters/HuTao/Resources/HuTao/HuTao.fbx";

    static bool IsHuTao(string path) => path.Replace('\\', '/').Contains("/Characters/HuTao/");

    static void Configure(ModelImporter importer)
    {
        importer.isReadable = true;
        importer.importAnimation = false;
        importer.animationType = ModelImporterAnimationType.Generic;
        importer.materialImportMode = ModelImporterMaterialImportMode.None;
        importer.optimizeGameObjects = false;
        importer.preserveHierarchy = true;
        importer.importCameras = false;
        importer.importLights = false;
    }

    void OnPreprocessModel()
    {
        if (!IsHuTao(assetPath)) return;
        Configure((ModelImporter)assetImporter);
    }

    void OnPreprocessTexture()
    {
        if (!IsHuTao(assetPath)) return;
        var importer = (TextureImporter)assetImporter;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = true;
    }

    [InitializeOnLoadMethod]
    static void EnsureModelSettings()
    {
        EditorApplication.delayCall += () =>
        {
            var importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
            if (importer == null) return;
            if (importer.isReadable && importer.materialImportMode == ModelImporterMaterialImportMode.None
                && !importer.importAnimation && importer.preserveHierarchy) return;
            Configure(importer);
            importer.SaveAndReimport();
            Debug.Log("[HuTao] Applied import settings and re-imported " + ModelPath);
        };
    }
}
