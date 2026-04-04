Shader "Default/GI_TemporalBlend"

Pass "TemporalBlend"
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

        layout(location = 0) out vec4 outColor;
        in vec2 TexCoords;

        uniform sampler2D _MainTex;      // Current frame GI
        uniform sampler2D _PreviousGI;   // Previous frame GI (reprojected)
        uniform float _BlendFactor;      // 0.0 = all current, 1.0 = all previous

        void main()
        {
            vec4 current = texture(_MainTex, TexCoords);
            vec4 previous = texture(_PreviousGI, TexCoords);

            // Simple temporal blend without reprojection.
            // Reject history when it differs too much from the current frame
            // (e.g., on camera cuts or fast motion) by clamping.
            float lumCurrent  = dot(current.rgb, vec3(0.2126, 0.7152, 0.0722));
            float lumPrevious = dot(previous.rgb, vec3(0.2126, 0.7152, 0.0722));
            float diff = abs(lumCurrent - lumPrevious);

            // Reduce blend factor when luminance difference is large
            float adaptiveBlend = _BlendFactor * (1.0 - smoothstep(0.1, 0.5, diff));

            outColor = mix(current, previous, adaptiveBlend);
        }
    }

    ENDGLSL
}
