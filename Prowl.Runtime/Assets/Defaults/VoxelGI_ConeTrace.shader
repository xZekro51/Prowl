Shader "Default/VoxelGI_ConeTrace"

Pass "ConeTrace"
{
    Tags { "RenderOrder" = "Opaque" }
    Cull None
    ZTest Off
    ZWrite Off
    Blend Additive

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
        uniform sampler2D _GBufferB;
        uniform sampler2D _GBufferC;
        uniform sampler2D _CameraDepthTexture;

        uniform vec3 _VoxelGridCenter;
        uniform float _VoxelGridSize;
        uniform int _VoxelResolution;
        uniform float _GIIntensity;
        uniform int _ConeCount;

        vec4 TraceCone(vec3 origin, vec3 direction, float coneAngle)
        {
            vec4 accumColor = vec4(0.0);
            float dist = _VoxelGridSize / float(_VoxelResolution) * 2.0;
            float maxDist = _VoxelGridSize * 2.0;

            while (dist < maxDist && accumColor.a < 0.95)
            {
                vec3 samplePos = origin + direction * dist;
                vec3 uvw = WorldToVoxelUVW(samplePos, _VoxelGridCenter, _VoxelGridSize);

                if (!IsInsideVolume(uvw))
                    break;

                float diameter = 2.0 * dist * tan(coneAngle * 0.5);
                float mipLevel = log2(max(1.0, diameter * float(_VoxelResolution) / (_VoxelGridSize * 2.0)));

                vec4 voxelSample = textureLod(_VoxelRadiance, uvw, max(0.0, mipLevel));

                float a = 1.0 - accumColor.a;
                accumColor.rgb += voxelSample.rgb * a * voxelSample.a;
                accumColor.a += voxelSample.a * a;

                dist += diameter * 0.5;
            }
            return accumColor;
        }

        void main()
        {
            float depth = texture(_CameraDepthTexture, TexCoords).r;
            if (depth >= 1.0) { outColor = vec4(0.0); return; }

            mat4 invVP = inverse(PROWL_MATRIX_VP);
            vec3 worldPos = GI_WorldPosFromDepth(depth, TexCoords, invVP);
            vec4 normalData = texture(_GBufferB, TexCoords);
            vec3 viewNormal = normalData.rgb * 2.0 - 1.0;
            vec3 worldNormal = normalize((inverse(transpose(PROWL_MATRIX_V)) * vec4(viewNormal, 0.0)).xyz);

            // Build tangent frame
            vec3 T, B;
            BuildTangentFrame(worldNormal, T, B);

            // Trace diffuse cones
            vec3 indirectDiffuse = vec3(0.0);
            int actualConeCount = min(_ConeCount, GI_CONE_COUNT_6);
            for (int i = 0; i < actualConeCount; i++)
            {
                vec3 localDir = GI_CONE_DIRS_6[i];
                vec3 coneDir = T * localDir.x + worldNormal * localDir.y + B * localDir.z;
                vec4 cone = TraceCone(worldPos + worldNormal * 0.05, coneDir, 1.0472);
                indirectDiffuse += cone.rgb * GI_CONE_WEIGHTS_6[i];
            }

            // Trace specular cone (narrow, in reflection direction)
            vec3 viewDir = normalize(worldPos - _WorldSpaceCameraPos.xyz);
            vec3 reflDir = reflect(viewDir, worldNormal);
            float roughness = texture(_GBufferC, TexCoords).r;
            float specConeAngle = mix(0.02, 0.5, roughness);
            vec4 indirectSpecular = TraceCone(worldPos + worldNormal * 0.05, reflDir, specConeAngle);

            outColor = vec4((indirectDiffuse + indirectSpecular.rgb) * _GIIntensity, 0.0);
        }
    }

    ENDGLSL
}
