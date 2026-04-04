Shader "Default/VoxelGI_Mipmap"

Pass "GenerateMipmap"
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

        layout(location = 0) out vec4 outColor;
        in vec2 TexCoords;

        uniform int _SourceSize;
        uniform int _MipLevel;

        void main()
        {
            // Placeholder for compute-based mipmap generation
            // When compute shaders are supported, this will generate mipmaps
            // for the 3D voxel radiance texture using a 2x2x2 box filter
            outColor = vec4(0.0);
        }
    }

    ENDGLSL
}
