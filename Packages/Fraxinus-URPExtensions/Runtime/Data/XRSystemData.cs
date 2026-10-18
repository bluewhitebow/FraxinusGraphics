using System;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.ProjectWindowCallback;
#endif

namespace UnityEngine.Rendering.Universal
{
    // Keep this type in its original file so existing XRSystemData assets retain
    // their MonoScript GUID while the active runtime resources live in GraphicsSettings.
    [Serializable]
    public partial class XRSystemData : ScriptableObject
    {
        [Serializable, ReloadGroup]
        sealed class ShaderResources
        {
            [Reload("Shaders/XR/XROcclusionMesh.shader")]
            public Shader xrOcclusionMeshPS;

            [Reload("Shaders/XR/XRMirrorView.shader")]
            public Shader xrMirrorViewPS;

            [Reload("Shaders/XR/XRMotionVector.shader")]
            public Shader xrMotionVector;
        }

        // Serialized by legacy renderer data assets. Retain it as data rather
        // than exposing the removed resource container as an active API.
        [SerializeField]
        ShaderResources shaders;

#if UNITY_EDITOR
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Microsoft.Performance", "CA1812")]
        internal class CreateXRSystemDataAsset : EndNameEditAction
        {
            public override void Action(
                int instanceId, string pathName, string resourceFile)
            {
                var instance = CreateInstance<XRSystemData>();
                AssetDatabase.CreateAsset(instance, pathName);
                ResourceReloader.ReloadAllNullIn(
                    instance, UniversalRenderPipelineAsset.packagePath);
                Selection.activeObject = instance;
            }
        }

        [MenuItem(
            "Assets/Create/Rendering/URP XR System Data",
            priority = CoreUtils.Sections.section5 +
                CoreUtils.Priorities.assetsCreateRenderingMenuPriority)]
        static void CreateXRSystemData()
        {
            ProjectWindowUtil.StartNameEditingIfProjectWindowExists(
                0,
                CreateInstance<CreateXRSystemDataAsset>(),
                "CustomXRSystemData.asset",
                null,
                null);
        }
#endif
    }
}
