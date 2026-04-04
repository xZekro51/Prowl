Shader "Default/SDFUI"

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
    _ScreenProjection ("Screen Projection", Matrix)
}

Pass "SDFUIText"
{
    Tags { "RenderOrder" = "Transparent" }

    Cull Off
    ZWrite Off
    ZTest Off
    Blend {
        Src One
        Dst OneMinusSrcAlpha
        Mode Add
    }

    GLSLPROGRAM

    Vertex
    {
        layout (location = 0) in vec3 aPosition;
        layout (location = 1) in vec2 aTexCoord0;
        layout (location = 2) in vec2 aTexCoord1;
        // location 3 = Normal (VertexSemantic.Normal = 3), unused by this shader
        layout (location = 4) in vec4 aColor;

        uniform mat4 _ScreenProjection;

        layout(location = 0) out vec2 texCoord0;
        layout(location = 1) out vec2 texCoord1;
        layout(location = 2) out vec4 vColor;

        void main()
        {
            gl_Position = _ScreenProjection * vec4(aPosition, 1.0);
            texCoord0 = aTexCoord0;
            texCoord1 = aTexCoord1;
            vColor = aColor;
        }
    }

    Fragment
    {
        layout(location = 0) in vec2 texCoord0;
        layout(location = 1) in vec2 texCoord1;
        layout(location = 2) in vec4 vColor;

        layout(location = 0) out vec4 finalColor;

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

            // Premultiply alpha for correct blending
            finalColor = vec4(composited.rgb * composited.a, composited.a);
        }
    }

    ENDGLSL
}
