Shader "Default/SDFGI_ProbeUpdate"

Pass "UpdateProbes"
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
        uniform int _ProbeResolution;
        uniform int _ProbeUpdateOffset;
        uniform float _Hysteresis;
        uniform vec3 _LightDirection;
        uniform vec3 _LightColor;

        void main()
        {
            // Placeholder for compute-based irradiance probe update
            // When compute shaders are supported, this will:
            // 1. For each probe, trace N rays through the global SDF
            // 2. At hit points, evaluate direct lighting
            // 3. Accumulate L1 SH coefficients
            // 4. Blend with previous value using hysteresis
            outColor = vec4(0.0);
        }
    }

    ENDGLSL
}
