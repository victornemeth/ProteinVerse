using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public class ShaderFixer
{
    static ShaderFixer()
    {
        EnsureShadersIncluded();
    }

    static void EnsureShadersIncluded()
    {
        var graphicsSettingsAsset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
        if (graphicsSettingsAsset == null || graphicsSettingsAsset.Length == 0) return;

        var serializedObject = new SerializedObject(graphicsSettingsAsset[0]);
        var arrayProp = serializedObject.FindProperty("m_AlwaysIncludedShaders");
        if (arrayProp == null) return;

        string[] shadersToAdd = { "Custom/PointCloud", "Sprites/Default", "Unlit/Color" };
        bool modified = false;

        foreach (var shaderName in shadersToAdd)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null) continue;

            bool hasShader = false;
            for (int i = 0; i < arrayProp.arraySize; ++i)
            {
                var arrayElement = arrayProp.GetArrayElementAtIndex(i);
                if (arrayElement.objectReferenceValue == shader)
                {
                    hasShader = true;
                    break;
                }
            }

            if (!hasShader)
            {
                arrayProp.InsertArrayElementAtIndex(arrayProp.arraySize);
                arrayProp.GetArrayElementAtIndex(arrayProp.arraySize - 1).objectReferenceValue = shader;
                modified = true;
                Debug.Log($"[ShaderFixer] Added shader '{shaderName}' to Always Included Shaders.");
            }
        }

        if (modified)
        {
            serializedObject.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
        }
    }
}
