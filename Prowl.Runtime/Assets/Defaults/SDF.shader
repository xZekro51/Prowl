Shader "Default/SDF"

Properties
{
    _FontAtlas ("Font Atlas", Texture2D) = "white"
    _PxRange ("SDF Pixel Range", Float) = 4.0
    _FaceColor ("Face Color", Color) = (1.0, 1.0, 1.0, 1.0)
    _OutlineColor ("Outline Color", Color) = (0.0, 0.0, 0.0, 1.0)
    _OutlineWidth ("Outline Width", Float) = 0.0
    _Softness ("Softness", Float) = 0.0
    _UnderlayColor ("Underlay Color", Color) = (0.0, 0.0, 0.0, 0.5)
    _UnderlayOffsetX ("Underlay Offset X", Float) = 0.0
    _UnderlayOffsetY ("Underlay Offset Y", Float) = 0.0
    _UnderlayDilate ("Underlay Dilate", Float) = 0.0
    _UnderlaySoftness ("Underlay Softness", Float) = 0.0
}

Pass "SDFText"
{
    Tags { "RenderOrder" = "Transparent" }

    Cull Back
    ZWrite Off
    Blend Alpha

    GLSLPROGRAM

    Vertex
    {
        #include "Fragment"
        #include "VertexAttributes"

        out vec2 texCoord0;
        out vec2 texCoord1;
        out vec4 vColor;
        out vec3 worldPos;
        out vec4 currentPos;
        out vec4 previousPos;

        void main()
        {
            gl_Position = PROWL_MATRIX_MVP * vec4(vertexPosition, 1.0);
            currentPos = gl_Position;

            vec4 prevWorldPos = PROWL_MATRIX_M_PREVIOUS * vec4(vertexPosition, 1.0);
            previousPos = PROWL_MATRIX_VP_PREVIOUS * prevWorldPos;

            texCoord0 = vertexTexCoord0;
            texCoord1 = vertexTexCoord1;
            worldPos = (PROWL_MATRIX_M * vec4(vertexPosition, 1.0)).xyz;
            vColor = vertexColor;
        }
    }

    Fragment
    {
        #include "Fragment"

        layout (location = 0) out vec4 gBufferA;
        layout (location = 1) out vec4 gBufferB;
        layout (location = 2) out vec4 gBufferC;
        layout (location = 3) out vec4 gBufferD;

        in vec2 texCoord0;
        in vec2 texCoord1;
        in vec4 vColor;
        in vec3 worldPos;
        in vec4 currentPos;
        in vec4 previousPos;

        uniform sampler2D _FontAtlas;
        uniform float _PxRange;
        uniform vec4 _FaceColor;
        uniform vec4 _OutlineColor;
        uniform float _OutlineWidth;
        uniform float _Softness;
        uniform vec4 _UnderlayColor;
        uniform float _UnderlayOffsetX;
        uniform float _UnderlayOffsetY;
        uniform float _UnderlayDilate;
        uniform float _UnderlaySoftness;

        float median(float r, float g, float b)
        {
            return max(min(r, g), min(max(r, g), b));
        }

        float screenPxRange(vec2 uv)
        {
            vec2 unitRange = vec2(_PxRange) / vec2(textureSize(_FontAtlas, 0));
            vec2 screenTexSize = vec2(1.0) / fwidth(uv);
            return max(0.5 * dot(unitRange, screenTexSize), 1.0);
        }

        void main()
        {
            vec2 uv = texCoord0;

            // Sample the MSDF atlas
            vec3 msd = texture(_FontAtlas, uv).rgb;
            float sd = median(msd.r, msd.g, msd.b);

            // Convert to screen-space distance for resolution-independent AA
            float pxRange = screenPxRange(uv);
            float screenPxDist = pxRange * (sd - 0.5);

            // Face opacity with softness
            float softness = max(_Softness, 0.001);
            float faceOpacity = clamp(screenPxDist / softness + 0.5, 0.0, 1.0);

            // Outline
            float outlineOpacity = 0.0;
            if (_OutlineWidth > 0.0)
            {
                float outlineDist = pxRange * (sd - 0.5 + _OutlineWidth);
                outlineOpacity = clamp(outlineDist / softness + 0.5, 0.0, 1.0);
            }
            else
            {
                outlineOpacity = faceOpacity;
            }

            // Compose face + outline colors
            vec4 faceColor = _FaceColor * vColor;
            vec4 composited = mix(_OutlineColor, faceColor, faceOpacity);
            composited.a *= outlineOpacity;

            // Underlay (drop shadow)
            if (_UnderlayColor.a > 0.001)
            {
                vec2 underlayUV = uv - vec2(_UnderlayOffsetX, _UnderlayOffsetY) / vec2(textureSize(_FontAtlas, 0));
                vec3 underlayMsd = texture(_FontAtlas, underlayUV).rgb;
                float underlaySd = median(underlayMsd.r, underlayMsd.g, underlayMsd.b);
                float underlayDist = pxRange * (underlaySd - 0.5 + _UnderlayDilate);
                float underlaySoft = max(_UnderlaySoftness, softness);
                float underlayAlpha = clamp(underlayDist / underlaySoft + 0.5, 0.0, 1.0);

                vec4 underlayResult = _UnderlayColor;
                underlayResult.a *= underlayAlpha;

                // Composite underlay behind main text
                composited = vec4(
                    mix(underlayResult.rgb, composited.rgb, composited.a),
                    composited.a + underlayResult.a * (1.0 - composited.a)
                );
            }

            // Discard fully transparent pixels
            if (composited.a < 0.001)
                discard;

            // Convert to linear space
            vec3 baseColor = gammaToLinearSpace(composited.rgb);

            // View-space normal (face camera for text)
            vec3 viewNormal = vec3(0.0, 0.0, 1.0);

            // Output to GBuffer
            // BufferA: RGB = Albedo, A = Alpha (for blending)
            gBufferA = vec4(baseColor, composited.a);

            // BufferB: RGB = Normal (view space), A = ShadingMode
            // ShadingMode: 0 = Unlit
            gBufferB = vec4(viewNormal * 0.5 + 0.5, 0.0);

            // BufferC: R = Roughness, G = Metalness, B = Specular, A = Unused
            gBufferC = vec4(1.0, 0.0, 0.0, 0.0);

            // BufferD: For Unlit mode, emissive data
            gBufferD = vec4(baseColor * composited.a, 0.0);
        }
    }

    ENDGLSL
}
