Shader "Paper/UI"

Properties
{
}

Pass "UI"
{
    Tags { "RenderOrder" = "Opaque" }

    // Set up blending for UI elements
    Blend Alpha
    ZTest Off
    ZWrite Off
    Cull Off

    GLSLPROGRAM

    Vertex
    {
        layout (location = 0) in vec2 aPosition;
        layout (location = 1) in vec2 aTexCoord;
        layout (location = 2) in vec4 aColor;

#if __VERSION__ >= 450
        layout(std140, set = 0, binding = 0) uniform UIUniforms
#else
        layout(std140) uniform UIUniforms
#endif
        {
            mat4 projection;
            mat4 scissorMat;
            vec4 scissorExtPad;
            mat4 brushMat;
            vec4 brushTypePad;
            vec4 brushColor1;
            vec4 brushColor2;
            vec4 brushParams;
            vec4 brushParams2Pad;
        };

        layout(location = 0) out vec2 fragTexCoord;
        layout(location = 1) out vec4 fragColor;
        layout(location = 2) out vec2 fragPos;

        void main()
        {
            fragTexCoord = aTexCoord;
            fragColor = aColor;
            fragPos = aPosition;
            gl_Position = projection * vec4(aPosition, 0.0, 1.0);
        }
    }

    Fragment
    {
        layout(location = 0) in vec2 fragTexCoord;
        layout(location = 1) in vec4 fragColor;
        layout(location = 2) in vec2 fragPos;

        layout(location = 0) out vec4 finalColor;

#if __VERSION__ >= 450
        layout(set = 1, binding = 0) uniform sampler2D texture0;
#else
        uniform sampler2D texture0;
#endif

#if __VERSION__ >= 450
        layout(std140, set = 0, binding = 0) uniform UIUniforms
#else
        layout(std140) uniform UIUniforms
#endif
        {
            mat4 projection;
            mat4 scissorMat;
            vec4 scissorExtPad;
            mat4 brushMat;
            vec4 brushTypePad;
            vec4 brushColor1;
            vec4 brushColor2;
            vec4 brushParams;
            vec4 brushParams2Pad;
        };

        float calculateBrushFactor() {
            int brushType = int(brushTypePad.x);

            // No brush
            if (brushType == 0) return 0.0;

            vec2 transformedPoint = (brushMat * vec4(fragPos, 0.0, 1.0)).xy;

            // Linear brush - projects position onto the line between start and end
            if (brushType == 1) {
                vec2 startPoint = brushParams.xy;
                vec2 endPoint = brushParams.zw;
                vec2 line = endPoint - startPoint;
                float lineLength = length(line);

                if (lineLength < 0.001) return 0.0;

                vec2 posToStart = transformedPoint - startPoint;
                float proj = dot(posToStart, line) / (lineLength * lineLength);
                return clamp(proj, 0.0, 1.0);
            }

            // Radial brush - based on distance from center
            if (brushType == 2) {
                vec2 center = brushParams.xy;
                float innerRadius = brushParams.z;
                float outerRadius = brushParams.w;

                if (outerRadius < 0.001) return 0.0;

                float distance = smoothstep(innerRadius, outerRadius, length(transformedPoint - center));
                return clamp(distance, 0.0, 1.0);
            }

            // Box brush - like radial but uses max distance in x or y direction
            if (brushType == 3) {
                vec2 center = brushParams.xy;
                vec2 halfSize = brushParams.zw;
                float radius = brushParams2Pad.x;
                float feather = brushParams2Pad.y;

                if (halfSize.x < 0.001 || halfSize.y < 0.001) return 0.0;

                // Calculate distance from center (normalized by half-size)
                vec2 q = abs(transformedPoint - center) - (halfSize - vec2(radius));

                // Distance field calculation for rounded rectangle
                float dist = min(max(q.x,q.y),0.0) + length(max(q,0.0)) - radius;

                return clamp((dist + feather * 0.5) / feather, 0.0, 1.0);
            }

            return 0.0;
        }

        // Determines whether a point is within the scissor region and returns the appropriate mask value
        float scissorMask(vec2 p) {
            vec2 scissorExt = scissorExtPad.xy;

            // Early exit if scissoring is disabled (when any scissor dimension is negative)
            if(scissorExt.x < 0.0 || scissorExt.y < 0.0) return 1.0;

            // Transform point to scissor space
            vec2 transformedPoint = (scissorMat * vec4(p, 0.0, 1.0)).xy;

            // Calculate signed distance from scissor edges (negative inside, positive outside)
            vec2 distanceFromEdges = abs(transformedPoint) - scissorExt;

            // Apply offset for smooth edge transition (0.5 creates half-pixel anti-aliased edges)
            vec2 smoothEdges = vec2(0.5, 0.5) - distanceFromEdges;

            // Clamp each component and multiply to get final mask value
            // Result is 1.0 inside, 0.0 outside, with smooth transition at edges
            return clamp(smoothEdges.x, 0.0, 1.0) * clamp(smoothEdges.y, 0.0, 1.0);
        }

        void main()
        {
            int brushType = int(brushTypePad.x);

            vec2 pixelSize = fwidth(fragTexCoord);
            vec2 edgeDistance = min(fragTexCoord, 1.0 - fragTexCoord);
            float edgeAlpha = smoothstep(0.0, pixelSize.x, edgeDistance.x) * smoothstep(0.0, pixelSize.y, edgeDistance.y);
            edgeAlpha = clamp(edgeAlpha, 0.0, 1.0);

            float mask = scissorMask(fragPos);
            vec4 color = fragColor;

            // Apply brush if active
            if (brushType > 0) {
                float factor = calculateBrushFactor();
                color = mix(brushColor1, brushColor2, factor);
            }

            vec4 textureColor = texture(texture0, fragTexCoord);
            color *= textureColor;
            color *= edgeAlpha * mask;
            finalColor = color;
        }
    }

    ENDGLSL
}
