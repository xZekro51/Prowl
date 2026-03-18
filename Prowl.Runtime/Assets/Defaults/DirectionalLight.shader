Shader "Default/DirectionalLight"

Properties
{
}

Pass "DirectionalLight"
{
    Tags { "RenderOrder" = "Opaque" }

    // Fullscreen pass settings
    Cull None
    ZTest Off
    ZWrite Off
    Blend Additive  // Additive blending for light accumulation

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
		#include "PBR"
		#include "Shadow"

		layout (location = 0) out vec4 finalColor;

		in vec2 TexCoords;

		// GBuffer textures
		uniform sampler2D _GBufferA; // RGB = Albedo, A = AO
		uniform sampler2D _GBufferB; // RGB = Normal (view space), A = ShadingMode
		uniform sampler2D _GBufferC; // R = Roughness, G = Metalness, B = Specular, A = Unused
		uniform sampler2D _GBufferD; // Custom Data per Shading Mode (e.g., Emissive for Lit mode)
		uniform sampler2D _CameraDepthTexture; // Depth texture
		uniform sampler2D _ShadowAtlas;

		// Directional light uniforms
		uniform vec3 _LightDirection;
		uniform vec4 _LightColor;
		uniform float _LightIntensity;
		uniform float _ShadowBias;
		uniform float _ShadowNormalBias;
		uniform float _ShadowStrength;
		uniform float _ShadowQuality;

		// Cascade shadow mapping uniforms
		uniform int _CascadeCount;
		uniform mat4 _CascadeShadowMatrix0;
		uniform mat4 _CascadeShadowMatrix1;
		uniform mat4 _CascadeShadowMatrix2;
		uniform mat4 _CascadeShadowMatrix3;
		uniform vec4 _CascadeAtlasParams0; // xy: atlasPos, z: atlasSize, w: splitDistance
		uniform vec4 _CascadeAtlasParams1;
		uniform vec4 _CascadeAtlasParams2;
		uniform vec4 _CascadeAtlasParams3;

		// Sample directional light shadow with cascaded shadow mapping
		float SampleShadow(vec3 worldPos, vec3 worldNormal)
		{
			if (_CascadeCount == 0) {
				return 0.0; // No shadows
			}
			// Calculate distance to select cascade
			float worldDistance = distance(worldPos, _WorldSpaceCameraPos.xyz);

			// Select appropriate cascade based on view depth
			// Pick the first cascade whose split distance contains this depth
			int cascadeIndex = _CascadeCount - 1; // Default to last cascade
			mat4 cascadeMatrix;
			vec4 cascadeParams;

			// Check cascades in order and pick the first one that covers this depth
			if (_CascadeCount >= 1 && worldDistance <= _CascadeAtlasParams0.w) {
				cascadeIndex = 0;
				cascadeMatrix = _CascadeShadowMatrix0;
				cascadeParams = _CascadeAtlasParams0;
			} else if (_CascadeCount >= 2 && worldDistance <= _CascadeAtlasParams1.w) {
				cascadeIndex = 1;
				cascadeMatrix = _CascadeShadowMatrix1;
				cascadeParams = _CascadeAtlasParams1;
			} else if (_CascadeCount >= 3 && worldDistance <= _CascadeAtlasParams2.w) {
				cascadeIndex = 2;
				cascadeMatrix = _CascadeShadowMatrix2;
				cascadeParams = _CascadeAtlasParams2;
			} else if (_CascadeCount >= 4 && worldDistance <= _CascadeAtlasParams3.w) {
				cascadeIndex = 3;
				cascadeMatrix = _CascadeShadowMatrix3;
				cascadeParams = _CascadeAtlasParams3;
			} else {
				// Beyond all cascades - use the last one
				if (_CascadeCount == 1) {
					cascadeMatrix = _CascadeShadowMatrix0;
					cascadeParams = _CascadeAtlasParams0;
				} else if (_CascadeCount == 2) {
					cascadeMatrix = _CascadeShadowMatrix1;
					cascadeParams = _CascadeAtlasParams1;
				} else if (_CascadeCount == 3) {
					cascadeMatrix = _CascadeShadowMatrix2;
					cascadeParams = _CascadeAtlasParams2;
				} else {
					cascadeMatrix = _CascadeShadowMatrix3;
					cascadeParams = _CascadeAtlasParams3;
				}
			}

			// Check if cascade has valid atlas coordinates
			if (cascadeParams.z <= 0.0) {
				return 0.0; // No shadow map for this cascade
			}

			// Apply normal bias
			vec3 worldPosBiased = worldPos + (normalize(worldNormal) * _ShadowNormalBias);
			vec4 lightSpacePos = cascadeMatrix * vec4(worldPosBiased, 1.0);
			vec3 projCoords = lightSpacePos.xyz / lightSpacePos.w;
			projCoords = projCoords * 0.5 + 0.5;

			// Early exit if outside shadow map
			if (projCoords.z > 1.0 || projCoords.x < 0.0 || projCoords.x > 1.0 || projCoords.y < 0.0 || projCoords.y > 1.0) {
				return 0.0;
			}

			// Get shadow atlas coordinates using common helper
			vec2 shadowAtlasSize = vec2(textureSize(_ShadowAtlas, 0));
			float atlasSize = shadowAtlasSize.x;
			vec2 atlasCoords, shadowMin, shadowMax;
			GetAtlasCoordinates(projCoords, cascadeParams, atlasSize, atlasCoords, shadowMin, shadowMax);

			// Calculate bias with slope-based adjustment (matches SpotLight pattern)
			float slopeBias = CalculateSlopeBias(worldNormal, _LightDirection, _ShadowBias);
			float currentDepth = projCoords.z - slopeBias;

			// Sample shadow using common PCF helper
			return SampleShadowPCF(_ShadowAtlas, atlasCoords, shadowMin, shadowMax,
			                       currentDepth, _ShadowQuality, _ShadowStrength);
		}

		// Calculate directional light contribution
		vec3 CalculateDirectionalLight(vec3 worldPos, vec3 worldNormal, vec3 cameraPos, vec3 albedo, float metallic, float roughness, float ao)
		{
			vec3 lightDir = normalize(_LightDirection);
			vec3 viewDir = normalize(-(worldPos - cameraPos));
			vec3 halfDir = normalize(lightDir + viewDir);

			// Calculate base reflectivity for PBR
			vec3 F0 = vec3(0.04);
			F0 = mix(F0, albedo, metallic);

			// Light radiance
			vec3 radiance = _LightColor.rgb * _LightIntensity;

			// Cook-Torrance BRDF
			float NDF = DistributionGGX(worldNormal, halfDir, roughness);
			float G = GeometrySmith(worldNormal, viewDir, lightDir, roughness);
			vec3 F = FresnelSchlick(max(dot(halfDir, viewDir), 0.0), F0);

			// Specular and diffuse
			vec3 kS = F;
			vec3 kD = vec3(1.0) - kS;
			kD *= 1.0 - metallic;

			float NdotL = max(dot(worldNormal, lightDir), 0.0);

			// Specular term
			vec3 numerator = NDF * G * F;
			float denominator = 4.0 * max(dot(worldNormal, viewDir), 0.0) * NdotL + 0.0001;
			vec3 specular = numerator / denominator;

			// Calculate shadow
			float shadow = SampleShadow(worldPos, worldNormal);
			float shadowFactor = 1.0 - shadow;

			// Final lighting
			vec3 diffuse = kD * albedo / PI;
			return (diffuse + specular) * radiance * NdotL * shadowFactor * ao;
		}

		void main()
		{
			// Sample GBuffer
			vec4 gbufferA = texture(_GBufferA, TexCoords);
			vec4 gbufferB = texture(_GBufferB, TexCoords);
			vec4 gbufferC = texture(_GBufferC, TexCoords);

			// Extract material properties
			vec3 albedo = gbufferA.rgb;
			float ao = gbufferA.a;

			// Decode normal from [0,1] to [-1,1] range
			vec3 viewNormal = gbufferB.rgb * 2.0 - 1.0;
			float shadingMode = gbufferB.a;

			float roughness = gbufferC.r;
			float metallic = gbufferC.g;

			// Sample depth
			float depth = texture(_CameraDepthTexture, TexCoords).r;

			// Reconstruct world position
			vec3 worldPos = WorldPosFromDepth(depth, TexCoords);

			// Transform normal from view space to world space
			vec3 worldNormal = normalize(mat3(PROWL_MATRIX_I_V) * viewNormal);

			// Check shading mode (0 = Unlit, 1 = Lit)
			if (shadingMode != 1.0) {
				finalColor = vec4(0.0, 0.0, 0.0, 0.0);
				return;
			}

			// Calculate directional light contribution only
			// Note: Ambient lighting is applied in the DeferredCompose shader
			vec3 lighting = CalculateDirectionalLight(worldPos, worldNormal, _WorldSpaceCameraPos.xyz, albedo, metallic, roughness, ao);

			finalColor = vec4(lighting, 1.0);
		}
	}

	ENDGLSL
}
