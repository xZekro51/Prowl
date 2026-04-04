Shader "Default/SDFGI_ProbeTrace"

Pass "ProbeLookup"
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

        // Per-cascade probe grids (up to 4 cascades)
        uniform sampler3D _ProbeIrradiance0;
        uniform sampler3D _ProbeIrradiance1;
        uniform sampler3D _ProbeIrradiance2;
        uniform sampler3D _ProbeIrradiance3;

        uniform vec3 _CascadeCenter0;
        uniform vec3 _CascadeCenter1;
        uniform vec3 _CascadeCenter2;
        uniform vec3 _CascadeCenter3;

        uniform float _CascadeSize0;
        uniform float _CascadeSize1;
        uniform float _CascadeSize2;
        uniform float _CascadeSize3;

        uniform int _CascadeCount;
        uniform int _ProbeResolution;

        uniform sampler2D _GBufferB;
        uniform sampler2D _CameraDepthTexture;
        uniform float _GIIntensity;

        vec4 SampleCascadeProbe(int cascadeIndex, vec3 uvw)
        {
            if (cascadeIndex == 0) return texture(_ProbeIrradiance0, uvw);
            if (cascadeIndex == 1) return texture(_ProbeIrradiance1, uvw);
            if (cascadeIndex == 2) return texture(_ProbeIrradiance2, uvw);
            return texture(_ProbeIrradiance3, uvw);
        }

        vec3 GetCascadeCenter(int idx)
        {
            if (idx == 0) return _CascadeCenter0;
            if (idx == 1) return _CascadeCenter1;
            if (idx == 2) return _CascadeCenter2;
            return _CascadeCenter3;
        }

        float GetCascadeSize(int idx)
        {
            if (idx == 0) return _CascadeSize0;
            if (idx == 1) return _CascadeSize1;
            if (idx == 2) return _CascadeSize2;
            return _CascadeSize3;
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

            // Find the tightest cascade containing this point
            vec3 irradiance = vec3(0.0);
            for (int c = 0; c < _CascadeCount && c < 4; c++)
            {
                vec3 center = GetCascadeCenter(c);
                float size = GetCascadeSize(c);
                vec3 localPos = (worldPos - center) / (size * 2.0) + 0.5;

                if (IsInsideVolumeMargin(localPos, 0.05))
                {
                    vec4 sh = SampleCascadeProbe(c, localPos);
                    irradiance = SHDecodeL1(sh, worldNormal);
                    break;
                }
            }

            outColor = vec4(irradiance * _GIIntensity, 0.0);
        }
    }

    ENDGLSL
}
