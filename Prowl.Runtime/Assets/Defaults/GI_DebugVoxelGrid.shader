Shader "Default/GI_DebugVoxelGrid"

Pass "DebugVoxelGrid"
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

        uniform sampler3D _VoxelRadiance;
        uniform sampler2D _CameraDepthTexture;
        uniform vec3 _VoxelGridCenter;
        uniform float _VoxelGridSize;
        uniform int _VoxelResolution;

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

            vec3 uvw = WorldToVoxelUVW(worldPos, _VoxelGridCenter, _VoxelGridSize);

            if (!IsInsideVolume(uvw))
            {
                outColor = vec4(0.0, 0.0, 0.0, 1.0);
                return;
            }

            vec4 voxelData = textureLod(_VoxelRadiance, uvw, 0.0);

            if (voxelData.a < 0.01)
            {
                // Empty voxel — show faint grid lines
                vec3 voxelCoord = uvw * float(_VoxelResolution);
                vec3 gridLine = abs(fract(voxelCoord) - 0.5);
                float edge = 1.0 - smoothstep(0.45, 0.5, min(gridLine.x, min(gridLine.y, gridLine.z)));
                outColor = vec4(vec3(edge * 0.05), 1.0);
            }
            else
            {
                // Occupied voxel — show stored color
                outColor = vec4(voxelData.rgb, 1.0);
            }
        }
    }

    ENDGLSL
}
