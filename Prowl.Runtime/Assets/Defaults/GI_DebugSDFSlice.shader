Shader "Default/GI_DebugSDFSlice"

Pass "DebugSDFSlice"
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

        uniform sampler3D _SDFCascade0;
        uniform vec3 _CascadeCenter0;
        uniform float _CascadeSize0;
        uniform float _SliceY; // Normalized Y position [0,1] within cascade

        void main()
        {
            // Map screen UV to cascade XZ, use _SliceY for Y
            vec3 uvw = vec3(TexCoords.x, _SliceY, TexCoords.y);

            float dist = texture(_SDFCascade0, uvw).r;

            // Visualize: blue = far from surface, white = near surface, red = very close
            float normalizedDist = clamp(dist / (_CascadeSize0 * 0.5), 0.0, 1.0);

            vec3 color;
            if (normalizedDist < 0.05)
                color = vec3(1.0, 0.2, 0.2); // Near surface — red
            else if (normalizedDist < 0.3)
                color = vec3(1.0, 1.0, 1.0); // Close — white
            else
                color = mix(vec3(0.0, 0.3, 0.8), vec3(0.0, 0.0, 0.1), normalizedDist); // Far — blue gradient

            outColor = vec4(color, 1.0);
        }
    }

    ENDGLSL
}
