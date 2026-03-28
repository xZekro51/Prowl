Shader "Default/SpotLight"

Properties
{
}

Pass "SpotLight"
{
    Tags { "RenderOrder" = "Opaque" }

    // Cone volume settings
    Cull Front  // Cull front faces so we only render back faces (inside of cone)
    ZTest Greater  // Only render where cone is in front of geometry
    ZWrite Off
    Blend Additive  // Additive blending for light accumulation

	GLSLPROGRAM

	Vertex
	{
		#include "Fragment"

		layout (location = 0) in vec3 vertexPosition;

		void main()
		{
			// Transform cone vertex using model-view-projection matrices
			vec4 worldPos = prowl_ObjectToWorld * vec4(vertexPosition, 1.0);
			gl_Position = PROWL_MATRIX_VP * worldPos;
		}
	}

	Fragment
	{
		#include "Fragment"
		#include "PBR"
		#include "Shadow"

		layout (location = 0) out vec4 finalColor;

		// GBuffer textures
		uniform sampler2D _GBufferA; // RGB = Albedo, A = AO
		uniform sampler2D _GBufferB; // RGB = Normal (view space), A = ShadingMode
		uniform sampler2D _GBufferC; // R = Roughness, G = Metalness, B = Specular, A = Unused
		uniform sampler2D _GBufferD; // Custom Data per Shading Mode (e.g., Emissive for Lit mode)
		uniform sampler2D _CameraDepthTexture; // Depth texture
		uniform sampler2D _ShadowAtlas;

		// Spot light uniforms
		uniform vec3 _LightPosition;
		uniform vec3 _LightDirection;
		uniform vec4 _LightColor;
		uniform float _LightIntensity;
		uniform float _LightRange;
		uniform float _SpotAngle;
		uniform float _InnerSpotAngle;
		uniform float _ShadowBias;
		uniform float _ShadowNormalBias;
		uniform float _ShadowStrength;
		uniform float _ShadowQuality;

		// Shadow mapping uniforms
		uniform mat4 _ShadowMatrix;
		uniform vec4 _ShadowAtlasParams; // xy: atlasPos, z: atlasSize, w: unused

		// Sample spot light shadow with perspective shadow mapping
		float SampleShadow(vec3 worldPos, vec3 worldNormal)
		{
			// Check if shadow map is valid
			if (_ShadowAtlasParams.z <= 0.0) {
				return 0.0; // No shadow map
			}

			// Apply normal bias
			vec3 worldPosBiased = worldPos + (normalize(worldNormal) * _ShadowNormalBias);
			vec4 lightSpacePos = _ShadowMatrix * vec4(worldPosBiased, 1.0);
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
			GetAtlasCoordinates(projCoords, _ShadowAtlasParams, atlasSize, atlasCoords, shadowMin, shadowMax);

			// Calculate bias for spot light
			float finalBias = CalculateSlopeBias(worldNormal, _LightDirection, _ShadowBias);
			float currentDepth = projCoords.z - finalBias;

			// Sample shadow using common PCF helper
			return SampleShadowPCF(_ShadowAtlas, atlasCoords, shadowMin, shadowMax,
			                       currentDepth, _ShadowQuality, _ShadowStrength);
		}

		// Calculate spot light contribution
		vec3 CalculateSpotLight(vec3 worldPos, vec3 worldNormal, vec3 cameraPos, vec3 albedo, float metallic, float roughness, float ao)
		{
			vec3 lightToPixel = worldPos - _LightPosition;
			float distance = length(lightToPixel);
			vec3 lightDir = normalize(-lightToPixel);
			vec3 viewDir = normalize(-(worldPos - cameraPos));
			vec3 halfDir = normalize(lightDir + viewDir);

			// Distance attenuation (inverse square law with smoothstep at range)
			float distanceAttenuation = 1.0 / (distance * distance + 1.0);
			float rangeAttenuation = 1.0 - smoothstep(_LightRange * 0.8, _LightRange, distance);
			float attenuation = distanceAttenuation * rangeAttenuation;

			// Spot light cone attenuation (smooth falloff from inner to outer angle)
			float spotAngleRad = radians(_SpotAngle);
			float innerSpotAngleRad = radians(_InnerSpotAngle);
			float lightAngleCos = dot(normalize(_LightDirection), normalize(lightToPixel));
			float outerCos = cos(spotAngleRad);
			float innerCos = cos(innerSpotAngleRad);
			float spotAttenuation = smoothstep(outerCos, innerCos, lightAngleCos);
			attenuation *= spotAttenuation;

			// Early exit if outside spot cone or range
			if (attenuation <= 0.0001) {
				return vec3(0.0);
			}

			// Calculate base reflectivity for PBR
			vec3 F0 = vec3(0.04);
			F0 = mix(F0, albedo, metallic);

			// Light radiance with attenuation
			vec3 radiance = _LightColor.rgb * _LightIntensity * attenuation;

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
			// Calculate screen-space texture coordinates from fragment position
			vec2 TexCoords = gl_FragCoord.xy / _ScreenParams.xy;

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
			// Use threshold comparison to handle floating point precision
			if (shadingMode < 0.5) {
				finalColor = vec4(0.0, 0.0, 0.0, 0.0);
				return;
			}

			// Calculate spot light contribution
			vec3 lighting = CalculateSpotLight(worldPos, worldNormal, _WorldSpaceCameraPos.xyz, albedo, metallic, roughness, ao);

			finalColor = vec4(lighting, 1.0);
		}
	}

	ENDGLSL
}
