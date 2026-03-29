Shader "Default/DeferredLighting"

Properties
{
}

Pass "DeferredLighting"
{
    Tags { "RenderOrder" = "Opaque" }

    // Fullscreen pass settings
    Cull None
    ZTest Off
    ZWrite Off

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
		#include "Lighting"

		layout (location = 0) out vec4 finalColor;

		in vec2 TexCoords;

		// GBuffer textures
		uniform sampler2D _GBufferA; // RGB = Albedo, A = AO
		uniform sampler2D _GBufferB; // RGB = Normal (view space), A = ShadingMode
		uniform sampler2D _GBufferC; // R = Roughness, G = Metalness, B = Specular, A = Unused
		uniform sampler2D _GBufferD; // Custom Data per Shading Mode (e.g., Emissive for Lit mode)
		uniform sampler2D _CameraDepthTexture; // Depth texture

		uniform sampler2D _ShadowAtlas;

		// Reconstruct world position from depth
		vec3 WorldPosFromDepth(float depth, vec2 texCoord) {
			// Convert depth to NDC Z
#ifdef PROWL_VULKAN
			float z = depth;  // Vulkan: gl_FragCoord.z is NDC Z directly [0,1]
#else
			float z = depth * 2.0 - 1.0;
#endif

			// Convert texCoord to NDC
			vec4 clipSpacePosition = vec4(texCoord * 2.0 - 1.0, z, 1.0);

			// Compute inverse view-projection matrix
			mat4 invVP = inverse(PROWL_MATRIX_VP);

			// Transform to world space
			vec4 worldSpacePosition = invVP * clipSpacePosition;

			// Perspective division
			worldSpacePosition /= worldSpacePosition.w;

			return worldSpacePosition.xyz;
		}

		void main()
		{
			// Sample GBuffer
			vec4 gbufferA = texture(_GBufferA, TexCoords);
			vec4 gbufferB = texture(_GBufferB, TexCoords);
			vec4 gbufferC = texture(_GBufferC, TexCoords);
			vec4 gbufferD = texture(_GBufferD, TexCoords);

			// Extract material properties
			vec3 albedo = gbufferA.rgb;
			float ao = gbufferA.a;

			// Decode normal from [0,1] to [-1,1] range
			vec3 viewNormal = gbufferB.rgb * 2.0 - 1.0;
			float shadingMode = gbufferB.a;

			float roughness = gbufferC.r;
			float metallic = gbufferC.g;
			float specular = gbufferC.b;

			vec3 emission = gbufferD.rgb;

			// Sample depth
			float depth = texture(_CameraDepthTexture, TexCoords).r;

			// Reconstruct world position
			vec3 worldPos = WorldPosFromDepth(depth, TexCoords);

			// Transform normal from view space to world space
			vec3 worldNormal = normalize(mat3(PROWL_MATRIX_I_V) * viewNormal);

			// Check shading mode
			// 0 = Unlit, 1 = Lit
			if (shadingMode < 0.5) {
				// Unlit mode - emission only
				finalColor = vec4(albedo + emission, 1.0);
				return;
			}

			// Lit mode - perform lighting calculations
			vec3 lighting = vec3(0.0);

			// Calculate base reflectivity for PBR
			vec3 F0 = vec3(0.04);
			F0 = mix(F0, albedo, metallic);

			// Get directional light from global uniforms
			SunLightStruct sun = GetDirectionalLight();
			lighting = CalculateDirectionalLight(sun, worldPos, worldNormal, _WorldSpaceCameraPos.xyz, albedo, metallic, roughness, ao, _ShadowAtlas, prowl_ShadowAtlasSize);

			// Add ambient lighting
			lighting += albedo.rgb * CalculateAmbient(worldNormal);

			// Add spot lights (max 4)
			for (int i = 0; i < prowl_SpotLightCount && i < 4; i++) {
				SpotLightStruct spotLight = GetSpotLight(i);
				lighting += CalculateSpotLight(spotLight, worldPos, worldNormal, _WorldSpaceCameraPos.xyz, albedo, metallic, roughness, ao, _ShadowAtlas, prowl_ShadowAtlasSize);
			}

			// Add point lights (max 4)
			for (int i = 0; i < prowl_PointLightCount && i < 4; i++) {
				PointLightStruct pointLight = GetPointLight(i);
				lighting += CalculatePointLight(pointLight, worldPos, worldNormal, _WorldSpaceCameraPos.xyz, albedo, metallic, roughness, ao, _ShadowAtlas, prowl_ShadowAtlasSize);
			}

			// Add emission
			lighting += emission;

			// Output final color
			finalColor = vec4(lighting, 1.0);
		}
	}

	ENDGLSL
}
