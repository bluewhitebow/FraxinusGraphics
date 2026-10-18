using UnityEngine;
using UnityEditor.ShaderGraph;

namespace UnityEditor.Rendering.Universal.ShaderGraph
{
    static class UniversalBlockFields
    {
        [GenerateBlocks("Universal Render Pipeline")]
        public struct VertexDescription
        {
            public static string name = "VertexDescription";
            public static BlockFieldDescriptor MotionVector = new BlockFieldDescriptor(VertexDescription.name, "MotionVector", "Motion Vector", "VERTEXDESCRIPTION_MOTIONVECTOR",
                new Vector3Control(new Vector3(0.0f, 0.0f, 0.0f)), ShaderStage.Vertex);
        }

        [GenerateBlocks("Universal Render Pipeline")]
        public struct SurfaceDescription
        {
            public static string name = "SurfaceDescription";
            public static BlockFieldDescriptor SpriteMask = new BlockFieldDescriptor(SurfaceDescription.name, "SpriteMask", "Sprite Mask", "SURFACEDESCRIPTION_SPRITEMASK",
                new ColorRGBAControl(new Color(1, 1, 1, 1)), ShaderStage.Fragment);

            public static BlockFieldDescriptor NormalAlpha = new BlockFieldDescriptor(SurfaceDescription.name, "NormalAlpha", "Normal Alpha", "SURFACEDESCRIPTION_NORMALALPHA",
                new FloatControl(1.0f), ShaderStage.Fragment);
            public static BlockFieldDescriptor MAOSAlpha = new BlockFieldDescriptor(SurfaceDescription.name, "MAOSAlpha", "MAOS Alpha", "SURFACEDESCRIPTION_MAOSALPHA",
                new FloatControl(1.0f), ShaderStage.Fragment);

            // Six-way lighting blocks. They are declared here rather than
            // relying on Unity 6 ShaderGraph's built-in descriptors so graphs
            // can deserialize on ShaderGraph 14.
            public static BlockFieldDescriptor MapRightTopBack = new BlockFieldDescriptor(SurfaceDescription.name, "MapRightTopBack", "Right Top Back", "SURFACEDESCRIPTION_MAPRIGHTTOPBACK",
                new Vector3Control(new Vector3(0, 0, 0)), ShaderStage.Fragment);
            public static BlockFieldDescriptor MapLeftBottomFront = new BlockFieldDescriptor(SurfaceDescription.name, "MapLeftBottomFront", "Left Bottom Front", "SURFACEDESCRIPTION_MAPLEFTBOTTOMFRONT",
                new Vector3Control(new Vector3(0, 0, 0)), ShaderStage.Fragment);
            public static BlockFieldDescriptor AbsorptionStrength = new BlockFieldDescriptor(SurfaceDescription.name, "AbsorptionStrength", "Absorption Strength", "SURFACEDESCRIPTION_ABSORPTIONSTRENGTH",
                new FloatControl(0.0f), ShaderStage.Fragment);
        }
    }

    // ShaderGraph 17 owns these descriptors. ShaderGraph 14 does not, so select
    // URP's descriptors while retaining the same SurfaceDescription reference names.
    static class SixWayBlockFields
    {
        public static BlockFieldDescriptor MapRightTopBack =>
#if UNITY_6000_0_OR_NEWER
            BlockFields.SurfaceDescription.MapRightTopBack;
#else
            UniversalBlockFields.SurfaceDescription.MapRightTopBack;
#endif

        public static BlockFieldDescriptor MapLeftBottomFront =>
#if UNITY_6000_0_OR_NEWER
            BlockFields.SurfaceDescription.MapLeftBottomFront;
#else
            UniversalBlockFields.SurfaceDescription.MapLeftBottomFront;
#endif

        public static BlockFieldDescriptor AbsorptionStrength =>
#if UNITY_6000_0_OR_NEWER
            BlockFields.SurfaceDescription.AbsorptionStrength;
#else
            UniversalBlockFields.SurfaceDescription.AbsorptionStrength;
#endif
    }
}
