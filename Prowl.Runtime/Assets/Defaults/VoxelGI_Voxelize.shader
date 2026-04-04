Shader "Default/VoxelGI_Voxelize"

Pass "Voxelize"
{
    Tags { "LightMode" = "Voxelize" }
    Cull None
    ZTest Off
    ZWrite Off
    Blend Off

    GLSLPROGRAM

    Vertex
    {
        layout(location = 0) in vec3 vertexPosition;
        layout(location = 1) in vec2 vertexTexCoord;
        layout(location = 2) in vec3 vertexNormal;

        out vec3 v_WorldPos;
        out vec3 v_Normal;
        out vec2 v_TexCoord;

        void main()
        {
            vec4 worldPos = PROWL_MATRIX_M * vec4(vertexPosition, 1.0);
            v_WorldPos = worldPos.xyz;
            v_Normal = normalize(mat3(PROWL_MATRIX_M) * vertexNormal);
            v_TexCoord = vertexTexCoord;
            gl_Position = PROWL_MATRIX_VP * worldPos;
        }
    }

    Fragment
    {
        #include "Fragment"

        layout(location = 0) out vec4 outColor;

        in vec3 v_WorldPos;
        in vec3 v_Normal;
        in vec2 v_TexCoord;

        uniform vec3 _VoxelGridCenter;
        uniform float _VoxelGridSize;
        uniform int _VoxelResolution;
        uniform sampler2D _MainTex;

        void main()
        {
            // Convert world position to voxel grid coordinates
            vec3 localPos = (v_WorldPos - _VoxelGridCenter) / _VoxelGridSize;
            localPos = localPos * 0.5 + 0.5; // [0, 1]
            ivec3 voxelCoord = ivec3(localPos * float(_VoxelResolution));

            // Bounds check
            if (any(lessThan(voxelCoord, ivec3(0))) ||
                any(greaterThanEqual(voxelCoord, ivec3(_VoxelResolution))))
                discard;

            vec4 albedo = texture(_MainTex, v_TexCoord);
            vec3 normal = normalize(v_Normal) * 0.5 + 0.5;

            // Output albedo with opacity for voxel accumulation
            outColor = vec4(albedo.rgb, 1.0);
        }
    }

    ENDGLSL
}
