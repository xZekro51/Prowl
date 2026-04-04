Shader "Default/DeferredCompose"

Properties
{
}

Pass "Compose"
{
    Tags { "RenderOrder" = "Opaque" }

    // Fullscreen pass settings
    Cull None
    ZTest Off
    ZWrite Off
    Blend Off

	GLSLPROGRAM

	Vertex
	{
		layout (location = 0) in vec3 vertexPosition;
		layout (location = 1) in vec2 vertexTexCoord;

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

		layout (location = 0) out vec4 finalColor;

		in vec2 TexCoords;

		// GBuffer textures
		uniform sampler2D _GBufferA; // RGB = Albedo, A = AO
		uniform sampler2D _GBufferB; // RGB = Normal (view space), A = ShadingMode
		uniform sampler2D _GBufferD; // Custom Data per Shading Mode (e.g., Emissive for Lit mode)
		uniform sampler2D _CameraDepthTexture; // Depth texture for fog

		// Light accumulation buffer
		uniform sampler2D _LightAccumulation;

		// Fog uniforms
		uniform vec4 _FogColor;
		uniform vec4 _FogParams; // x: density/sqrt(ln(2)) for Exp2, y: density/ln(2) for Exp, z: -1/(end-start) for Linear, w: end/(end-start) for Linear
		uniform vec3 _FogStates;  // x: linear enabled, y: exp enabled, z: exp2 enabled

		// Ambient lighting uniforms
		uniform vec2 _AmbientMode; // x: uniform, y: hemisphere
		uniform vec4 _AmbientColor;
		uniform vec4 _AmbientSkyColor;
		uniform vec4 _AmbientGroundColor;
		uniform float _AmbientStrength;

		// Global Illumination
		uniform float _GIActive; // 0.0 = use ambient, 1.0 = GI replaces ambient

		// Ambient Lighting
		vec3 CalculateAmbient(vec3 worldNormal)
		{
			vec3 ambient = vec3(0.0);

			// Uniform ambient
			ambient += _AmbientColor.rgb * _AmbientMode.x;

			// Hemisphere ambient
			float upDot = dot(worldNormal, vec3(0.0, 1.0, 0.0));
			ambient += mix(_AmbientGroundColor.rgb, _AmbientSkyColor.rgb, upDot * 0.5 + 0.5) * _AmbientMode.y;

			return ambient;
		}

		// Apply fog - fogCoord is the linear depth
		vec3 ApplyFog(float fogCoord, vec3 color) {
			// When no fog mode is active (all states = 0), return color unchanged
			if (_FogStates.x + _FogStates.y + _FogStates.z < 0.5)
				return color;
			float prowlFog = 0.0;
			prowlFog += (fogCoord * _FogParams.z + _FogParams.w) * _FogStates.x;
			prowlFog += exp2(-fogCoord * _FogParams.y) * _FogStates.y;
			prowlFog += exp2(-fogCoord * fogCoord * _FogParams.x * _FogParams.x) * _FogStates.z;
			return mix(_FogColor.rgb, color, clamp(prowlFog, 0.0, 1.0));
		}

		// Reconstruct world position from depth
		vec3 WorldPosFromDepth(float depth, vec2 texCoord) {
#ifdef PROWL_VULKAN
			float z = depth;  // Vulkan: gl_FragCoord.z is NDC Z directly [0,1]
#else
			float z = depth * 2.0 - 1.0;
#endif
			// Fullscreen passes use non-flipped viewport but the GBuffer was rendered with Y-flip.
			// We need to flip UV Y for correct NDC reconstruction.
			vec2 ndcXY = vec2(texCoord.x * 2.0 - 1.0, 1.0 - texCoord.y * 2.0);
			vec4 clipSpacePosition = vec4(ndcXY, z, 1.0);
			mat4 invVP = inverse(PROWL_MATRIX_VP);
			vec4 worldSpacePosition = invVP * clipSpacePosition;
			worldSpacePosition /= worldSpacePosition.w;
			return worldSpacePosition.xyz;
		}

		void main()
		{
			// Sample textures
			vec4 gbufferA = texture(_GBufferA, TexCoords);
			vec4 gbufferB = texture(_GBufferB, TexCoords);
			vec4 gbufferD = texture(_GBufferD, TexCoords);
			vec3 lightAccumulation = texture(_LightAccumulation, TexCoords).rgb;

			float shadingMode = gbufferB.a;

			// Extract albedo and ambient occlusion
			vec3 albedo = gbufferA.rgb;
			float ao = gbufferA.a;

			vec3 color;

			// Check shading mode
			// 0 = Unlit, 1 = Lit
			// Use threshold comparison to handle floating point precision
			if (shadingMode < 0.5) {
				// Unlit mode - use albedo + emission from GBuffer
				vec3 emission = gbufferD.rgb;
				color = albedo + emission;
			} else {
					// Lit mode - combine ambient + light accumulation + emissive
					vec3 worldNormal = normalize((inverse(transpose(PROWL_MATRIX_V)) * vec4(gbufferB.rgb * 2.0 - 1.0, 0.0)).xyz);

					vec3 ambient;
					if (_GIActive < 0.5) {
						// No GI: use scene ambient as before
						ambient = CalculateAmbient(worldNormal) * albedo * ao * _AmbientStrength;
					} else {
						// GI is active: indirect lighting is already in the accumulation buffer.
						// Reduce ambient to avoid double-counting indirect light, but keep
						// enough to prevent total darkness in areas the GI grid doesn't cover.
						ambient = CalculateAmbient(worldNormal) * albedo * ao * _AmbientStrength * 0.35;
					}

					color = ambient + lightAccumulation;
				}

			// Apply fog
			float depth = texture(_CameraDepthTexture, TexCoords).r;
            if(depth < 1.0)
            {
			    vec3 worldPos = WorldPosFromDepth(depth, TexCoords);
			    float fogCoord = length(worldPos - _WorldSpaceCameraPos.xyz);
			    color = ApplyFog(fogCoord, color);
            }

			finalColor = vec4(color, 1.0);
		}
	}

	ENDGLSL
}
