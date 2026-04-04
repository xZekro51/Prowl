Shader "Default/VoxelGI_InjectLight"

Pass "InjectLight"
{
    Tags { "LightMode" = "Compute" }
    Cull None
    ZTest Off
    ZWrite Off
    Blend Off

    GLSLPROGRAM

    Vertex
    {
        layout(location = 0) in vec3 vertexPosition;
        layout(location = 1) in vec2 vertexTexCoord;
        out vec2 TexCoords;
        void main()
        {
            TexCoords = vertexTexCoord;
            gl_Position = vec4(vertexPosition, 1.0);
        }
    }

    Fragment
    {
        #include "Fragment"
        #include "GICommon"

        layout(location = 0) out vec4 outColor;
        in vec2 TexCoords;

        uniform vec3 _LightDirection;
        uniform vec3 _LightColor;
        uniform float _LightIntensity;
        uniform vec3 _VoxelGridCenter;
        uniform float _VoxelGridSize;
        uniform int _VoxelResolution;

        void main()
        {
            // Placeholder for compute-based light injection
            // When compute shaders are supported, this will operate on the 3D voxel texture
            outColor = vec4(0.0);
        }
    }

    ENDGLSL
}
