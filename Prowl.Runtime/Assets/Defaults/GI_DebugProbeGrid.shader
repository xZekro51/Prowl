Shader "Default/GI_DebugProbeGrid"

Pass "DebugProbeGrid"
{
    Tags { "RenderOrder" = "Opaque" }
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

        uniform sampler3D _ProbeIrradiance0;
        uniform sampler2D _CameraDepthTexture;
        uniform vec3 _CascadeCenter0;
        uniform float _CascadeSize0;
        uniform int _ProbeResolution;

        void main()
        {
            float depth = texture(_CameraDepthTexture, TexCoords).r;
            if (depth >= 1.0)
            {
                outColor = vec4(0.0, 0.0, 0.0, 1.0);
                return;
            }

            mat4 invVP = inverse(PROWL_MATRIX_VP);
            vec3 worldPos = GI_WorldPosFromDepth(depth, TexCoords, invVP);

            vec3 uvw = WorldToVoxelUVW(worldPos, _CascadeCenter0, _CascadeSize0);

            if (!IsInsideVolume(uvw))
            {
                outColor = vec4(0.0, 0.0, 0.0, 1.0);
                return;
            }

            vec3 probeCoord = uvw * float(_ProbeResolution);
            vec3 nearestProbe = floor(probeCoord) + 0.5;
            float distToProbe = length(probeCoord - nearestProbe);

            // Render probe as a small sphere
            float probeRadius = 0.3; // In probe-grid units
            if (distToProbe < probeRadius)
            {
                // Sample probe irradiance and decode as color
                vec3 probeUVW = nearestProbe / float(_ProbeResolution);
                vec4 sh = texture(_ProbeIrradiance0, probeUVW);
                vec3 irradiance = SHDecodeL1(sh, vec3(0.0, 1.0, 0.0)); // Decode for up direction
                outColor = vec4(irradiance * 2.0, 1.0); // Boost for visibility
            }
            else
            {
                // Background — show dimmed scene
                outColor = vec4(0.0, 0.0, 0.0, 1.0);
            }
        }
    }

    ENDGLSL
}
