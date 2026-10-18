using System;

namespace UnityEngine.Rendering.Universal
{
    // Keep this type in its original file so the packaged legacy resource asset
    // retains its MonoScript GUID. Runtime and editor code use GraphicsSettings.
    public partial class UniversalRenderPipelineEditorResources : ScriptableObject
    {
        [Serializable, ReloadGroup]
        sealed class ShaderResources
        {
            [Reload("Shaders/AutodeskInteractive/AutodeskInteractive.shadergraph")]
            public Shader autodeskInteractivePS;

            [Reload("Shaders/AutodeskInteractive/AutodeskInteractiveTransparent.shadergraph")]
            public Shader autodeskInteractiveTransparentPS;

            [Reload("Shaders/AutodeskInteractive/AutodeskInteractiveMasked.shadergraph")]
            public Shader autodeskInteractiveMaskedPS;

            [Reload("Shaders/Terrain/TerrainDetailLit.shader")]
            public Shader terrainDetailLitPS;

            [Reload("Shaders/Terrain/WavingGrass.shader")]
            public Shader terrainDetailGrassPS;

            [Reload("Shaders/Terrain/WavingGrassBillboard.shader")]
            public Shader terrainDetailGrassBillboardPS;

            [Reload("Shaders/Nature/SpeedTree7.shader")]
            public Shader defaultSpeedTree7PS;

            [Reload("Shaders/Nature/SpeedTree8_PBRLit.shadergraph")]
            public Shader defaultSpeedTree8PS;

            [Reload("Shaders/Nature/SpeedTree9_URP.shadergraph")]
            public Shader defaultSpeedTree9PS;
        }

        [Serializable, ReloadGroup]
        sealed class MaterialResources
        {
            [Reload("Runtime/Materials/Lit.mat")]
            public Material lit;

            [Reload("Runtime/Materials/ParticlesUnlit.mat")]
            public Material particleLit;

            [Reload("Runtime/Materials/TerrainLit.mat")]
            public Material terrainLit;

            [Reload("Runtime/Materials/Decal.mat")]
            public Material decal;
        }

        // These fields only deserialize the packaged pre-GraphicsSettings asset.
        [SerializeField]
        ShaderResources shaders;

        [SerializeField]
        MaterialResources materials;
    }
}
