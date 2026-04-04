Shader "Default/SDFGI_MergeSDF"

Pass "MergeSDF"
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

        uniform vec3 _CascadeCenter;
        uniform float _CascadeSize;
        uniform int _CascadeResolution;
        uniform int _ObjectCount;

        void main()
        {
            // Placeholder for compute-based global SDF cascade merge
            // When compute shaders are supported, this will:
            // 1. For each voxel in the cascade, transform world pos to each object's local SDF space
            // 2. Sample the per-object SDF and take the minimum distance
            outColor = vec4(0.0);
        }
    }

    ENDGLSL
}
