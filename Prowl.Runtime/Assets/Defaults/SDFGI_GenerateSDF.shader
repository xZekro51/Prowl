Shader "Default/SDFGI_GenerateSDF"

Pass "GenerateMeshSDF"
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

        uniform int _TriangleCount;
        uniform vec3 _BoundsMin;
        uniform vec3 _BoundsMax;
        uniform int _Resolution;

        void main()
        {
            // Placeholder for compute-based per-mesh SDF generation
            // When compute shaders are supported, this will:
            // 1. Upload mesh triangle data to SSBO
            // 2. For each voxel, compute min signed distance to all triangles
            outColor = vec4(0.0);
        }
    }

    ENDGLSL
}
