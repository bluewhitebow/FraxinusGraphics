#if UNITY_6000_0_OR_NEWER
using System.Runtime.CompilerServices;
using UnityEngine;

[assembly: InternalsVisibleTo("Unity.RenderPipelines.Universal.Editor")]

namespace UnityEditor.Rendering.Universal
{
    // Unity 2022 does not expose Material.rawRenderQueue. Keep this bridge
    // available for Unity 6 builds, while compiling it out of the 2022
    // package so the shared UnityEngine.UI assembly remains valid.
    internal static class MaterialAccess
    {
        internal static int ReadMaterialRawRenderQueue(Material mat)
        {
            return mat.rawRenderQueue;
        }
    }
}
#endif
